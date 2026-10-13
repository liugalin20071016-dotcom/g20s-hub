using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using G20SHub.Native;
using static G20SHub.Native.Win32;

namespace G20SHub.Input;

enum InputKind { Consumer, Keyboard, System, Paused }

/// <summary>
/// 输入事件。Consumer：Code = usage；Keyboard：Code = 键码（见 KeyboardKeys）；
/// System：Code = System Control usage（⏻）；Paused：Down = 是否暂停。
/// </summary>
sealed record InputEvent(InputKind Kind, int Code, bool Down, double Ts);

/// <summary>
/// 专用输入线程：隐藏窗口接收 Raw Input + 低级键盘钩子 + 自己的消息循环。
/// 与界面线程分开，界面卡顿不会拖慢钩子（钩子超时会被系统摘掉）。
/// </summary>
sealed class InputThread
{
    const string WindowClass = "G20SHubInput";
    const int VK_F12 = 0x7B;

    public readonly ConsumerTracker Consumer = new();
    public readonly SystemControl SystemKeys = new();
    public readonly MediaKeyFilter Media;
    public readonly KeyboardKeys Keys = new();

    /// <summary>在输入线程上触发，订阅方需自行切换线程。</summary>
    public event Action<InputEvent> Event;

    public volatile bool Paused;
    public int DrainedInHook;

    /// <summary>语音输入正按住 Ctrl+Win（由界面线程设置）。</summary>
    public volatile bool VoiceLatched;

    /// <summary>语音按住期间被扣下的按键：VK、扫描码、是否扩展键（输入线程上触发）。</summary>
    public event Action<ushort, ushort, bool> LatchedKey;

    readonly HashSet<uint> swallowUps = new();

    readonly bool dryRun;
    readonly ManualResetEventSlim ready = new();
    readonly Dictionary<IntPtr, (bool remote, ushort page, ushort usage)> devices = new();
    Thread thread;
    uint threadId;
    IntPtr hwnd, hook, rawBuf;
    uint rawBufSize;
    bool swallowF12Up;
    string startError;

    // 委托必须一直被引用，否则会被 GC 回收导致崩溃
    LowLevelKeyboardProc hookProc;
    WndProc wndProc;

    public InputThread(Config cfg, bool dryRun)
    {
        this.dryRun = dryRun;
        Media = new MediaKeyFilter(Consumer, cfg.Num("timing.match_ms", 150), 50) { DryRun = dryRun };
        Keys.DryRun = dryRun;
        Consumer.Changed += OnConsumer;
        SystemKeys.Changed += (usage, down, ts) => Event?.Invoke(new InputEvent(InputKind.System, usage, down, ts));
        Keys.Key += (code, down, ts) =>
        {
            Log.Write($"[钩子] {KeyName(code)} {(down ? "按下" : "抬起")} → 接管{(dryRun ? "（dry-run，未拦截）" : "")}");
            Event?.Invoke(new InputEvent(InputKind.Keyboard, code, down, ts));
        };
    }

