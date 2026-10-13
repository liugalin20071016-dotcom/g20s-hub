using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using G20SHub.UI;

namespace G20SHub.Setup;

/// <summary>
/// G20S Hub 安装 / 卸载程序（自己写的，不用第三方安装工具）。只给当前用户安装，不需要管理员权限：
/// 程序 → %LOCALAPPDATA%\Programs\G20S Hub；开始菜单快捷方式；「设置 → 应用」里的卸载项；可选开机自启。
/// 用户数据（config.json、ring.json、logs）在 %LOCALAPPDATA%\G20SHub，由程序自己创建，卸载时可选删除。
///
/// 命令行：
///   G20SHub-Setup.exe                                    图形界面安装
///   G20SHub-Setup.exe /S [/dir 目录] [/noautostart] [/run]   静默安装（默认开机自启、装完不启动）
///   Uninstall.exe [/S] [/purge]                          卸载（/purge 同时删除用户数据）
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (Arg(args, "/preview") is string previewDir)
        {
            SetupWindow.Preview(previewDir);  // 自检：把各页面画成 PNG 后退出，不安装任何东西
            return 0;
        }
        bool silent = Has(args, "/S");
        bool uninstall = Has(args, "/uninstall") ||
            string.Equals(System.IO.Path.GetFileNameWithoutExtension(Installer.Self), "Uninstall", StringComparison.OrdinalIgnoreCase);
        var o = new Options
        {
            Dir = Arg(args, "/dir") ?? (uninstall && Installer.SelfInstalled ? Installer.SelfDir : Installer.InstalledDir) ?? Installer.DefaultDir,
            AutoStart = !Has(args, "/noautostart"),
            Desktop = File.Exists(Installer.DesktopLnk),
            Run = !silent || Has(args, "/run"),
            Purge = Has(args, "/purge"),
        };
        o.Dir = System.IO.Path.GetFullPath(o.Dir);

        int code;
        if (silent)
        {
            try
            {
                if (uninstall) Installer.Uninstall(o);
                else Installer.Install(o);
                code = 0;
            }
            catch (Exception e)
            {
                File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "G20SHub-setup.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(uninstall ? "卸载" : "安装")}失败：{e}\r\n");
                code = 1;
            }
        }
        else
        {
            code = new Application().Run(new SetupWindow(uninstall, o));
        }
        Installer.RunPendingCleanup();
        return code;
    }

    static bool Has(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    static string Arg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return args[i].Substring(flag.Length + 1);
        }
        return null;
    }
}

sealed class Options
{
    public string Dir;
    public bool AutoStart = true, Desktop, Run = true, Purge;
}

