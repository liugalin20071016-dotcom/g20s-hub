using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using G20SHub.Ring;
using G20SHub.UI;
using static G20SHub.Native.Win32;

namespace G20SHub.Actions;

/// <summary>
/// 窗口圆环的行为：☰ 呼出；方向键直接打开当前圈的格子；OK 翻圈；
/// 再按 ☰ 或 ↩ 取消；一段时间没按键自动取消。打开期间通知输入层接管方向键与 OK。
/// </summary>
sealed class RingController
{
    readonly RingSlots slots;
    readonly RingWindow view;
    readonly DispatcherTimer timeout;
    readonly bool atWindowCenter;
    PickerWindow picker;

    /// <summary>圆环打开 / 关闭（输入层据此接管方向键与 OK）。</summary>
    public event Action<bool> ModalChanged;

    public bool Visible { get; private set; }

    public RingController(Config cfg, string root)
    {
        slots = new RingSlots(Path.Combine(root, "ring.json"));
        view = new RingWindow(cfg.Num("ring.radius", 118), cfg.Num("ring.icon_size", 64));
        view.Clicked += (page, dir) => Choose(page, dir);
        atWindowCenter = cfg.Str("ring.position", "screen_center") == "window_center";
        timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(cfg.Num("ring.timeout_ms", 4000)) };
        timeout.Tick += (_, _) => Cancel("超时");
        Log.Write($"窗口圆环：{slots.Slots.Count} 个格子");
    }

    /// <summary>☰ 单击：打开；已打开则取消。还没设置任何格子时直接打开设置。</summary>
    public void Toggle()
    {
        if (Visible)
        {
            Cancel("再按 ☰");
            return;
        }
        slots.Load();  // 设置窗口可能刚保存过
        if (slots.IsEmpty)
        {
            Osd.Show("◎ 先把常用窗口放进圆环");
            OpenSettings();
            return;
        }
        SetVisible(true);
        view.Present(slots, Anchor());
        Log.Write("[圆环] 打开");
    }

    /// <summary>方向键：打开当前圈对应的格子。</summary>
    public void Press(Dir dir) => Choose(view.Page, dir);

    /// <summary>OK：翻圈。</summary>
    public void Flip()
    {
        if (!Visible) return;
        timeout.Stop();
        timeout.Start();
        view.FlipPage();
    }

    public void Cancel(string why)
    {
        if (!Visible) return;
        SetVisible(false);
        view.Dismiss();
        Log.Write($"[圆环] 取消（{why}）");
    }

    /// <summary>☰ 长按：圆环设置。</summary>
    public void OpenSettings()
    {
        Cancel("打开设置");
        if (picker == null)
        {
            picker = new PickerWindow(slots);
            picker.Closed += (_, _) => picker = null;
            picker.Show();
        }
        picker.Activate();
        WindowTarget.Activate(new WindowInteropHelper(picker).Handle);
    }

    void Choose(int page, Dir dir)
    {
        if (!Visible) return;
        timeout.Stop();
        timeout.Start();
        var slot = slots.Get(page, dir);
        if (slot == null)
        {
            view.Shake(page, dir);
            return;
        }
        SetVisible(false);
        view.Pick(page, dir);
        string result = AppCatalog.Open(slot);
        Log.Write($"[圆环] {RingSlots.KeyOf(page, dir)} → {result}");
        Osd.Show(result.StartsWith("⚠") ? result : "→ " + result);
    }

    void SetVisible(bool visible)
    {
        Visible = visible;
        if (visible) timeout.Start();
        else timeout.Stop();
        ModalChanged?.Invoke(visible);
    }

    /// <summary>圆环中心（设备无关像素）：屏幕工作区中央，或前台窗口中央。</summary>
    Point Anchor()
    {
        if (atWindowCenter && GetWindowRect(GetForegroundWindow(), out var r) && r.Right > r.Left)
        {
            double scale = 96.0 / Math.Max(96, GetDpiForSystem());
            return new Point((r.Left + r.Right) / 2.0 * scale, (r.Top + r.Bottom) / 2.0 * scale);
        }
        var area = SystemParameters.WorkArea;
        return new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
    }
}
