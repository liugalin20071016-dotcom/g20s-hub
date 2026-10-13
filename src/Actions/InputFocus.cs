using System;
using System.Linq;
using System.Windows.Automation;

namespace G20SHub.Actions;

/// <summary>
/// 把焦点放回 AI 应用的输入框。焦点停在侧边栏、任务列表、某个按钮上时，Enter / Shift+Enter / 语音上屏
/// 都会作用到那里（例如误触「新建任务」）——所以语音输入、插话、换行、标点之前先调用这里。
/// 输入框 = 窗口里可编辑（非只读）、可获得焦点、可见的 Edit 控件，取最靠下的那个（聊天输入框都在底部）。
/// 实测（2026-10-12）：Claude 桌面版为「提示词」，ZCode 为「向 ZCode 提问…」/「提出后续修改要求」。
/// 只用 SetFocus，不点鼠标、不移动光标位置。会跨进程调用，必须在后台线程执行。
/// </summary>
static class InputFocus
{
    /// <summary>确保焦点在输入框里。返回：null = 本来就在 / 找不到；否则为刚聚焦的输入框名。</summary>
    public static string Ensure(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            int pid = root.Current.ProcessId;
            var focused = AutomationElement.FocusedElement;
            if (focused != null && focused.Current.ProcessId == pid && IsEditable(focused))
                return null;

            var edits = UiaClick.Retry(() => root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().Where(IsEditable).ToList(), list => list.Count > 0);
            var input = edits
                .Where(e => { var r = e.Current.BoundingRectangle; return !r.IsEmpty && r.Width > 40 && !e.Current.IsOffscreen; })
                .OrderBy(e => e.Current.BoundingRectangle.Bottom)
                .LastOrDefault();
            if (input == null)
            {
                Log.Write("[输入框] 没找到可编辑的输入框，焦点不变");
                return null;
            }
            input.SetFocus();
            string name = UiaClick.Name(input);
            Log.Write($"[输入框] 焦点移到「{(name.Length > 20 ? name.Substring(0, 20) + "…" : name)}」");
            return name;
        }
        catch (Exception e)
        {
            Log.Write("[输入框] 聚焦失败：" + e.Message);
            return null;
        }
    }

    static bool IsEditable(AutomationElement e)
    {
        try
        {
            var c = e.Current;
            if (c.ControlType != ControlType.Edit || !c.IsEnabled || !c.IsKeyboardFocusable)
                return false;
            return !(e.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && ((ValuePattern)v).Current.IsReadOnly);
        }
        catch { return false; }
    }
}
