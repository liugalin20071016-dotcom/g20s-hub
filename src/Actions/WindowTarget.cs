using System;
using System.Text;
using static G20SHub.Native.Win32;

namespace G20SHub.Actions;

/// <summary>窗口信息与激活。圆环（P5）也用它打开窗口。</summary>
static class WindowTarget
{
    /// <summary>窗口所属进程的完整路径；拿不到（如更高权限的进程）返回空串。</summary>
    public static string ProcessPath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 把窗口切到前台（最小化的先还原）。后台进程直接 SetForegroundWindow 常被系统拒绝，
    /// 所以先临时挂接到当前前台线程的输入队列；仍失败时用「按住 Alt」的办法（中间插一个遮罩键，避免激活菜单栏）。
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        if (GetForegroundWindow() == hwnd)
            return true;

        uint self = GetCurrentThreadId();
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(self, fgThread, false);
        }
        if (GetForegroundWindow() == hwnd)
            return true;

        KeySender.Down(KeySender.VK_MENU);
        SetForegroundWindow(hwnd);
        KeySender.Tap(KeySender.VK_MASK);
        KeySender.Up(KeySender.VK_MENU);
        return GetForegroundWindow() == hwnd;
    }
}
