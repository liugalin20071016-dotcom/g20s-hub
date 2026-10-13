using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace G20SHub.UI;

/// <summary>
/// 托盘图标（WinForms NotifyIcon，系统自带）：圆环设置、日志窗口、暂停/恢复、开机自启、数据文件夹、退出。
/// 提示文字显示版本（编译时间），方便确认正在运行的是哪个版本。
/// </summary>
sealed class Tray : IDisposable
{
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem pauseItem;
    readonly Icon running, paused;

    public Tray(string version, Action openSettings, Action showLog, Action togglePause, Action restart, Action exit)
    {
        running = MakeIcon(Color.FromArgb(0x00, 0x7A, 0xFF));
        paused = MakeIcon(Color.FromArgb(0x8E, 0x8E, 0x93));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"G20S Hub · {version}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("圆环设置", null, (_, _) => openSettings());
        menu.Items.Add("日志窗口", null, (_, _) => showLog());
        pauseItem = new ToolStripMenuItem("暂停（Ctrl+Alt+F12）", null, (_, _) => togglePause());
        menu.Items.Add(pauseItem);
        menu.Items.Add("重启（遥控器 🔇）", null, (_, _) => restart());
        menu.Items.Add(new ToolStripSeparator());
        var autoStart = new ToolStripMenuItem("开机自启");
        autoStart.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(autoStart);
        menu.Items.Add("打开数据文件夹", null, (_, _) => Process.Start("explorer.exe", $"\"{AppPaths.DataDir}\""));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());
        menu.Opening += (_, _) => autoStart.Checked = AutoStart.IsOn(AppPaths.Exe);

        icon = new NotifyIcon
        {
            Icon = running,
            Text = $"G20S Hub · {version}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => showLog();
    }

    public void SetPaused(bool isPaused)
    {
        icon.Icon = isPaused ? paused : running;
        pauseItem.Text = isPaused ? "恢复（Ctrl+Alt+F12）" : "暂停（Ctrl+Alt+F12）";
    }

    static void ToggleAutoStart()
    {
        bool on = !AutoStart.IsOn(AppPaths.Exe);
        try
        {
            AutoStart.Set(AppPaths.Exe, on);
            Log.Write(on ? $"已开启开机自启：{AppPaths.Exe}" : "已关闭开机自启");
            Osd.Show(on ? "✅ 已开启开机自启" : "已关闭开机自启");
        }
        catch (Exception e)
        {
            Log.Write("!! 设置开机自启失败：" + e.Message);
            Osd.Show("设置开机自启失败");
        }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }

    /// <summary>画一个圆环图标：实心圆底 + 白色小圆环，呼应 ☰ 的窗口圆环。</summary>
    static Icon MakeIcon(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(fill);
            g.FillEllipse(b, 1, 1, 30, 30);
            using var p = new Pen(Color.White, 3.2f);
            g.DrawEllipse(p, 9, 9, 14, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
