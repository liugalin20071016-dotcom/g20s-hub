using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using G20SHub.Ring;

namespace G20SHub.UI;

/// <summary>
/// 圆环设置（☰ 长按）：左边是 8 个格子的示意图，右边是当前打开的窗口。
/// 先点格子，再点窗口把它放进去；可改名称、标题包含、启动方式。保存写入 ring.json。
/// </summary>
sealed class PickerWindow : Window
{
    sealed class SlotButton
    {
        public Grid Root;
        public Ellipse Ring;
        public Grid Face;
        public TextBlock Caption;
        public FrameworkElement Remove;
    }

    static readonly string[] DirNames = { "↑", "→", "↓", "←" };

    readonly RingSlots store;
    readonly Dictionary<string, Slot> edit;
    readonly Dictionary<string, SlotButton> buttons = new();
    readonly StackPanel windowList = new();
    readonly TextBox nameBox, titleBox, launchBox;
    readonly TextBlock slotTitle;
    string selected = RingSlots.Keys[0];
    bool loading;

    public PickerWindow(RingSlots store)
    {
        this.store = store;
        edit = store.Slots.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());

        Title = "圆环设置 · G20S Hub";
        Width = 940;
        Height = 680;
        MinWidth = 820;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Grouped;
        FontFamily = Theme.Font;

        var page = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(Text("圆环设置", 22, FontWeights.SemiBold, Theme.Label));
        header.Children.Add(Wrap(Text("先点左边的格子，再点右边的窗口把它放进去；点格子右上角的 × 清空。不用的方向空着就行。",
            13, FontWeights.Normal, Theme.Label2), new Thickness(0, 4, 0, 0)));
        page.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        page.Children.Add(body);

        // 左：格子示意图 + 这个格子
        var left = new StackPanel();
        left.Children.Add(SectionHeader("格子"));
        left.Children.Add(Card(BuildDiagram(), new Thickness(12)));

        slotTitle = Text("", 12, FontWeights.Normal, Theme.Label2);
        slotTitle.Margin = new Thickness(16, 20, 0, 6);
        left.Children.Add(slotTitle);

        nameBox = Field();
        titleBox = Field();
        launchBox = Field();
        var fields = new StackPanel();
        fields.Children.Add(FieldRow("名称", nameBox));
        fields.Children.Add(Hairline(16));
        fields.Children.Add(FieldRow("标题包含", titleBox));
        fields.Children.Add(Hairline(16));
        fields.Children.Add(FieldRow("启动方式", launchBox));
        left.Children.Add(Card(fields, new Thickness(0)));
        left.Children.Add(Wrap(Text("标题包含留空 = 这个程序的任意窗口。启动方式用于窗口没打开时，可以是程序、快捷方式或网址。",
            12, FontWeights.Normal, Theme.Label2), new Thickness(16, 6, 0, 0)));
        body.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        nameBox.TextChanged += (_, _) => UpdateField(s => s.Name = nameBox.Text);
        titleBox.TextChanged += (_, _) => UpdateField(s => s.TitleContains = titleBox.Text.Trim());
        launchBox.TextChanged += (_, _) => UpdateField(s => s.Launch = launchBox.Text.Trim());

        // 右：打开的窗口
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var listHeader = new DockPanel();
        var refresh = Plain("刷新", Theme.Tint, RefreshWindows);
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Margin = new Thickness(0, 0, 4, 6);
        listHeader.Children.Add(refresh);
        listHeader.Children.Add(SectionHeader("打开的窗口"));
        right.Children.Add(listHeader);
        var scroller = new ScrollViewer { Content = windowList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var listCard = Card(scroller, new Thickness(0, 4, 0, 4));
        Grid.SetRow(listCard, 1);
        right.Children.Add(listCard);
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        // 底部按钮
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        footer.Children.Add(Pill("取消", Theme.Fill, Theme.Label, Close));
        footer.Children.Add(Pill("保存", Theme.Tint, Theme.OnTint, Save));
        Grid.SetRow(footer, 2);
        page.Children.Add(footer);

        Content = page;
        KeyDown +=(_, e) => { if (e.Key == Key.Escape) Close(); };
        Select(selected);
        RefreshWindows();
    }

    // ---------------- 格子示意图 ----------------

