using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Automation;
using static G20SHub.Actions.UiaClick;

namespace G20SHub.Actions;

/// <summary>
/// Claude 桌面版的 Chat ⇄ Code 切换（通过 UI Automation，不用坐标点击）。
/// P6 探查（2026-10-11）：切换按钮是两个 RadioButton「Claude」（Chat）和「Code」；AutomationId 是自动生成的，不能用。
/// Code 里有会话在运行时，Chat 界面里显示为「Code，執行中」，所以按名称前缀匹配。
/// 会跨进程调用，必须在后台线程执行。
/// </summary>
static class ClaudeDesktop
{
    /// <summary>Claude 桌面版窗口：优先前台那个；与同名的 Claude Code 命令行进程区分，只认带窗口的商店版。</summary>
    public static IntPtr FindWindow()
    {
        var fg = Native.Win32.GetForegroundWindow();
        if (fg != IntPtr.Zero && IsClaudeWindow(fg))
            return fg;
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            using (p)
            {
                if (p.MainWindowHandle != IntPtr.Zero && IsClaudeWindow(p.MainWindowHandle))
                    return p.MainWindowHandle;
            }
        }
        return IntPtr.Zero;
    }

    public static bool IsClaudeWindow(IntPtr hwnd) =>
        WindowTarget.ProcessPath(hwnd).IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>在 Chat 与 Code 之间切换，返回切换后的模式名；失败抛出带说明的异常。</summary>
    public static string ToggleMode()
    {
        var hwnd = FindWindow();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Claude 桌面版没有打开");
        WindowTarget.Activate(hwnd);

        var root = AutomationElement.FromHandle(hwnd);
        var radios = Retry(() => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)).Cast<AutomationElement>().ToList(),
            list => list.Any(e => IsMode(Name(e), "Code")));
        var chat = radios.FirstOrDefault(e => IsMode(Name(e), "Claude"));
        var code = radios.FirstOrDefault(e => IsMode(Name(e), "Code"));
        if (chat == null || code == null)
            throw new InvalidOperationException("没找到 Chat/Code 切换按钮（界面可能改版了）");

        bool inCode = IsSelected(code);
        var target = inCode ? chat : code;
        if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
            ((SelectionItemPattern)sel).Select();
        else if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
            ((InvokePattern)inv).Invoke();
        else
            throw new InvalidOperationException("切换按钮不支持点击");
        return inCode ? "Chat" : "Code";
    }

    /// <summary>名称本身，或名称 + 逗号/空格开头（如「Code，執行中」），不会误认「Claude Code」这类按钮。</summary>
    static bool IsMode(string name, string label) =>
        name == label || name.StartsWith(label + "，") || name.StartsWith(label + ",") || name.StartsWith(label + " ");

    static bool IsSelected(AutomationElement e) =>
        e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected;
}
