using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using G20SHub.Input;

namespace G20SHub.UI;

/// <summary>P1 诊断窗口：实时显示输入层日志与统计。后续阶段收进托盘。</summary>
sealed class DiagWindow : Window
{
    const int MaxChars = 200_000;

    /// <summary>程序真正退出时置 true，窗口才允许关闭。</summary>
    public static bool Exiting;

    readonly InputThread input;
    readonly TextBlock status;
    readonly TextBox box;

    public DiagWindow(InputThread input, bool dryRun)
    {
        this.input = input;
        Title = $"G20S Hub 日志 · {BuildInfo.Label}" + (dryRun ? "（dry-run）" : "");
        // 关闭只是隐藏，程序继续在托盘运行；从托盘「日志窗口」再打开，「退出」才真正退出
        Closing += (_, e) =>
        {
            if (Exiting)
                return;
            e.Cancel = true;
            Hide();
        };
        Width = 960;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        status = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        box = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(status, Dock.Top);
        panel.Children.Add(status);
        panel.Children.Add(box);
        Content = panel;

        Log.Line += line => Dispatcher.BeginInvoke(new Action(() => Append(line)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();
        UpdateStatus();
    }

    void Append(string line)
    {
        if (box.Text.Length > MaxChars)
            box.Text = box.Text.Substring(box.Text.Length / 2);
        box.AppendText(line + "\r\n");
        box.ScrollToEnd();
    }

    void UpdateStatus()
    {
        var m = input.Media;
        status.Text =
            $"{(input.Paused ? "⏸ 已暂停" : "▶ 运行中")}　｜　遥控器拦截 {m.Blocked}（连发 {m.RepeatsBlocked}）" +
            $"　｜　真键盘放行 {m.Passed}　｜　钩子先到警告 {m.LateWarnings}　｜　钩子内取出 Raw {input.DrainedInHook}" +            "　｜　真键盘 Ctrl+Alt+F12 暂停/恢复";
    }
}
