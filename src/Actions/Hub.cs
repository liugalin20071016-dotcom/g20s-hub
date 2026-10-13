using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using G20SHub.Context;
using G20SHub.Gestures;
using G20SHub.Input;
using G20SHub.Ring;
using G20SHub.UI;
using static G20SHub.Actions.KeySender;

namespace G20SHub.Actions;

/// <summary>
/// 键位绑定：遥控器的键 → 手势需求 → 动作。换用法时改 Specs 和 OnGesture。
/// </summary>
sealed class Hub
{
    /// <summary>
    /// 每个键需要哪些手势。只绑定能精确区分来源的媒体键；方向键 / OK / DEL 走键盘接口，
    /// 分不清是遥控器还是真键盘（Raw Input 比钩子晚 60–100ms），一律原样放行，不做手势（P5 教训）。
    /// </summary>
    public readonly Dictionary<string, Needs> Specs = BaseSpecs();

    /// <summary>🔇 不用长按：遥控器自己占用了长按静音（P3 实测）。</summary>
    static Dictionary<string, Needs> BaseSpecs() => new()
    {
        ["mic"] = Needs.None,
        // 🔇 单击 = 新建任务；双击 = 紧急重启（双击判定在输入线程里做，界面卡死也能重启，单击才转到这里）
        ["mute"] = Needs.None,
        ["home"] = Needs.Double,
        ["back"] = Needs.None,
        ["play"] = Needs.Double,  // 单击 Chat ⇄ Code，双击换行
        ["volup"] = Needs.Repeat,
        ["voldown"] = Needs.Repeat,
        ["prev"] = Needs.Double,  // 单击切标签，双击输入「，」
        ["next"] = Needs.Double,  // 单击切标签，双击输入「。」
        ["menu"] = Needs.Long,
        ["power"] = Needs.None,  // ⏻ 锁屏
        // 圆环打开期间由输入层接管的键
        ["ring_up"] = Needs.None,
        ["ring_right"] = Needs.None,
        ["ring_down"] = Needs.None,
        ["ring_left"] = Needs.None,
        ["ring_ok"] = Needs.None,
    };

    /// <summary>输入事件 → 键名；不归遥控器管的返回 null。</summary>
    public static string KeyOf(InputEvent ev) => ev.Kind switch
    {
        InputKind.Consumer => ev.Code switch
        {
            RemoteKeys.Mic => "mic",
            RemoteKeys.Mute => "mute",
            RemoteKeys.Home => "home",
            RemoteKeys.Back => "back",
            RemoteKeys.PlayPause => "play",
            RemoteKeys.VolUp => "volup",
            RemoteKeys.VolDown => "voldown",
            RemoteKeys.Prev => "prev",
            RemoteKeys.Next => "next",
            RemoteKeys.ConsumerPower or RemoteKeys.ConsumerSleep => "power",
            _ => null,
        },
        InputKind.System => "power",
        InputKind.Keyboard => ev.Code switch
        {
            KeyboardKeys.Apps => "menu",
            KeyboardKeys.Up => "ring_up",
            KeyboardKeys.Right => "ring_right",
            KeyboardKeys.Down => "ring_down",
            KeyboardKeys.Left => "ring_left",
            KeyboardKeys.NumpadEnter => "ring_ok",
            _ => null,
        },
        _ => null,
    };

    public readonly VoiceInput Voice;
    public readonly RingController Ring;
    readonly ContextResolver resolver;
    readonly int imeCommitMs, scrollNotches;
    readonly string prevDouble, nextDouble;
    bool sending, uiaBusy;

    public Hub(Config cfg, string root)
    {
        Voice = new VoiceInput(cfg);
        Ring = new RingController(cfg, root);
        resolver = new ContextResolver(cfg);
        imeCommitMs = (int)cfg.Num("timing.ime_commit_ms", 300);
        scrollNotches = (int)cfg.Num("scroll.notches", 5);
        prevDouble = cfg.Str("text.prev_double", "，");
        nextDouble = cfg.Str("text.next_double", "。");
    }

