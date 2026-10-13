using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace G20SHub.Actions;

/// <summary>
/// 用 UI Automation（Windows 自带）在某个窗口里点按钮，适用于 Claude、ZCode、Codex 等 Electron 应用。
/// 只认名称与名单完全一致、且可用的按钮；有多个时取最后一个（最新出现的）。
/// 找不到时把疑似相关的按钮名写进日志，测一次就能把真实名称补进 config.json。
/// 会跨进程调用，必须在后台线程执行。
/// </summary>
static class UiaClick
{
    public static readonly string[] ApproveHints = { "允", "拒", "批准", "核准", "同意", "确认", "確認", "运行", "執行", "执行", "Allow", "Deny", "Approve", "Reject", "Confirm", "Run", "Yes", "No", "是", "否" };

    /// <summary>
    /// 选项卡片式的权限请求（如 ZCode：选项是列表项「允许 / 始终允许本项目 / 拒绝」，再点「确认」）：
    /// 先选中名称与 names 完全一致的选项，再点名称在 confirmNames 中的按钮。返回选中的选项名；找不到返回 null。
    /// </summary>
    public static string Choose(IntPtr hwnd, string[] names, string[] confirmNames, string[] hints, string what)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var cond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var items = Retry(() => root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>().ToList(), list => list.Count > 10);

        var option = items.LastOrDefault(e => Enabled(e) && names.Any(n => string.Equals(Name(e), n, StringComparison.OrdinalIgnoreCase)));
        var confirm = items.LastOrDefault(e => Enabled(e) && confirmNames.Any(n => string.Equals(Name(e), n, StringComparison.OrdinalIgnoreCase)));
        if (option == null || confirm == null)
        {
            var candidates = items.Select(Name)
                .Where(n => n.Length > 0 && n.Length <= 30 && hints.Any(h => n.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0))
                .Distinct();
            Log.Write($"[{what}] 没找到选项或确认按钮。选项：{string.Join(" / ", names)}，确认：{string.Join(" / ", confirmNames)}；疑似相关：{string.Join(" | ", candidates)}");
            return null;
        }

        string name = Name(option);
        if (option.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
            ((SelectionItemPattern)sel).Select();
        else if (option.TryGetCurrentPattern(InvokePattern.Pattern, out var inv0))
            ((InvokePattern)inv0).Invoke();
        Thread.Sleep(60);
        if (confirm.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
            ((InvokePattern)inv).Invoke();
        if (!StillThere(confirm, 700))
            return name;

        // 程序化点击没生效：在实时位置用鼠标点选项、再点确认
        Log.Write($"[{what}] 程序化点击没生效，改用鼠标点击「{name}」和「{Name(confirm)}」");
        if (RealClick(option, hwnd))
        {
            Thread.Sleep(80);
            if (RealClick(confirm, hwnd) && !StillThere(confirm, 700))
                return name + "（鼠标点击）";
        }
        return null;
    }
    public static readonly string[] StopHints = { "停", "中断", "中止", "Stop", "Cancel", "Interrupt", "Abort" };
    public static readonly string[] NewTaskHints = { "新建", "新增", "新任务", "新会话", "新对话", "新聊天", "New" };

    /// <summary>
    /// 点击名称在 names 中的按钮，返回点到的按钮名；找不到返回 null。
    /// fallbackKey：点击后按钮仍在时（点击没生效）改发的快捷键，如 Claude 的 "^{Enter}"；null 表示不补发。
    /// </summary>
    public static string Button(IntPtr hwnd, string[] names, string fallbackKey, string[] hints, string what)
    {
        if (names == null || names.Length == 0)
            return null;
        var root = AutomationElement.FromHandle(hwnd);
        var buttons = Retry(() => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().ToList(),
            list => list.Count > 10);

        var match = buttons.LastOrDefault(b => Enabled(b) && names.Any(n => string.Equals(Name(b), n, StringComparison.OrdinalIgnoreCase)));
        if (match == null || !match.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            var candidates = buttons.Select(Name)
                .Where(n => n.Length > 0 && n.Length <= 30 && hints.Any(h => n.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0))
                .Distinct();
            Log.Write($"[{what}] 没找到按钮。名单：{string.Join(" / ", names)}；疑似相关的按钮：{string.Join(" | ", candidates)}");
            return null;
        }

        string name = Name(match);
        ((InvokePattern)inv).Invoke();
        if (!StillThere(match, 700))
            return name;

        // 有的按钮不响应程序化点击（Claude 桌面版的「允许一次」，2026-10-11 实测）：
        // 改为在它实时读出的屏幕位置点一下真实的鼠标左键
        Log.Write($"[{what}] 程序化点击「{name}」没生效，改用鼠标点击按钮位置");
        if (RealClick(match, hwnd) && !StillThere(match, 700))
            return name + "（鼠标点击）";

        if (fallbackKey == null)
        {
            Log.Write($"[{what}] 鼠标点击后按钮仍在");
            return null;
        }
        Log.Write($"[{what}] 鼠标点击后按钮仍在，改用 {fallbackKey}");
        WindowTarget.Activate(hwnd);
        KeySender.Spec(fallbackKey);
        if (!StillThere(match, 700))
            return name + $"（经 {fallbackKey}）";
        Log.Write($"[{what}] {fallbackKey} 后按钮仍在");
        return null;
    }

    /// <summary>
    /// 在元素的实时屏幕位置中心点一下鼠标左键：先滚动到可见、确认窗口在前台，再取 BoundingRectangle。
    /// 位置为空（不可见）时返回 false，不点。
    /// </summary>
    public static bool RealClick(AutomationElement e, IntPtr hwnd)
    {
        // 本线程临时切到每显示器 DPI 感知：读位置、光标、虚拟屏幕尺寸、发鼠标输入都用物理像素，
        // 窗口在不同缩放的副屏上也不会点歪
        var oldDpi = Native.Win32.SetThreadDpiAwarenessContext(Native.Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            if (e.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
                ((ScrollItemPattern)scroll).ScrollIntoView();
            WindowTarget.Activate(hwnd);
            Thread.Sleep(80);
            var r = e.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 2 || r.Height < 2)
            {
                Log.Write("[点击] 按钮没有可见位置，不点");
                return false;
            }
            int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height / 2);
            Log.Write($"[点击] 「{Name(e)}」位置 {r.Left:F0},{r.Top:F0} {r.Width:F0}×{r.Height:F0}，点中心 ({x}, {y})");
            KeySender.ClickAt(x, y);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;  // 按钮已经消失（可能程序化点击其实生效了）
        }
        finally
        {
            if (oldDpi != IntPtr.Zero)
                Native.Win32.SetThreadDpiAwarenessContext(oldDpi);
        }
    }