    static string KeyName(int code) => code switch
    {
        KeyboardKeys.Apps => "☰菜单",
        KeyboardKeys.NumpadEnter => "OK",
        KeyboardKeys.Up => "↑",
        KeyboardKeys.Down => "↓",
        KeyboardKeys.Left => "←",
        KeyboardKeys.Right => "→",
        0x08 => "DEL",
        0x0D => "Enter",
        _ => $"VK 0x{code:X2}",
    };

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "G20S-Input" };
        thread.Start();
        if (!ready.Wait(3000))
            throw new InvalidOperationException("输入线程启动超时");
        if (startError != null)
            throw new InvalidOperationException(startError);
        SystemKeys.Start();  // ⏻ 电源键接口不走 Raw Input，单独一个线程直接读
    }

    // ---------------- 紧急重启（🔇 / 托盘「重启」） ----------------

    const uint WM_APP_PANIC = 0x8000 + 1;

    /// <summary>重启时启动的 exe（固定用 bin\G20SHub.exe，确保是最新编译的版本）。为 null 时不启用 🔇 紧急重启。</summary>
    public string RestartExe;

    /// <summary>从其他线程请求紧急重启（转到输入线程执行）。</summary>
    public void RequestPanic(string why)
    {
        panicWhy = why;
        PostThreadMessage(threadId, WM_APP_PANIC, IntPtr.Zero, IntPtr.Zero);
    }

    string panicWhy = "托盘";
    bool panicking;

    /// <summary>
    /// 紧急重启：在输入线程上直接执行，不依赖界面线程（界面卡死时也有效）。
    /// 1) 松开所有修饰键（也就结束了按住 Ctrl+Win 的语音输入）2) 摘掉键盘钩子，不再拦截任何键
    /// 3) 启动新实例接管 4) 2.5 秒内本实例没正常退出就强制结束，保证新实例能拿到单实例锁。
    /// </summary>
    void Panic(string why)
    {
        if (panicking)
            return;
        panicking = true;
        Log.Write($"🛑 紧急重启（{why}）：松开所有按键、停止拦截、重启中枢");
        ReleaseAllKeys();
        // 钩子先保留到退出：只继续吞掉遥控器的媒体键（否则这一下 🔇 会漏给系统、切换静音），其他键一律放行（见 Decide）
        try
        {
            Process.Start(new ProcessStartInfo(RestartExe, "--restarted") { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Log.Write("!! 重启失败：" + e.Message);
        }
        new Thread(() =>
        {
            Thread.Sleep(2500);
            Log.Write("!! 旧实例没有按时退出，强制结束");
            Log.Close();
            Process.GetCurrentProcess().Kill();
        }) { IsBackground = true, Name = "G20S-PanicWatchdog" }.Start();
    }

    /// <summary>给所有修饰键发「松开」（未按下的键收到松开不会有副作用）；松开 Win 前先发遮罩键，防止弹出开始菜单。</summary>
    static void ReleaseAllKeys()
    {
        ushort[] keys = { 0xE8, 0xA2, 0xA3, 0xA0, 0xA1, 0xA4, 0xA5, 0x5B, 0x5C, 0x11, 0x10, 0x12 };
        var inputs = new List<INPUT>();
        inputs.Add(KeyInput(0xE8, false));  // 遮罩键：按下
        foreach (var vk in keys)
            inputs.Add(KeyInput(vk, true));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (vk is 0xA3 or 0xA5 or 0x5B or 0x5C ? KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = OwnInjectSignature,
            },
        },
    };

    /// <summary>暂停 / 恢复（托盘菜单用；真键盘 Ctrl+Alt+F12 效果相同）。</summary>
    public void TogglePause()
    {
        Paused = !Paused;
        Log.Write(Paused ? "⏸ 已暂停：遥控器全部恢复系统默认" : "▶ 已恢复");
        Event?.Invoke(new InputEvent(InputKind.Paused, 0, Paused, Clock.Ms));
    }

    public void Stop()
    {
        if (threadId != 0)
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread?.Join(1000);
    }

    void Run()
    {
        try
        {
            threadId = GetCurrentThreadId();
            var hInst = GetModuleHandle(null);
            rawBufSize = 1024;
            rawBuf = Marshal.AllocHGlobal((int)rawBufSize);

            wndProc = OnWindowMessage;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = wndProc,
                hInstance = hInst,
                lpszClassName = WindowClass,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx 失败 {Marshal.GetLastWin32Error()}");
            hwnd = CreateWindowEx(0, WindowClass, WindowClass, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx 失败 {Marshal.GetLastWin32Error()}");

            RegisterRaw(0x0C, 0x01, "Consumer");
            RegisterRaw(0x01, 0x06, "键盘（只记录遥控器）");

            hookProc = OnHook;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, hInst, 0);
            if (hook == IntPtr.Zero)
                throw new InvalidOperationException($"SetWindowsHookEx 失败 {Marshal.GetLastWin32Error()}");

            Log.Write($"输入线程就绪：Raw Input + 低级键盘钩子{(dryRun ? "（dry-run：只记录不拦截）" : "")}");
            ready.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_PANIC)
                {
                    Panic(panicWhy);
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception e)
        {
            startError = e.Message;
            Log.Write("!! 输入线程异常: " + e);
        }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            if (rawBuf != IntPtr.Zero) Marshal.FreeHGlobal(rawBuf);
            hook = hwnd = rawBuf = IntPtr.Zero;
            ready.Set();
        }
    }

    void RegisterRaw(ushort page, ushort usage, string what)
    {
        var dev = new[] { new RAWINPUTDEVICE { usUsagePage = page, usUsage = usage, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write($"!! Raw Input 注册失败：{what} {Marshal.GetLastWin32Error()}");
    }

    // ---------------- Raw Input ----------------

    IntPtr OnWindowMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { ProcessRaw(lParam); }
            catch (Exception e) { Log.Write("!! Raw Input 处理异常: " + e.Message); }
        }
        else if (msg == WM_TIMER && (uint)wParam == TimerMute)
        {
            OnMuteTimer();
            return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    /// <summary>
    /// 钩子回调按「发送消息」处理，可能排在已投递但还没处理的 WM_INPUT 前面，
    /// 所以判定前先把队列里的 WM_INPUT 取出来处理掉。只取输入类消息，不会重入钩子。
    /// </summary>
    bool DrainRaw()
    {
        bool any = false;
        while (PeekMessage(out var m, hwnd, WM_INPUT, WM_INPUT, PM_REMOVE | PM_QS_INPUT))
        {
            any = true;
            DrainedInHook++;
            ProcessRaw(m.lParam);
            DefWindowProc(hwnd, m.message, m.wParam, m.lParam);
        }
        return any;
    }

    void ProcessRaw(IntPtr hRaw)
    {
        uint hdr = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, hdr);
        if (size > rawBufSize)
        {
            Marshal.FreeHGlobal(rawBuf);
            rawBufSize = size;
            rawBuf = Marshal.AllocHGlobal((int)size);
        }
        if (GetRawInputData(hRaw, RID_INPUT, rawBuf, ref size, hdr) != size)
            return;
        double ts = Clock.Ms;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(rawBuf);
        if (header.dwType != RIM_TYPEHID && header.dwType != RIM_TYPEKEYBOARD)
            return;
        var dev = Device(header.hDevice);

        if (header.dwType == RIM_TYPEKEYBOARD)
        {
            // RAWKEYBOARD：MakeCode, Flags, Reserved, VKey。只记录遥控器的按键（真键盘的不记录）。
            // 键盘接口的 Raw Input 比钩子晚 60–100ms 才到（P5 实测），不能用来在钩子里区分来源，只做日志。
            if (!dev.remote)
                return;
            ushort kflags = (ushort)Marshal.ReadInt16(rawBuf, (int)hdr + 2);
            ushort vkey = (ushort)Marshal.ReadInt16(rawBuf, (int)hdr + 6);
            int code = vkey == 0x0D && (kflags & RI_KEY_E0) != 0 ? KeyboardKeys.NumpadEnter : vkey;
            Log.Write($"[Raw] 遥控器键盘 {KeyName(code)} {((kflags & RI_KEY_BREAK) != 0 ? "抬起" : "按下")}");
            return;
        }
        if (!dev.remote)
            return;  // 只关心遥控器；真键盘媒体键不产生 Consumer 报文（P0 已证实）

        int sizeHid = Marshal.ReadInt32(rawBuf, (int)hdr);
        int count = Marshal.ReadInt32(rawBuf, (int)hdr + 4);
        if (sizeHid <= 0 || count <= 0)
            return;
        var bytes = new byte[sizeHid * count];
        Marshal.Copy(rawBuf + (int)hdr + 8, bytes, 0, bytes.Length);

        if (dev.page != 0x0C)
            return;
        for (int i = 0; i < count; i++)
            Consumer.OnReport(bytes, i * sizeHid, sizeHid, ts);
    }

    (bool remote, ushort page, ushort usage) Device(IntPtr h)
    {
        if (devices.TryGetValue(h, out var d))
            return d;
        // RID_DEVICE_INFO：cbSize, dwType, 然后 HID 段：vendor, product, version, usagePage, usage
        const int infoSize = 32;
        var buf = Marshal.AllocHGlobal(infoSize);
        try
        {
            Marshal.WriteInt32(buf, 0, infoSize);
            uint size = infoSize;
            d = (false, 0, 0);
            if (GetRawInputDeviceInfo(h, RIDI_DEVICEINFO, buf, ref size) > 0)
            {
                uint type = (uint)Marshal.ReadInt32(buf, 4);
                if (type == RIM_TYPEHID)
                {
                    int vendor = Marshal.ReadInt32(buf, 8);
                    d = (vendor == RemoteKeys.RemoteVendorId, (ushort)Marshal.ReadInt16(buf, 20), (ushort)Marshal.ReadInt16(buf, 22));
                }
                else if (type == RIM_TYPEKEYBOARD)
                {
                    // 键盘类设备信息里没有 VID，从设备路径判断
                    bool remote = DeviceName(h).IndexOf($"VID_{RemoteKeys.RemoteVendorId:X4}", StringComparison.OrdinalIgnoreCase) >= 0;
                    d = (remote, 0x01, 0x06);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        devices[h] = d;
        if (d.remote)
            Log.Write($"识别到遥控器接口：Usage Page 0x{d.page:X2} / Usage 0x{d.usage:X2}");
        return d;
    }

    static string DeviceName(IntPtr h)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(h, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0)
            return "";
        var buf = Marshal.AllocHGlobal((int)(chars + 1) * 2);
        try
        {
            GetRawInputDeviceInfo(h, RIDI_DEVICENAME, buf, ref chars);
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void OnConsumer(int usage, bool down, double ts)
    {
        if (down)
            Media.OnRemoteDown(usage, ts);
        Log.Write($"[Raw] 遥控器 {RemoteKeys.Name(usage)} {(down ? "按下" : "抬起")}");
        if (usage == RemoteKeys.Mute && RestartExe != null)
        {
            OnMute(down, ts);
            return;
        }
        Event?.Invoke(new InputEvent(InputKind.Consumer, usage, down, ts));
    }

    // 🔇：双击 = 紧急重启（在输入线程里判定，界面卡死也有效）；单击等双击窗口过后才交给界面线程（新建任务）
    const uint TimerMute = 9;
    const uint MuteDoubleMs = 350;
    double lastMuteDown = double.MinValue;
    bool muteTapPending;

    void OnMute(bool down, double ts)
    {
        if (!down)
            return;
        if (muteTapPending && ts - lastMuteDown < MuteDoubleMs)
        {
            muteTapPending = false;
            KillTimer(hwnd, (UIntPtr)TimerMute);
            Panic("🔇 双击");
            return;
        }
        lastMuteDown = ts;
        muteTapPending = true;
        SetTimer(hwnd, (UIntPtr)TimerMute, MuteDoubleMs, IntPtr.Zero);
    }

    void OnMuteTimer()
    {
        KillTimer(hwnd, (UIntPtr)TimerMute);
        if (!muteTapPending || panicking)
            return;
        muteTapPending = false;
        double ts = Clock.Ms;
        Event?.Invoke(new InputEvent(InputKind.Consumer, RemoteKeys.Mute, true, ts));
        Event?.Invoke(new InputEvent(InputKind.Consumer, RemoteKeys.Mute, false, ts));
    }

    // ---------------- 低级键盘钩子 ----------------

    IntPtr OnHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // 只放行本程序自己发出的按键。不能看 LLKHF_INJECTED：
                // Windows 由 Consumer 报文翻译出的媒体键也带注入标记（P1 实测）。
                if (k.dwExtraInfo != OwnInjectSignature && Decide(k))
                    return (IntPtr)1;
            }
            catch (Exception e) { Log.Write("!! 钩子异常: " + e.Message); }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    bool Decide(KBDLLHOOKSTRUCT k)
    {
        bool up = (k.flags & LLKHF_UP) != 0;

        // 全局暂停：真键盘 Ctrl+Alt+F12
        if (k.vkCode == VK_F12)
        {
            if (!up && GetAsyncKeyState(VK_CONTROL) < 0 && GetAsyncKeyState(VK_MENU) < 0)
            {
                Paused = !Paused;
                swallowF12Up = true;
                Log.Write(Paused ? "⏸ 已暂停：遥控器全部恢复系统默认" : "▶ 已恢复");
                Event?.Invoke(new InputEvent(InputKind.Paused, 0, Paused, Clock.Ms));
                return true;
            }
            if (up && swallowF12Up)
            {
                swallowF12Up = false;
                return true;
            }
            return false;
        }
        if (Paused && !panicking)
            return false;

        if (RemoteKeys.VkToUsage.TryGetValue(k.vkCode, out int usage))
        {
            bool drained = DrainRaw();
            return Media.Decide(k.vkCode, usage, up, Clock.Ms, drained, (k.flags & LLKHF_INJECTED) != 0);
        }
        if (panicking)
            return false;  // 紧急重启中：除遥控器媒体键外一律放行
        if (Keys.Decide(k.vkCode, k.flags, up, Clock.Ms))
            return true;
        return Native(k, up);
    }

    /// <summary>
    /// 真键盘的按键（或分不出来源的键）：原样放行。例外：语音输入按住 Ctrl+Win 期间，把按键扣下交给界面线程，
    /// 先松开快捷键再补发，否则会变成 Ctrl+Win+键 的系统快捷键（如 +Enter 启动讲述人、+L 锁屏）。对应的抬起也吞掉。
    /// 返回 true 表示已拦截。
    /// </summary>
    bool Native(KBDLLHOOKSTRUCT k, bool up)
    {
        if (up)
            return swallowUps.Remove(k.vkCode);
        if (VoiceLatched && !IsModifier(k.vkCode))
        {
            swallowUps.Add(k.vkCode);
            LatchedKey?.Invoke((ushort)k.vkCode, (ushort)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0);
            return true;
        }
        return false;
    }

    static bool IsModifier(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
}