    public void OnGesture(string key, Gesture g)
    {
        if (g != Gesture.Repeat)
            Log.Write($"[手势] {key} {g}");

        // 圆环打开时：方向键 / OK / ☰ / ↩ 交给圆环，其他键先关圆环再照常处理
        if (Ring.Visible)
        {
            switch (key)
            {
                case "ring_up": Ring.Press(Dir.Up); return;
                case "ring_right": Ring.Press(Dir.Right); return;
                case "ring_down": Ring.Press(Dir.Down); return;
                case "ring_left": Ring.Press(Dir.Left); return;
                case "ring_ok": Ring.Flip(); return;
                case "menu" when g == Gesture.Tap: Ring.Toggle(); return;
                case "back": Ring.Cancel("↩"); return;
                default: Ring.Cancel(KeyLabel(key)); break;
            }
        }
        if (key.StartsWith("ring_"))
            return;  // 圆环刚关闭时残留的按键

        // 语音输入按住 Ctrl+Win 期间，其他动作先关掉语音，免得变成 Ctrl+Win+键。
        // 输入文字类的动作（插话、换行、标点）由 AfterVoice 关语音，并等文字上屏后再发。
        if (key != "mic" && !IsTextAction(key, g))
            Voice.Stop(KeyLabel(key));

        switch (key, g)
        {
            case ("mic", Gesture.Tap): Mic(); break;

            // 🏠 插话：发送；AI 正在输出时消息会排队，再点「立即传送」（Claude）/「引导」（Codex）让它立即送达
            case ("home", Gesture.Tap): AfterVoice(() => Spec(SteerKey()), "💬 插话", "💬 已发送", steer: true); break;
            case ("home", Gesture.Double): Approve(); break;

            // ⏯ 双击换行（Shift+Enter，不会把消息发出去）；⏮⏭ 双击输入标点
            case ("play", Gesture.Double): AfterVoice(() => Spec(NewlineKey()), null, "↵ 换行"); break;
            case ("prev", Gesture.Double): AfterVoice(() => Spec(prevDouble), null, null); break;
            case ("next", Gesture.Double): AfterVoice(() => Spec(nextDouble), null, null); break;

            case ("back", Gesture.Tap): Interrupt(); break;

            // 方向按用户实测对调：🔉 向上、🔊 向下；用滚轮滚约半页
            case ("voldown", Gesture.Tap or Gesture.Repeat): Wheel(+scrollNotches); break;
            case ("volup", Gesture.Tap or Gesture.Repeat): Wheel(-scrollNotches); break;
            case ("prev", Gesture.Tap): Tap(VK_TAB, VK_CONTROL, VK_SHIFT); Osd.Show("◀ 上一个标签"); break;
            case ("next", Gesture.Tap): Tap(VK_TAB, VK_CONTROL); Osd.Show("▶ 下一个标签"); break;

            case ("mute", Gesture.Tap): NewTask(); break;  // 🔇 双击 = 紧急重启，由输入线程处理

            case ("menu", Gesture.Tap): Ring.Toggle(); break;
            case ("menu", Gesture.Long): Ring.OpenSettings(); break;
            case ("play", Gesture.Tap): ModeToggle(); break;
            case ("power", Gesture.Tap): Lock(); break;
        }
    }

    Profile Foreground(out IntPtr fg)
    {
        fg = Native.Win32.GetForegroundWindow();
        return resolver.Resolve(fg, out _);
    }

    /// <summary>🎤：关语音；或先把焦点放回输入框再开语音（文字才会上屏到输入框里）。</summary>
    async void Mic()
    {
        if (Voice.Active)
        {
            Voice.Stop("再按 🎤");
            return;
        }
        var p = Foreground(out var fg);
        if (p?.FocusInput == true)
            await Task.Run(() => InputFocus.Ensure(fg));
        Voice.Start();
    }

    /// <summary>⏯ 单击：只在支持的应用里切换模式（Claude 桌面版 Chat ⇄ Code），其他应用里不做事。</summary>
    void ModeToggle()
    {
        var p = Foreground(out _);
        if (p?.ModeToggle == "claude")
            ToggleClaudeMode();
        else
            Osd.Show(p == null ? "⏯ 当前窗口没有模式切换" : $"⏯ {p.Name} 没有模式切换");
    }