    /// <summary>两行格子：第 1 圈、第 2 圈，各按 ↑ → ↓ ← 排列。</summary>
    UIElement BuildDiagram()
    {
        const double disc = 52;
        var panel = new StackPanel();
        for (int pageNo = 1; pageNo <= 2; pageNo++)
        {
            var header = Text(pageNo == 1 ? "第 1 圈 · 方向键直接打开" : "第 2 圈 · 先按 OK 翻页", 12, FontWeights.Normal, Theme.Label2);
            header.Margin = new Thickness(4, pageNo == 1 ? 0 : 12, 0, 8);
            panel.Children.Add(header);
            var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
            for (int d = 0; d < 4; d++)
            {
                string key = RingSlots.Keys[(pageNo - 1) * 4 + d];
                var b = new SlotButton
                {
                    Root = new Grid { Height = disc + 32, Cursor = Cursors.Hand, Background = Brushes.Transparent },
                    Ring = new Ellipse { Width = disc + 8, Height = disc + 8, Stroke = Theme.Tint, StrokeThickness = 2.5, VerticalAlignment = VerticalAlignment.Top },
                    Face = new Grid { Width = disc, Height = disc, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) },
                    Caption = Text("", 11, FontWeights.Medium, Theme.Label2),
                };
                b.Caption.VerticalAlignment = VerticalAlignment.Bottom;
                b.Caption.HorizontalAlignment = HorizontalAlignment.Center;
                b.Caption.TextTrimming = TextTrimming.CharacterEllipsis;
                b.Caption.Margin = new Thickness(2, 0, 2, 0);
                b.Root.Children.Add(b.Ring);
                b.Root.Children.Add(b.Face);
                b.Root.Children.Add(b.Caption);
                // 右上角的 ×：清空这个格子并选中它，接着点右边的窗口就能放进别的
                b.Remove = RemoveBadge(() => ClearSlot(key));
                var corner = new Grid { Width = disc + 14, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, 0, 0) };
                corner.Children.Add(b.Remove);
                b.Root.Children.Add(corner);
                b.Root.MouseLeftButtonUp += (_, _) => Select(key);
                row.Children.Add(b.Root);
                buttons[key] = b;
            }
            panel.Children.Add(row);
        }
        RefreshDiagram();
        return panel;
    }

    void RefreshDiagram()
    {
        for (int i = 0; i < RingSlots.Keys.Length; i++)
        {
            string key = RingSlots.Keys[i];
            var b = buttons[key];
            b.Face.Children.Clear();
            b.Ring.Visibility = key == selected ? Visibility.Visible : Visibility.Hidden;
            b.Remove.Visibility = edit.ContainsKey(key) ? Visibility.Visible : Visibility.Collapsed;
            if (edit.TryGetValue(key, out var s))
            {
                b.Face.Children.Add(new Ellipse
                {
                    Fill = Theme.Raised,
                    Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Direction = 270, Opacity = Theme.Dark ? 0.4 : 0.12, Color = Colors.Black },
                });
                var icon = AppCatalog.Icon(s);
                if (icon != null)
                    b.Face.Children.Add(new Image { Source = icon, Width = 30, Height = 30 });
                b.Caption.Text = $"{DirNames[i % 4]} {s.Name}";
                b.Caption.Foreground = Theme.Label2;
            }
            else
            {
                b.Face.Children.Add(new Ellipse { Fill = Theme.Fill });
                var plus = Text("+", 20, FontWeights.Light, Theme.Label3);
                plus.HorizontalAlignment = HorizontalAlignment.Center;
                plus.VerticalAlignment = VerticalAlignment.Center;
                b.Face.Children.Add(plus);
                b.Caption.Text = DirNames[i % 4];
                b.Caption.Foreground = Theme.Label3;
            }
        }
    }

    void Select(string key)
    {
        selected = key;
        int i = Array.IndexOf(RingSlots.Keys, key);
        slotTitle.Text = $"第 {i / 4 + 1} 圈 · {DirNames[i % 4]}";
        loading = true;
        edit.TryGetValue(key, out var s);
        nameBox.Text = s?.Name ?? "";
        titleBox.Text = s?.TitleContains ?? "";
        launchBox.Text = s?.Launch ?? "";
        nameBox.IsEnabled = titleBox.IsEnabled = launchBox.IsEnabled = s != null;
        loading = false;
        RefreshDiagram();
    }

    void UpdateField(Action<Slot> set)
    {
        if (loading || !edit.TryGetValue(selected, out var s))
            return;
        set(s);
        buttons[selected].Caption.Text = $"{DirNames[Array.IndexOf(RingSlots.Keys, selected) % 4]} {s.Name}";
    }

    void ClearSlot(string key)
    {
        edit.Remove(key);
        Select(key);
    }

    /// <summary>放进当前选中的格子；位置由用户决定，不自动跳格，其他格子可以空着。</summary>
    void Assign(AppWindow w)
    {
        edit[selected] = AppCatalog.ToSlot(w);
        Select(selected);
    }

    void Save()
    {
        store.Save(edit);
        Osd.Show($"◎ 圆环已保存（{edit.Count} 个格子）");
        Close();
    }

    // ---------------- 窗口列表 ----------------

    void RefreshWindows()
    {
        windowList.Children.Clear();
        var windows = AppCatalog.List();
        if (windows.Count == 0)
        {
            windowList.Children.Add(Wrap(Text("没有找到可切换的窗口。", 13, FontWeights.Normal, Theme.Label2), new Thickness(16, 14, 16, 14)));
            return;
        }
        for (int i = 0; i < windows.Count; i++)
        {
            if (i > 0)
                windowList.Children.Add(Hairline(56));
            windowList.Children.Add(WindowRow(windows[i]));
        }
    }

    UIElement WindowRow(AppWindow w)
    {
        var row = new Grid { MinHeight = 52, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = AppCatalog.Icon(w);
        if (icon != null)
            row.Children.Add(new Image { Source = icon, Width = 28, Height = 28 });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 16, 8) };
        texts.Children.Add(Text(w.DisplayName, 14, FontWeights.Medium, Theme.Label));
        var title = Text(w.Title, 12, FontWeights.Normal, Theme.Label2);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        row.MouseEnter += (_, _) => row.Background = Theme.Fill;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => Assign(w);
        return row;
    }

    // ---------------- 小部件 ----------------

    /// <summary>格子右上角的删除按钮：灰色小圆 + 白色 ×，点它只清空，不触发格子的选中。</summary>
    static FrameworkElement RemoveBadge(Action click)
    {
        var cross = new Path
        {
            Data = Geometry.Parse("M0,0 L7,7 M7,0 L0,7"),
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Grid
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "清空这个格子",
        };
        badge.Children.Add(new Ellipse { Fill = new SolidColorBrush(Theme.Dark ? Color.FromRgb(0x63, 0x63, 0x66) : Color.FromRgb(0x8E, 0x8E, 0x93)) });
        badge.Children.Add(cross);
        badge.MouseEnter += (_, _) => badge.Opacity = 0.8;
        badge.MouseLeave += (_, _) => badge.Opacity = 1;
        badge.MouseLeftButtonDown += (_, e) => e.Handled = true;
        badge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return badge;
    }

    static TextBlock Text(string s, double size, FontWeight weight, Brush color) =>
        new() { Text = s, FontFamily = Theme.Font, FontSize = size, FontWeight = weight, Foreground = color };

    static TextBlock Wrap(TextBlock t, Thickness margin)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.LineHeight = t.FontSize * 1.6;
        t.Margin = margin;
        return t;
    }

    static TextBlock SectionHeader(string s)
    {
        var t = Text(s, 12, FontWeights.Normal, Theme.Label2);
        t.Margin = new Thickness(16, 0, 0, 6);
        return t;
    }

    static Border Card(UIElement child, Thickness padding) =>
        new() { Background = Theme.Elevated, CornerRadius = new CornerRadius(12), Padding = padding, Child = child };

    static Border Hairline(double inset) =>
        new() { Height = 1, Background = Theme.Separator, Margin = new Thickness(inset, 0, 0, 0) };

    static TextBox Field() => new()
    {
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = Theme.Label,
        CaretBrush = Theme.Label,
        FontFamily = Theme.Font,
        FontSize = 14,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    static UIElement FieldRow(string label, TextBox box)
    {
        var row = new Grid { MinHeight = 44, Margin = new Thickness(16, 0, 16, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = Text(label, 14, FontWeights.Normal, Theme.Label);
        t.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(t);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

    static Border Pill(string text, Brush bg, Brush fg, Action click)
    {
        var b = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 7, 20, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = Text(text, 13, FontWeights.Medium, fg),
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    static Border Plain(string text, Brush fg, Action click)
    {
        var b = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Text(text, 13, FontWeights.Medium, fg) };
        b.MouseEnter += (_, _) => b.Opacity = 0.7;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }
}
