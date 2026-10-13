using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using G20SHub.Ring;
using static G20SHub.Native.Win32;

namespace G20SHub.UI;

/// <summary>
/// 窗口圆环（只负责显示）。第 1 圈在上右下左，第 2 圈平时在四个斜角、半透明；
/// 翻圈时整圈逆时针转 45°，图标反向自转保持正立。不抢焦点、不进任务栏。
/// </summary>
sealed class RingWindow : Window
{
    sealed class SlotView
    {
        public FrameworkElement Root;
        public ScaleTransform Scale;
        public RotateTransform Counter;
        public TranslateTransform Shift;
        public TextBlock Caption;
        public bool Empty;
        public int Page;
    }

    static readonly Duration Fast = TimeSpan.FromMilliseconds(170), Flip = TimeSpan.FromMilliseconds(220);

    readonly double radius, disc, size;
    readonly Canvas root, orbit;
    readonly ScaleTransform rootScale = new(1, 1);
    readonly RotateTransform orbitRotate = new(0);
    readonly Dictionary<string, SlotView> views = new();
    readonly Ellipse dot1, dot2;
    readonly TextBlock hint;
    readonly StackPanel center;
    int version;

    public int Page { get; private set; } = 1;

    /// <summary>鼠标点了某个格子（页、方向）。</summary>
    public event Action<int, Dir> Clicked;

    public RingWindow(double radius, double disc)
    {
        this.radius = radius;
        this.disc = disc;
        size = 2 * (radius + disc / 2 + 48);

        Width = Height = size;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        root = new Canvas { Width = size, Height = size, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rootScale };

        double backdrop = 2 * (radius + disc / 2 + 38);
        var plate = new Ellipse
        {
            Width = backdrop,
            Height = backdrop,
            Fill = Theme.Material,
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = Theme.Dark ? 0.55 : 0.18, Color = Colors.Black },
        };
        Place(plate, size / 2, size / 2, backdrop, backdrop);
        root.Children.Add(plate);