/// <summary>安装 / 卸载的实际操作（界面和静默模式共用）。</summary>
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\G20SHub";
    const string PayloadPrefix = "payload/";
    const string AppExe = "G20SHub.exe", UninstallExe = "Uninstall.exe";

    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string DefaultDir = System.IO.Path.Combine(LocalAppData, "Programs", "G20S Hub");
    public static readonly string DataDir = System.IO.Path.Combine(LocalAppData, "G20SHub");
    public static readonly string StartMenuLnk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "G20S Hub.lnk");
    public static readonly string DesktopLnk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "G20S Hub.lnk");

    public static string Self => Assembly.GetExecutingAssembly().Location;
    public static string SelfDir => System.IO.Path.GetDirectoryName(Self);
    /// <summary>自己就是安装目录里的 Uninstall.exe。</summary>
    public static bool SelfInstalled => File.Exists(System.IO.Path.Combine(SelfDir, AppExe));

    public static string InstalledVersion => ReadKey("DisplayVersion");
    public static string InstalledDir => ReadKey("InstallLocation");

    static string ReadKey(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return k?.GetValue(name) as string;
    }

    static IEnumerable<string> PayloadNames => Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .Where(n => n.StartsWith(PayloadPrefix)).Select(n => n.Substring(PayloadPrefix.Length));

    public static void Install(Options o, Action<string> progress = null)
    {
        var exe = System.IO.Path.Combine(o.Dir, AppExe);
        Directory.CreateDirectory(o.Dir);

        progress?.Invoke("正在关闭运行中的 G20S Hub…");
        StopRunning(o.Dir);

        progress?.Invoke("正在复制文件…");
        foreach (var f in Directory.GetFiles(o.Dir, "*.old"))
            TryDelete(f);
        foreach (var name in PayloadNames)
        {
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadPrefix + name);
            WriteFile(System.IO.Path.Combine(o.Dir, name), res);
        }
        var uninstaller = System.IO.Path.Combine(o.Dir, UninstallExe);
        if (!string.Equals(Self, uninstaller, StringComparison.OrdinalIgnoreCase))
        {
            using var self = File.OpenRead(Self);
            WriteFile(uninstaller, self);
        }

        progress?.Invoke("正在创建快捷方式…");
        CreateShortcut(StartMenuLnk, exe);
        if (o.Desktop) CreateShortcut(DesktopLnk, exe);
        else if (File.Exists(DesktopLnk)) TryDelete(DesktopLnk);

        if (o.AutoStart) AutoStart.Set(exe, true);
        else if (AutoStart.IsOn(exe)) AutoStart.Set(exe, false);

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            long bytes = Directory.GetFiles(o.Dir).Sum(f => new FileInfo(f).Length);
            k.SetValue("DisplayName", "G20S Hub");
            k.SetValue("DisplayVersion", SetupInfo.Version);
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("Publisher", SetupInfo.Publisher);
            k.SetValue("InstallLocation", o.Dir);
            k.SetValue("UninstallString", $"\"{uninstaller}\"");
            k.SetValue("QuietUninstallString", $"\"{uninstaller}\" /S");
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("EstimatedSize", (int)Math.Max(1, bytes / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            if (SetupInfo.Url.Length > 0)
                k.SetValue("URLInfoAbout", SetupInfo.Url);
        }

        if (o.Run)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = o.Dir });
    }

    public static void Uninstall(Options o, Action<string> progress = null)
    {
        var exe = System.IO.Path.Combine(o.Dir, AppExe);

        progress?.Invoke("正在关闭运行中的 G20S Hub…");
        StopRunning(o.Dir);

        progress?.Invoke("正在删除文件…");
        if (AutoStart.IsOn(exe)) AutoStart.Set(exe, false);
        foreach (var lnk in new[] { StartMenuLnk, DesktopLnk })
            if (File.Exists(lnk)) TryDelete(lnk);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        // 只删自己装的文件，最后目录空了才删目录（不会误删用户放进去的东西）
        foreach (var name in PayloadNames.Concat(Directory.Exists(o.Dir) ? Directory.GetFiles(o.Dir, "*.old").Select(System.IO.Path.GetFileName) : Enumerable.Empty<string>()))
            TryDelete(System.IO.Path.Combine(o.Dir, name));
        var uninstaller = System.IO.Path.Combine(o.Dir, UninstallExe);
        if (string.Equals(Self, uninstaller, StringComparison.OrdinalIgnoreCase))
            pendingCleanup = o.Dir;  // 自己正在运行，退出后再删
        else
        {
            TryDelete(uninstaller);
            try { Directory.Delete(o.Dir); } catch { }
        }

        if (o.Purge && Directory.Exists(DataDir))
            Directory.Delete(DataDir, true);
    }

    static string pendingCleanup;

    /// <summary>卸载程序删不掉正在运行的自己：退出时交给 cmd，稍等片刻删掉 Uninstall.exe 和空目录。</summary>
    public static void RunPendingCleanup()
    {
        if (pendingCleanup == null)
            return;
        var cmd = $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{System.IO.Path.Combine(pendingCleanup, UninstallExe)}\" & rd \"{pendingCleanup}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetTempPath(),
        });
    }

    /// <summary>请安装目录里正在运行的 G20S Hub 退出（和新版本接管旧实例用同一个事件），超时则强制结束。</summary>
    static void StopRunning(string dir)
    {
        var prefix = dir.TrimEnd('\\') + "\\";
        var running = Process.GetProcessesByName("G20SHub")
            .Where(p => PathOf(p)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (running.Count == 0)
            return;
        try
        {
            using var quit = EventWaitHandle.OpenExisting(@"Local\G20SHub.Quit");
            quit.Set();
        }
        catch { }
        foreach (var p in running)
        {
            if (p.WaitForExit(6000))
                continue;
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    static string PathOf(Process p)
    {
        try { return p.MainModule.FileName; }
        catch { return null; }  // 以管理员身份运行的进程读不到路径
    }

    /// <summary>写文件；目标被占用（比如以管理员身份运行的旧版本）时先把它改名挪开再写。</summary>
    static void WriteFile(string path, Stream data)
    {
        try
        {
            using var f = File.Create(path);
            data.CopyTo(f);
        }
        catch (IOException)
        {
            var aside = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".old";
            File.Move(path, aside);
            data.Position = 0;
            using var f = File.Create(path);
            data.CopyTo(f);
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    /// <summary>用系统自带的 WScript.Shell 创建快捷方式（后期绑定，不需要额外引用）。</summary>
    static void CreateShortcut(string lnk, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(type);
        try
        {
            object sc = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var t = sc.GetType();
            void Set(string prop, object value) => t.InvokeMember(prop, BindingFlags.SetProperty, null, sc, new[] { value });
            Set("TargetPath", target);
            Set("WorkingDirectory", System.IO.Path.GetDirectoryName(target));
            Set("IconLocation", target + ",0");
            Set("Description", "G20S 飞鼠遥控器 → AI 对话 / 编程遥控中枢");
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            Marshal.FinalReleaseComObject(sc);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

/// <summary>安装 / 卸载窗口：一屏选项 → 进行中 → 完成。</summary>
sealed class SetupWindow : Window
{
    static readonly Brush SwitchOff = Freeze(new SolidColorBrush(Theme.Dark ? Color.FromRgb(0x39, 0x39, 0x3D) : Color.FromRgb(0xE5, 0xE5, 0xEA)));

    readonly bool uninstall;
    readonly Options o;
    readonly TextBlock subtitle, status;
    readonly ContentControl body = new();
    readonly StackPanel footer;
    bool busy;
    Action primary;

    public SetupWindow(bool uninstall, Options o)
    {
        this.uninstall = uninstall;
        this.o = o;
        Title = uninstall ? "卸载 G20S Hub" : "安装 G20S Hub";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Grouped;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => DarkTitleBar();
        Closing += (_, e) => e.Cancel = busy;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !busy) Close();
            else if (e.Key == Key.Enter && !busy) primary?.Invoke();
        };

        var page = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var icon = new Image { Width = 64, Height = 64, Source = LoadIcon(), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        header.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(uninstall ? "卸载 G20S Hub" : "G20S Hub", 22, FontWeights.SemiBold, Theme.Label));
        titles.Children.Add(Wrap(Text($"版本 {SetupInfo.Version} · 把 G20S 飞鼠遥控器变成 AI 对话 / 编程的遥控器", 13, FontWeights.Normal, Theme.Label2), new Thickness(0, 2, 0, 0)));
        subtitle = Wrap(Text("", 13, FontWeights.Normal, Theme.Label2), new Thickness(0, 2, 0, 0));
        titles.Children.Add(subtitle);
        header.Children.Add(titles);
        page.Children.Add(header);

        page.Children.Add(body);
        status = Wrap(Text("", 12, FontWeights.Normal, Theme.Label2), new Thickness(16, 8, 0, 0));
        status.Visibility = Visibility.Collapsed;
        page.Children.Add(status);

        footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        page.Children.Add(footer);
        Content = page;

        if (uninstall) ShowUninstall();
        else ShowInstall();
    }

    // ---------------- 三个页面 ----------------

    void ShowInstall()
    {
        var existing = Installer.InstalledVersion;
        Note(subtitle, existing == null ? "" :
            existing == SetupInfo.Version ? "已经安装了这个版本，将重新安装。" : $"已安装 {existing}，将更新到 {SetupInfo.Version}。");

        var rows = new StackPanel();
        rows.Children.Add(InfoRow("安装位置", o.Dir));
        rows.Children.Add(Hairline(16));
        rows.Children.Add(SwitchRow("开机自动启动", o.AutoStart, v => o.AutoStart = v));
        rows.Children.Add(Hairline(16));
        rows.Children.Add(SwitchRow("桌面快捷方式", o.Desktop, v => o.Desktop = v));
        rows.Children.Add(Hairline(16));
        rows.Children.Add(SwitchRow("安装完成后启动", o.Run, v => o.Run = v));

        var panel = new StackPanel();
        panel.Children.Add(Card(rows));
        panel.Children.Add(Wrap(Text("只为当前用户安装，不需要管理员权限。配置、圆环设置和日志单独保存，卸载时可以选择保留。",
            12, FontWeights.Normal, Theme.Label2), new Thickness(16, 8, 16, 0)));
        body.Content = panel;

        SetButtons(("取消", false, Close), (existing == null ? "安装" : "更新", true, () => Run(Installer.Install, Installed)));
    }

    void ShowUninstall()
    {
        Note(subtitle, "");
        var rows = new StackPanel();
        rows.Children.Add(InfoRow("安装位置", o.Dir));
        rows.Children.Add(Hairline(16));
        rows.Children.Add(SwitchRow("同时删除我的配置和圆环设置", o.Purge, v => o.Purge = v, Installer.DataDir));
        body.Content = Card(rows);
        SetButtons(("取消", false, Close), ("卸载", true, () => Run(Installer.Uninstall, Uninstalled)));
    }

    void Installed()
    {
        Note(subtitle, "");
        body.Content = Done("安装完成",
            o.Run ? "G20S Hub 已在运行，图标在任务栏右下角的托盘里（没看到就点一下 ^）。按遥控器 ☰ 设置窗口圆环；托盘菜单里可以打开日志、暂停或退出。"
                  : "可以从开始菜单打开 G20S Hub。按遥控器 ☰ 设置窗口圆环；托盘菜单里可以打开日志、暂停或退出。");
        SetButtons(("完成", true, Close));
    }

    void Uninstalled()
    {
        Note(subtitle, "");
        body.Content = Done("已卸载 G20S Hub",
            o.Purge ? "程序和你的配置都已删除。" : $"你的配置还保留在 {Installer.DataDir}，重新安装后会继续使用。");
        SetButtons(("完成", true, Close));
    }

    /// <summary>后台执行安装 / 卸载，期间不能关窗口；失败时停在当前页并显示原因。</summary>
    async void Run(Action<Options, Action<string>> work, Action done)
    {
        busy = true;
        footer.IsEnabled = false;
        footer.Opacity = 0.4;
        status.Foreground = Theme.Label2;
        try
        {
            await Task.Run(() => work(o, msg => Dispatcher.BeginInvoke(new Action(() => Note(status, msg)))));
            Note(status, "");
            done();
        }
        catch (Exception e)
        {
            status.Foreground = Theme.Red;
            Note(status, (uninstall ? "卸载失败：" : "安装失败：") + e.Message);
        }
        finally
        {
            busy = false;
            footer.IsEnabled = true;
            footer.Opacity = 1;
        }
    }

    /// <summary>把安装 / 完成 / 卸载 / 已卸载四个页面画成 PNG（2 倍分辨率），不显示窗口。</summary>
    public static void Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        void Shot(bool uninstall, string name, Action<SetupWindow> page = null)
        {
            var w = new SetupWindow(uninstall, new Options { Dir = Installer.DefaultDir });
            page?.Invoke(w);
            var content = (UIElement)w.Content;
            w.Content = null;
            var host = new Border { Background = Theme.Grouped, Child = content, Width = 524 };
            host.Measure(new Size(524, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * 2), (int)Math.Ceiling(host.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(System.IO.Path.Combine(dir, name)))
                enc.Save(f);
            w.Close();
        }
        Shot(false, "install.png");
        Shot(false, "installed.png", w => w.Installed());
        Shot(true, "uninstall.png");
        Shot(true, "uninstalled.png", w => w.Uninstalled());
    }

    // ---------------- 组件 ----------------

    void SetButtons(params (string text, bool primary, Action click)[] buttons)
    {
        footer.Children.Clear();
        primary = null;
        foreach (var (text, isPrimary, click) in buttons)
        {
            var danger = uninstall && text == "卸载";
            footer.Children.Add(Pill(text, isPrimary ? (danger ? Theme.Red : Theme.Tint) : Theme.Fill, isPrimary ? Theme.OnTint : Theme.Label, click));
            if (isPrimary) primary = click;
        }
    }

    static UIElement Done(string title, string detail)
    {
        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var check = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) };
        check.Children.Add(new Ellipse { Fill = Theme.Tint });
        check.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 6.5,11.5 L 9.8,14.6 L 15.8,7.8"),
            Stroke = Theme.OnTint,
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        });
        line.Children.Add(check);
        var t = Text(title, 15, FontWeights.SemiBold, Theme.Label);
        t.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(t);
        panel.Children.Add(line);
        panel.Children.Add(Wrap(Text(detail, 13, FontWeights.Normal, Theme.Label2), new Thickness(32, 6, 0, 0)));
        return Card(panel);
    }

    static UIElement InfoRow(string label, string value)
    {
        var row = Row(label);
        var v = Text(value, 13, FontWeights.Normal, Theme.Label2);
        v.TextTrimming = TextTrimming.CharacterEllipsis;
        v.ToolTip = value;
        v.VerticalAlignment = VerticalAlignment.Center;
        v.HorizontalAlignment = HorizontalAlignment.Right;
        v.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(v, 1);
        row.Children.Add(v);
        return row;
    }

    /// <summary>一行文字 + 右侧开关；点整行都能切换，Tab 到开关后空格也能切换。</summary>
    static UIElement SwitchRow(string label, bool on, Action<bool> changed, string caption = null)
    {
        var row = Row(label, caption);
        var knob = new Ellipse
        {
            Width = 22, Height = 22, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2),
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.22 },
        };
        var move = new TranslateTransform(on ? 18 : 0, 0);
        knob.RenderTransform = move;
        var track = new Border
        {
            Width = 44, Height = 26, CornerRadius = new CornerRadius(13), Child = knob, Focusable = true,
            Background = on ? Theme.Tint : SwitchOff, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            FocusVisualStyle = null,
        };
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        void Toggle()
        {
            on = !on;
            track.Background = on ? Theme.Tint : SwitchOff;
            var to = on ? 18 : 0;
            if (Theme.Motion)
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else
                move.X = to;
            changed(on);
        }
        row.Background = Brushes.Transparent;  // 整行可点
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, _) => Toggle();
        track.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space) return;
            Toggle();
            e.Handled = true;
        };
        return row;
    }

    static Grid Row(string label, string caption = null)
    {
        var row = new Grid { MinHeight = 46, Margin = new Thickness(16, 0, 16, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        texts.Children.Add(Text(label, 14, FontWeights.Normal, Theme.Label));
        if (caption != null)
            texts.Children.Add(Wrap(Text(caption, 12, FontWeights.Normal, Theme.Label2), new Thickness(0, 2, 0, 0)));
        row.Children.Add(texts);
        return row;
    }

    static Border Card(UIElement child) =>
        new() { Background = Theme.Elevated, CornerRadius = new CornerRadius(12), Child = child };

    static Border Hairline(double inset) =>
        new() { Height = 1, Background = Theme.Separator, Margin = new Thickness(inset, 0, 0, 0) };

    static TextBlock Text(string s, double size, FontWeight weight, Brush color) =>
        new() { Text = s, FontFamily = Theme.Font, FontSize = size, FontWeight = weight, Foreground = color };

    static TextBlock Wrap(TextBlock t, Thickness margin)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.LineHeight = t.FontSize * 1.55;
        t.Margin = margin;
        return t;
    }

    static void Note(TextBlock t, string s)
    {
        t.Text = s;
        t.Visibility = s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static Border Pill(string text, Brush bg, Brush fg, Action click)
    {
        var b = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(22, 7, 22, 8),
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 88,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text, FontFamily = Theme.Font, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    static ImageSource LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup/icon.png");
        if (s == null) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = s;
        img.EndInit();
        img.Freeze();
        return img;
    }

    static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    // 深色模式下标题栏也用深色（Windows 11 / 10 20H1+）
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void DarkTitleBar()
    {
        if (!Theme.Dark) return;
        int on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4);
    }
}
