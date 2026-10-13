using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using G20SHub.Actions;
using static G20SHub.Native.Win32;

namespace G20SHub.Ring;

/// <summary>一个可切换的顶层窗口。</summary>
sealed class AppWindow
{
    public IntPtr Hwnd;
    public string Path = "", Exe = "", Class = "", Title = "", Aumid = "", DisplayName = "";
}

/// <summary>枚举可切换的窗口、按格子匹配窗口、启动程序、取程序图标。只用 Windows 自带接口。</summary>
static class AppCatalog
{
    static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow",
    };
    static readonly HashSet<string> SkipExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost.exe", "ShellExperienceHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe",
    };
    static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>可切换的顶层窗口，按 Z 序（最近使用的在前）。</summary>
    public static List<AppWindow> List()
    {
        var result = new List<AppWindow>();
        int self = Process.GetCurrentProcess().Id;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero)
                return true;
            long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0)
                return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
                return true;  // 其他虚拟桌面上或被隐藏的应用窗口
            string title = WindowTarget.Title(h), cls = WindowTarget.ClassName(h);
            if (title.Length == 0 || SkipClasses.Contains(cls))
                return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self)
                return true;
            string path = WindowTarget.ProcessPath(h), exe = System.IO.Path.GetFileName(path);
            if (exe.Length == 0 || SkipExes.Contains(exe))
                return true;
            result.Add(new AppWindow
            {
                Hwnd = h, Path = path, Exe = exe, Class = cls, Title = title,
                Aumid = Aumid(pid), DisplayName = DisplayName(path),
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>格子对应的窗口：同一程序、类名（若有）、标题包含（若有），取最近使用的那个。</summary>
    public static AppWindow Find(Slot s)
    {
        foreach (var w in List())
        {
            if (!string.Equals(w.Exe, s.Exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Class.Length > 0 && !string.Equals(w.Class, s.Class, StringComparison.Ordinal)) continue;
            if (s.TitleContains.Length > 0 && w.Title.IndexOf(s.TitleContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            return w;
        }
        return null;
    }

    /// <summary>打开格子：窗口已在就切过去，否则按「启动方式」启动。返回给用户看的结果。</summary>
    public static string Open(Slot s)
    {
        var w = Find(s);
        if (w != null)
        {
            WindowTarget.Activate(w.Hwnd);
            return s.Name;
        }
        if (s.Launch.Length == 0)
            return $"⚠ {s.Name} 没有在运行";
        try
        {
            if (s.Launch.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start("explorer.exe", "\"" + s.Launch + "\"");
            else
                Process.Start(new ProcessStartInfo(s.Launch) { UseShellExecute = true });
            return $"正在打开 {s.Name}";
        }
        catch (Exception e)
        {
            Log.Write($"!! 启动 {s.Name} 失败（{s.Launch}）：{e.Message}");
            return $"⚠ 打不开 {s.Name}";
        }
    }

    /// <summary>由窗口生成格子。商店应用（如 Claude）的程序目录不能直接启动，改用 shell:AppsFolder\应用ID。</summary>
    public static Slot ToSlot(AppWindow w) => new()
    {
        Name = w.DisplayName,
        Exe = w.Exe,
        Path = w.Path,
        Class = w.Class,
        Launch = w.Aumid.Length > 0 ? @"shell:AppsFolder\" + w.Aumid : w.Path,
    };

    public static string DisplayName(string path)
    {
        if (NameCache.TryGetValue(path, out var n))
            return n;
        try { n = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
        catch { n = null; }
        if (string.IsNullOrEmpty(n))
            n = System.IO.Path.GetFileNameWithoutExtension(path);
        return NameCache[path] = n;
    }

    static string Aumid(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return "";
        try
        {
            uint len = 256;
            var sb = new StringBuilder((int)len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    // ---------------- 图标 ----------------

    /// <summary>格子的图标：商店应用用应用 ID，其余用程序路径；都取不到时用正在运行的窗口图标。</summary>
    public static ImageSource Icon(Slot s)
    {
        string source = s.Launch.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ? s.Launch : s.Path;
        var img = Icon(source);
        if (img != null)
            return img;
        var w = Find(s);
        return w != null ? WindowIcon(w.Hwnd) : null;
    }

    public static ImageSource Icon(AppWindow w) =>
        Icon(w.Aumid.Length > 0 ? @"shell:AppsFolder\" + w.Aumid : w.Path) ?? WindowIcon(w.Hwnd);

    /// <summary>用外壳的 IShellItemImageFactory 取 96px 图标（保留透明通道），失败返回 null。</summary>
    public static ImageSource Icon(string source)
    {
        if (string.IsNullOrEmpty(source))
            return null;
        if (IconCache.TryGetValue(source, out var cached))
            return cached;
        ImageSource img = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out var factory);
            const int ICONONLY = 0x4, BIGGERSIZEOK = 0x1;
            if (factory.GetImage(new SIZE { cx = 96, cy = 96 }, ICONONLY | BIGGERSIZEOK, out var hbm) == 0 && hbm != IntPtr.Zero)
            {
                try { img = FromHBitmap(hbm); }
                finally { DeleteObject(hbm); }
            }
            Marshal.ReleaseComObject(factory);
        }
        catch { img = null; }
        return IconCache[source] = img;
    }

    static ImageSource WindowIcon(IntPtr hwnd)
    {
        var h = SendMessage(hwnd, WM_GETICON, (IntPtr)1, IntPtr.Zero);  // ICON_BIG
        if (h == IntPtr.Zero)
            h = GetClassLongPtr(hwnd, GCLP_HICON);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var img = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>32 位预乘透明的 DIB → WPF 位图（自下而上存储时翻转行）。</summary>
    static BitmapSource FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }
        int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = w * 4;
        var buf = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, buf, 0, buf.Length);
        if (ds.dsBmih.biHeight > 0)
        {
            var flipped = new byte[buf.Length];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(buf, y * stride, flipped, (h - 1 - y) * stride, stride);
            buf = flipped;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }
}
