using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using G20SHub.Ring;

namespace G20SHub.UI;

/// <summary>
/// 自检：G20SHub.exe --preview 目录。用当前打开的窗口拼一组示例格子，
/// 把圆环（两圈）和设置窗口画成 PNG，不显示任何窗口、不写 ring.json。
/// </summary>
static class Preview
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var keys = new[] { "1-up", "1-right", "1-down", "1-left", "2-up", "2-right" };
        var apps = AppCatalog.List()
            .GroupBy(w => w.Exe, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Take(keys.Length).ToList();
        var sample = new Dictionary<string, Slot>();
        for (int i = 0; i < apps.Count; i++)
            sample[keys[i]] = AppCatalog.ToSlot(apps[i]);
        var slots = new RingSlots(Path.Combine(dir, "不存在.json")) { Slots = sample };

        var desktop = new SolidColorBrush(Color.FromRgb(0x5E, 0x6B, 0x7D));
        var ring = new RingWindow(118, 64);
        Save(ring.RenderPreview(slots, 1, desktop), Path.Combine(dir, "ring-page1.png"));
        Save(ring.RenderPreview(slots, 2, desktop), Path.Combine(dir, "ring-page2.png"));
        ring.Close();

        var picker = new PickerWindow(slots);
        var content = (UIElement)picker.Content;
        picker.Content = null;
        var host = new Border { Background = Theme.Grouped, Child = content, Width = 940, Height = 640 };
        host.Measure(new Size(940, 640));
        host.Arrange(new Rect(0, 0, 940, 640));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(940 * 2, 640 * 2, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(host);
        Save(bmp, Path.Combine(dir, "picker.png"));
        picker.Close();
    }

    static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
