using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static G20SHub.Native.Win32;

namespace G20SHub.UI;

/// <summary>
/// 屏幕下方的半透明提示条。不抢焦点、鼠标穿透、不进任务栏。
/// 只能在界面线程调用。
/// </summary>
sealed class Osd : Window
{
    static Osd instance;

    readonly TextBlock text;
    readonly DispatcherTimer hideTimer;

    Osd()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;

        text = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 10, 20, 11),
            Child = text,
        };

        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); FadeTo(0); };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW));
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>显示提示。sticky = true 时一直显示，直到下一次 Show 或 Hide。</summary>
    public static void Show(string message, bool sticky = false)
    {
        instance ??= new Osd();
        instance.Present(message, sticky);
    }

    public static new void Hide() => instance?.FadeTo(0);

    void Present(string message, bool sticky)
    {
        text.Text = message;
        if (!IsVisible)
            base.Show();
        Place();
        FadeTo(1);
        hideTimer.Stop();
        if (!sticky)
            hideTimer.Start();
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 72;
    }

    void FadeTo(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 110 : 220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, anim);
    }
}