    static readonly string[] SteerHints = { "立即", "引导", "引導", "Steer", "Send now" };

    /// <summary>
    /// 插话用：发送后在 timeoutMs 内等待「立即传送」/「引导」这类按钮出现（AI 正在输出、消息进了队列才会有），
    /// 出现就点最新的那个（程序化点击不生效再用鼠标）。返回点到的按钮名；AI 空闲时不会出现，返回 null。
    /// </summary>
    public static string ClickWhenAppears(IntPtr hwnd, string[] names, int timeoutMs)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        var sw = Stopwatch.StartNew();
        List<AutomationElement> buttons = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            buttons = root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>().ToList();
            var b = buttons.LastOrDefault(x => Enabled(x) && names.Any(n => string.Equals(Name(x), n, StringComparison.OrdinalIgnoreCase)));
            if (b != null)
            {
                string name = Name(b);
                if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                    ((InvokePattern)inv).Invoke();
                if (StillThere(b, 500))
                    RealClick(b, hwnd);
                return name;
            }
            Thread.Sleep(150);
        }
        // 没等到：AI 空闲时这是正常的；若有名字相近的按钮，记下来方便补进配置
        var similar = buttons?.Select(Name).Where(n => n.Length > 0 && n.Length <= 20 && SteerHints.Any(h => n.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)).Distinct().ToList();
        if (similar != null && similar.Count > 0)
            Log.Write($"[插话] 没找到 {string.Join(" / ", names)}；名字相近的按钮：{string.Join(" | ", similar)}");
        return null;
    }

    /// <summary>在 timeoutMs 内，元素一直存在且可用则返回 true。</summary>
    public static bool StillThere(AutomationElement e, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var c = e.Current;
                if (c.IsOffscreen || !c.IsEnabled)
                    return false;
            }
            catch (ElementNotAvailableException) { return false; }
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Electron 的无障碍树在第一次被查询后才开启：取不到时稍等再取一次。</summary>
    public static T Retry<T>(Func<T> get, Func<T, bool> ok)
    {
        var v = get();
        if (ok(v)) return v;
        Thread.Sleep(400);
        return get();
    }

    public static string Name(AutomationElement e)
    {
        try { return e.Current.Name ?? ""; } catch { return ""; }
    }

    public static bool Enabled(AutomationElement e)
    {
        try { return e.Current.IsEnabled; } catch { return false; }
    }
}