        dot1 = Dot();
        dot2 = Dot();
        hint = new TextBlock { FontFamily = Theme.Font, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = Theme.Label3, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        dots.Children.Add(dot1);
        dots.Children.Add(dot2);
        center = new StackPanel();
        center.Children.Add(dots);
        center.Children.Add(hint);
        center.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Children.Add(center);

        orbit = new Canvas { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = orbitRotate };
        root.Children.Add(orbit);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        };
    }

    /// <summary>在 centerDip（设备无关像素）处显示，回到第 1 圈。</summary>
    public void Present(RingSlots slots, Point centerDip)
    {
        version++;
        Build(slots);
        Page = 1;
        orbitRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        orbitRotate.Angle = 0;
        ApplyPage(animate: false);

        bool hasSecond = RingSlots.Keys.Skip(4).Any(slots.Slots.ContainsKey);
        dot1.Visibility = dot2.Visibility = hasSecond ? Visibility.Visible : Visibility.Collapsed;
        hint.Text = hasSecond ? "OK 翻页" : "";
        center.UpdateLayout();
        center.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(center, (size - center.DesiredSize.Width) / 2);
        Canvas.SetTop(center, (size - center.DesiredSize.Height) / 2);

        Left = centerDip.X - size / 2;
        Top = centerDip.Y - size / 2;
        if (!IsVisible)
            Show();
        Animate(root, OpacityProperty, 0, 1, Fast);
        Animate(rootScale, ScaleTransform.ScaleXProperty, 0.92, 1, Fast);
        Animate(rootScale, ScaleTransform.ScaleYProperty, 0.92, 1, Fast);
    }

    /// <summary>翻到另一圈。</summary>
    public void FlipPage()
    {
        Page = Page == 1 ? 2 : 1;
        ApplyPage(animate: true);
    }

    /// <summary>选中某格：图标轻轻放大，然后整圈淡出。</summary>
    public void Pick(int page, Dir dir)
    {
        if (views.TryGetValue(RingSlots.KeyOf(page, dir), out var v))
        {
            Animate(v.Scale, ScaleTransform.ScaleXProperty, null, 1.14, TimeSpan.FromMilliseconds(110));
            Animate(v.Scale, ScaleTransform.ScaleYProperty, null, 1.14, TimeSpan.FromMilliseconds(110));
        }
        Dismiss(delayMs: 90);
    }

    /// <summary>空格子：左右轻摇一下。</summary>
    public void Shake(int page, Dir dir)
    {
        if (!views.TryGetValue(RingSlots.KeyOf(page, dir), out var v) || !Theme.Motion)
            return;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220) };
        foreach (var (x, t) in new[] { (-5.0, 45), (5.0, 100), (-3.0, 150), (0.0, 220) })
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        v.Shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>淡出后隐藏。</summary>
    public void Dismiss(int delayMs = 0)
    {
        int my = ++version;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Theme.Motion ? 140 : 1))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (my == version)
                Hide();
        };
        root.BeginAnimation(OpacityProperty, fade);
        Animate(rootScale, ScaleTransform.ScaleXProperty, null, 0.96, TimeSpan.FromMilliseconds(140 + delayMs));
        Animate(rootScale, ScaleTransform.ScaleYProperty, null, 0.96, TimeSpan.FromMilliseconds(140 + delayMs));
    }

    /// <summary>自检用：不显示窗口，直接把指定页的圆环画成位图（放在 backdrop 色背景上）。</summary>
    public System.Windows.Media.Imaging.BitmapSource RenderPreview(RingSlots slots, int page, Brush backdrop)
    {
        Present(slots, new Point(-10000, -10000));
        Hide();
        Page = page;
        Animate(root, OpacityProperty, null, 1, TimeSpan.Zero);
        Animate(rootScale, ScaleTransform.ScaleXProperty, null, 1, TimeSpan.Zero);
        Animate(rootScale, ScaleTransform.ScaleYProperty, null, 1, TimeSpan.Zero);
        ApplyPage(animate: false);
        Content = null;
        var host = new Grid { Width = size, Height = size, Background = backdrop };
        host.Children.Add(root);
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        host.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(size * 2), (int)(size * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(host);
        host.Children.Clear();
        Content = root;
        return bmp;
    }

    // ---------------- 构建 ----------------

    void Build(RingSlots slots)
    {
        orbit.Children.Clear();
        views.Clear();
        foreach (var key in RingSlots.Keys)
        {
            int page = key[0] - '0';
            var dir = (Dir)Array.IndexOf(new[] { "up", "right", "down", "left" }, key.Substring(2));
            slots.Slots.TryGetValue(key, out var slot);
            var v = MakeView(slot, page, dir);
            double a = Angle(page, dir) * Math.PI / 180;
            double cx = size / 2 + radius * Math.Cos(a), cy = size / 2 + radius * Math.Sin(a);
            Place(v.Root, cx, cy, v.Root.Width, v.Root.Height);
            orbit.Children.Add(v.Root);
            views[key] = v;
        }
    }

    SlotView MakeView(Slot slot, int page, Dir dir)
    {
        double w = disc + 48, h = disc + 2 * 22;
        var grid = new Grid { Width = w, Height = h, RenderTransformOrigin = new Point(0.5, 0.5) };
        var v = new SlotView { Root = grid, Scale = new ScaleTransform(1, 1), Counter = new RotateTransform(0), Shift = new TranslateTransform(), Empty = slot == null, Page = page };
        var group = new TransformGroup();
        group.Children.Add(v.Scale);
        group.Children.Add(v.Counter);
        group.Children.Add(v.Shift);
        grid.RenderTransform = group;

        var face = new Grid { Width = disc, Height = disc, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (slot == null)
        {
            face.Children.Add(new Ellipse { Stroke = Theme.Label3, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 3 } });
        }
        else
        {
            face.Children.Add(new Ellipse
            {
                Fill = Theme.Raised,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = Theme.Dark ? 0.45 : 0.14, Color = Colors.Black },
            });
            var icon = AppCatalog.Icon(slot);
            if (icon != null)
                face.Children.Add(new Image { Source = icon, Width = disc * 0.6, Height = disc * 0.6 });
            else
                face.Children.Add(new TextBlock
                {
                    Text = slot.Name.Length > 0 ? slot.Name.Substring(0, 1) : "?",
                    FontFamily = Theme.Font, FontSize = disc * 0.36, FontWeight = FontWeights.SemiBold, Foreground = Theme.Label,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
            face.Cursor = System.Windows.Input.Cursors.Hand;
            face.MouseLeftButtonUp += (_, _) => Clicked?.Invoke(page, dir);
        }
        grid.Children.Add(face);

        v.Caption = new TextBlock
        {
            Text = slot?.Name ?? "",
            FontFamily = Theme.Font, FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = Theme.Label2,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = w, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        };
        grid.Children.Add(v.Caption);
        return v;
    }

    /// <summary>格子的角度（度，0 = 右，顺时针为正）。第 2 圈在斜角：↗↘↙↖。</summary>
    static double Angle(int page, Dir dir)
    {
        double a = dir switch { Dir.Up => -90, Dir.Right => 0, Dir.Down => 90, _ => 180 };
        return page == 1 ? a : a + 45;
    }

    void ApplyPage(bool animate)
    {
        double orbitAngle = Page == 1 ? 0 : -45;
        var d = animate ? Flip : new Duration(TimeSpan.Zero);
        Animate(orbitRotate, RotateTransform.AngleProperty, null, orbitAngle, d, inOut: true);
        foreach (var v in views.Values)
        {
            bool active = v.Page == Page;
            Animate(v.Counter, RotateTransform.AngleProperty, null, -orbitAngle, d, inOut: true);
            Animate(v.Root, OpacityProperty, null, active ? 1 : (v.Empty ? 0 : 0.36), d);
            Animate(v.Scale, ScaleTransform.ScaleXProperty, null, active ? 1 : 0.8, d);
            Animate(v.Scale, ScaleTransform.ScaleYProperty, null, active ? 1 : 0.8, d);
            Animate(v.Caption, OpacityProperty, null, active ? 1 : 0, d);
        }
        dot1.Fill = Page == 1 ? Theme.Label : Theme.Label3;
        dot2.Fill = Page == 2 ? Theme.Label : Theme.Label3;
    }

    static Ellipse Dot() => new() { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0), Fill = Theme.Label3 };

    static void Place(UIElement e, double cx, double cy, double w, double h)
    {
        Canvas.SetLeft(e, cx - w / 2);
        Canvas.SetTop(e, cy - h / 2);
    }

    static void Animate(IAnimatable target, DependencyProperty prop, double? from, double to, Duration d, bool inOut = false)
    {
        if (!Theme.Motion || d == new Duration(TimeSpan.Zero))
        {
            // 不做动画：清掉进行中的动画，直接设成终值
            target.BeginAnimation(prop, null);
            ((DependencyObject)target).SetValue(prop, to);
            return;
        }
        var anim = new DoubleAnimation(to, d)
        {
            EasingFunction = new CubicEase { EasingMode = inOut ? EasingMode.EaseInOut : EasingMode.EaseOut },
        };
        if (from.HasValue)
            anim.From = from.Value;
        target.BeginAnimation(prop, anim);
    }
}
