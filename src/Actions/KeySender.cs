using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static G20SHub.Native.Win32;

namespace G20SHub.Actions;

/// <summary>
/// 用 SendInput 发送按键。每个事件都带 OwnInjectSignature，自己的钩子据此放行。
/// 文字字符走 KEYEVENTF_UNICODE，绕开输入法组字（如在中文输入法下发送 "y"）。
/// </summary>
static class KeySender
{
    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_DELETE = 0x2E, VK_LWIN = 0x5B,
        VK_MASK = 0xE8;  // 未分配的键码，用来遮罩 Win 键松开时弹出开始菜单

    static readonly HashSet<ushort> Extended = new()
    {
        VK_PRIOR, VK_NEXT, VK_END, VK_HOME, VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN, 0x2D, VK_DELETE, VK_LWIN, 0x5C, 0x5D,
        0xA3, 0xA5,  // 右 Ctrl、右 Alt
    };

    static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = VK_RETURN, ["Esc"] = VK_ESCAPE, ["Tab"] = VK_TAB, ["Space"] = VK_SPACE, ["Backspace"] = VK_BACK,
        ["PgUp"] = VK_PRIOR, ["PgDn"] = VK_NEXT, ["Home"] = VK_HOME, ["End"] = VK_END, ["Delete"] = VK_DELETE,
        ["Up"] = VK_UP, ["Down"] = VK_DOWN, ["Left"] = VK_LEFT, ["Right"] = VK_RIGHT,
        ["Ctrl"] = VK_CONTROL, ["LCtrl"] = 0xA2, ["RCtrl"] = 0xA3, ["Shift"] = VK_SHIFT, ["Alt"] = VK_MENU,
        ["LWin"] = VK_LWIN, ["RWin"] = 0x5C,
    };

    /// <summary>按键名 → VK，支持 Named 里的名字和 F1–F24。</summary>
    public static ushort Vk(string name)
    {
        if (Named.TryGetValue(name, out ushort vk)) return vk;
        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.Substring(1), out int n) && n >= 1 && n <= 24)
            return (ushort)(0x70 + n - 1);
        throw new FormatException($"不认识的按键名 {name}");
    }

    public static bool DryRun;

    public static void Down(ushort vk) => Send($"按下 VK 0x{vk:X2}", Key(vk, false));
    public static void Up(ushort vk) => Send($"抬起 VK 0x{vk:X2}", Key(vk, true));

    /// <summary>点按一个键，可带修饰键（按顺序按下、逆序松开）。</summary>
    public static void Tap(ushort vk, params ushort[] mods)
    {
        var list = new List<INPUT>();
        foreach (var m in mods) list.Add(Key(m, false));
        list.Add(Key(vk, false));
        list.Add(Key(vk, true));
        for (int i = mods.Length - 1; i >= 0; i--) list.Add(Key(mods[i], true));
        Send($"点按 VK 0x{vk:X2}{(mods.Length > 0 ? " + 修饰键" : "")}", list.ToArray());
    }

    /// <summary>原样补发一次物理按键（保留扫描码和扩展键标志）。</summary>
    public static void Replay(ushort vk, ushort scan, bool extended)
    {
        uint ext = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        INPUT Make(bool up) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = ext | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = OwnInjectSignature } },
        };
        Send($"补发 VK 0x{vk:X2}", Make(false), Make(true));
    }

    /// <summary>
    /// 在前台窗口中央滚动鼠标滚轮（正数向上）。滚轮只作用于光标下的窗口，
    /// 所以同一批输入里：光标移到窗口中央 → 滚动 → 移回原处，按顺序处理，不会滚错窗口。
    /// </summary>
    public static void Wheel(int notches)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetClientRect(fg, out var rc) || !GetCursorPos(out var old))
            return;
        var center = new POINT { X = (rc.Right - rc.Left) / 2, Y = (rc.Bottom - rc.Top) / 2 };
        ClientToScreen(fg, ref center);
        Send($"滚轮 {notches:+0;-0} 格", MoveTo(center), new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)(notches * WHEEL_DELTA)), dwFlags = MOUSEEVENTF_WHEEL, dwExtraInfo = OwnInjectSignature } },
        }, MoveTo(old));
    }

    /// <summary>
    /// 在屏幕坐标 (x, y) 点一下鼠标左键，再把光标移回原处（同一批输入，按顺序处理）。
    /// 只用于 UI Automation 实时读出的按钮位置，不用写死的坐标。
    /// </summary>
    public static void ClickAt(int x, int y)
    {
        if (!GetCursorPos(out var old))
            return;
        INPUT Button(uint flags) => new()
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = OwnInjectSignature } },
        };
        Send($"鼠标点击 ({x}, {y})", MoveTo(new POINT { X = x, Y = y }),
            Button(MOUSEEVENTF_LEFTDOWN), Button(MOUSEEVENTF_LEFTUP), MoveTo(old));
    }

    static INPUT MoveTo(POINT p)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);
        return new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)((p.X - vx) * 65535L / vw),
                    dy = (int)((p.Y - vy) * 65535L / vh),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = OwnInjectSignature,
                },
            },
        };
    }

    /// <summary>
    /// 按配置里的写法发送，例如 "{Enter}"、"y"、"^{PgUp}"。
    /// 前缀 ^ Ctrl、+ Shift、! Alt、# Win 作用于紧随其后的一个键；不带修饰键的普通字符按 Unicode 发送。
    /// </summary>
    public static void Spec(string spec)
    {
        var mods = new List<ushort>();
        for (int i = 0; i < spec.Length; i++)
        {
            char c = spec[i];
            switch (c)
            {
                case '^': mods.Add(VK_CONTROL); continue;
                case '+': mods.Add(VK_SHIFT); continue;
                case '!': mods.Add(VK_MENU); continue;
                case '#': mods.Add(VK_LWIN); continue;
            }
            if (c == '{')
            {
                int end = spec.IndexOf('}', i);
                if (end < 0) throw new FormatException($"按键写法缺少 }}：{spec}");
                string name = spec.Substring(i + 1, end - i - 1);
                if (!Named.TryGetValue(name, out ushort vk)) throw new FormatException($"不认识的按键名 {{{name}}}");
                Tap(vk, mods.ToArray());
                i = end;
            }
            else if (mods.Count > 0 && char.IsLetterOrDigit(c) && c < 128)
            {
                Tap(char.ToUpperInvariant(c), mods.ToArray());
            }
            else
            {
                Send($"字符 '{c}'", Unicode(c, false), Unicode(c, true));
            }
            mods.Clear();
        }
    }

    static INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags, dwExtraInfo = OwnInjectSignature } },
        };
    }

    static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = OwnInjectSignature } },
    };

    static void Send(string what, params INPUT[] inputs)
    {
        if (DryRun)
        {
            Log.Write($"  （dry-run）将发送：{what}");
            return;
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Log.Write($"!! SendInput 只发出 {sent}/{inputs.Length}（错误 {Marshal.GetLastWin32Error()}；目标窗口可能以管理员身份运行）");
    }
}
