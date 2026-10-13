using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace G20SHub.UI;

/// <summary>系统配色（苹果风格的语义色），跟随 Windows 深浅色模式。</summary>
static class Theme
{
    public static readonly bool Dark = ReadDark();

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

    public static readonly Brush Grouped = B(Dark ? 0xFF000000 : 0xFFF2F2F7);
    public static readonly Brush Elevated = B(Dark ? 0xFF1C1C1E : 0xFFFFFFFF);
    public static readonly Brush Raised = B(Dark ? 0xFF2C2C2E : 0xFFFFFFFF);
    public static readonly Brush Label = B(Dark ? 0xFFFFFFFF : 0xE0000000);
    public static readonly Brush Label2 = B(Dark ? 0x9EEBEBF5 : 0x9E3C3C43);
    public static readonly Brush Label3 = B(Dark ? 0x52EBEBF5 : 0x523C3C43);
    public static readonly Brush Separator = B(Dark ? 0x99545458 : 0x2E3C3C43);
    public static readonly Brush Fill = B(Dark ? 0x3D787880 : 0x1F787880);
    public static readonly Brush Tint = B(Dark ? 0xFF0A84FF : 0xFF007AFF);
    public static readonly Brush Red = B(Dark ? 0xFFFF453A : 0xFFFF3B30);
    public static readonly Brush OnTint = B(0xFFFFFFFF);

    /// <summary>浮层材质：WPF 透明窗口做不了背景模糊，用高不透明度的抬升色代替。</summary>
    public static readonly Brush Material = B(Dark ? 0xE61C1C1E : 0xEBF9F9FB);

    /// <summary>系统关闭了动画（辅助功能设置）时不做动画。</summary>
    public static bool Motion => SystemParameters.ClientAreaAnimation;

    static Brush B(long argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    static bool ReadDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