    /// <summary>🔇：在当前 AI 应用里新建任务 / 会话（点 profile 的 new_task_names 按钮），之后焦点放到输入框。</summary>
    void NewTask()
    {
        var p = Foreground(out var fg);
        if (p == null || p.NewTaskNames.Length == 0)
        {
            Osd.Show(p == null ? "⚠ 当前窗口不是 AI 应用，没有新建任务" : $"⚠ {p.Name} 还没配置新建任务按钮");
            return;
        }
        RunUia($"新建任务（{p.Name}）", () =>
        {
            var clicked = UiaClick.Button(fg, p.NewTaskNames, null, UiaClick.NewTaskHints, "新建任务");
            if (clicked == null)
                return "⚠ 没找到新建任务按钮（候选已记入日志）";
            if (p.FocusInput)
            {
                System.Threading.Thread.Sleep(400);  // 等新任务页面出来
                InputFocus.Ensure(fg);
            }
            return $"＋ 新建任务（{clicked}）";
        });
    }

    /// <summary>⏻：锁屏（等同 Win+L）。语音输入的 Ctrl+Win 已在上面先松开。</summary>
    static void Lock()
    {
        Log.Write("[动作] 🔒 锁屏");
        if (DryRun)
            return;
        if (!Native.Win32.LockWorkStation())
            Log.Write($"!! 锁屏失败：{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    static bool IsTextAction(string key, Gesture g) =>
        (key, g) is ("home", Gesture.Tap) or ("play", Gesture.Double)
            or ("prev", Gesture.Double) or ("next", Gesture.Double);

    /// <summary>
    /// 语音输入按住期间按了键盘上的键（遥控器的 OK / DEL / 方向键，或真键盘）：
    /// 输入线程已把这个键扣下，这里先关语音再补发。Enter 等文字上屏后再发，相当于插话。
    /// </summary>
    public void OnKeyWhileLatched(ushort vk, ushort scan, bool extended)
    {
        if (vk == VK_RETURN)
        {
            AfterVoice(() => Replay(vk, scan, extended), "💬 插话", "💬 已发送");
            return;
        }
        Voice.Stop("按键");
        Replay(vk, scan, extended);
    }

    /// <summary>
    /// 输入文字类动作（插话、换行、标点）：
    /// 1) 正在录音就先关掉语音，等录音真正停止、文字上屏；
    /// 2) 当前是 AI 应用时把焦点放回输入框（焦点在侧边栏 / 任务列表时，Enter 会误触别的东西）；
    /// 3) 发送；
    /// 4) steer 且应用支持时：AI 正在输出、消息进了队列，就点「立即传送」/「引导」让它立即送达（不打断 AI）。
    /// pending / done 为提示条文字，null 表示不提示。
    /// </summary>
    async void AfterVoice(System.Action send, string pending, string done, bool steer = false)
    {
        if (sending)
            return;
        sending = true;
        try
        {
            if (Voice.Active || Voice.Recording)
            {
                if (pending != null) Osd.Show(pending);
                Voice.Stop("输入");
                var t0 = Clock.Ms;
                while (Voice.Recording && Clock.Ms - t0 < 1500)
                    await Task.Delay(30);
            }
            int wait = (int)System.Math.Max(0, imeCommitMs - (Clock.Ms - Voice.StoppedAt));
            if (wait > 0)
                await Task.Delay(wait);

            var p = Foreground(out var fg);
            if (p?.FocusInput == true)
                await Task.Run(() => InputFocus.Ensure(fg));
            send();
            if (steer && p != null && p.SteerButtons.Length > 0)
            {
                string clicked = await Task.Run(() => UiaClick.ClickWhenAppears(fg, p.SteerButtons, 1500));
                if (clicked != null)
                {
                    Log.Write($"[插话] 消息在排队，点了「{clicked}」");
                    done = $"⚡ {clicked}";
                }
            }
            if (done != null) Osd.Show(done);
        }
        catch (Exception e)
        {
            Log.Write("!! 输入动作失败：" + e.Message);
        }
        finally
        {
            sending = false;
        }
    }

    /// <summary>当前窗口 profile 的插话键，未识别的窗口用 Enter。</summary>
    string SteerKey() => resolver.Resolve(Native.Win32.GetForegroundWindow(), out _)?.Steer ?? "{Enter}";

    /// <summary>当前窗口 profile 的换行键，默认 Shift+Enter。</summary>
    string NewlineKey() => resolver.Resolve(Native.Win32.GetForegroundWindow(), out _)?.Newline ?? "+{Enter}";

    /// <summary>
    /// 允许（🏠 双击）：只在识别出的 AI 工具窗口里生效。Claude 桌面版用 UI Automation 点「允许」按钮，
    /// 其他按 profile 发送允许键；未识别的窗口不发送任何东西。
    /// </summary>
    void Approve()
    {
        var fg = Native.Win32.GetForegroundWindow();
        var p = resolver.Resolve(fg, out string who);
        if (p == null || string.IsNullOrEmpty(p.Approve))
        {
            Log.Write($"[允许] 未识别的窗口 {who}，不发送");
            Osd.Show("⚠ 未识别的窗口，没有允许");
            return;
        }
        if (p.Approve == "uia")
        {
            RunUia($"允许（{p.Name}）", () =>
            {
                var clicked = p.ApproveConfirm.Length > 0
                    ? UiaClick.Choose(fg, p.ApproveNames, p.ApproveConfirm, UiaClick.ApproveHints, "允许")
                    : UiaClick.Button(fg, p.ApproveNames, p.ApproveFallback, UiaClick.ApproveHints, "允许");
                return clicked != null ? $"✅ 已允许（{clicked}）" : "⚠ 没找到允许按钮（候选已记入日志）";
            });
            return;
        }
        Log.Write($"[允许] {p.Name}：发送 {p.Approve}");
        Spec(p.Approve);
        Osd.Show($"✅ 已允许（{p.Name}）");
    }

    /// <summary>
    /// 打断（↩）：按当前窗口的 profile。"uia" 表示先点「停止」按钮，找不到再发 Esc；
    /// 其他写法直接发送（默认 Esc，未识别的窗口也发 Esc）。
    /// </summary>
    void Interrupt()
    {
        var fg = Native.Win32.GetForegroundWindow();
        var p = resolver.Resolve(fg, out _);
        if (p?.Interrupt == "uia")
        {
            RunUia($"打断（{p.Name}）", () =>
            {
                var clicked = UiaClick.Button(fg, p.InterruptNames, null, UiaClick.StopHints, "打断");
                if (clicked != null)
                    return $"⏹ 打断（点了「{clicked}」）";
                Spec("{Esc}");
                return "⏹ 打断（Esc）";
            });
            return;
        }
        Spec(p?.Interrupt ?? "{Esc}");
        Osd.Show("⏹ 打断");
    }

    /// <summary>⏯：Claude 桌面版 Chat ⇄ Code。</summary>
    void ToggleClaudeMode() => RunUia("Chat ⇄ Code", () => $"已切换到 {ClaudeDesktop.ToggleMode()}");

    /// <summary>UI Automation 会跨进程调用，放到后台线程执行，结果回到界面线程显示。</summary>
    async void RunUia(string what, Func<string> work)
    {
        if (uiaBusy)
            return;
        uiaBusy = true;
        try
        {
            var t0 = Clock.Ms;
            string result = await Task.Run(work);
            Log.Write($"[动作] {what}：{result}（{Clock.Ms - t0:F0} ms）");
            Osd.Show(result);
        }
        catch (Exception e)
        {
            Log.Write($"!! {what} 失败：{e.Message}");
            Osd.Show("⚠ " + e.Message);
        }
        finally
        {
            uiaBusy = false;
        }
    }

    /// <summary>暂停或退出时调用：松开语音快捷键，避免 Ctrl+Win 卡住。</summary>
    public void ReleaseAll(string reason)
    {
        Ring.Cancel(reason);
        Voice.Stop(reason);
    }

    static string KeyLabel(string key) => key switch
    {
        "home" => "🏠",
        "back" => "↩",
        "volup" or "voldown" => "翻页",
        "prev" or "next" => "切标签",
        "menu" => "☰",
        "play" => "⏯",
        "power" => "⏻",
        _ => key,
    };
}
