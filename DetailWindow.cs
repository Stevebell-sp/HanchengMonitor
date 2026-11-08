using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace GlassMonitor;

/// <summary>點監控格子後跳出的詳細資訊面板（毛玻璃）。</summary>
public sealed class DetailWindow : Window
{
    private const double PanelWidth = 420;
    private const double InnerWidth = PanelWidth - 30;

    public string Category { get; }

    private readonly AppSettings _s;
    private readonly bool _dark;
    private readonly LineChart _chart = new() { Height = 128 };
    private readonly TextBlock _coresTitle;
    private readonly CoreBars _cores = new() { Height = 44, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _infoTitle;
    private readonly Grid _info = new();
    private readonly TextBlock _topTitle;
    private readonly StackPanel _top = new();
    private readonly StackPanel _sensors = new();
    private readonly ScrollViewer _sensorScroll;
    private readonly TextBlock _note;
    private Rect _anchor, _workArea;
    private bool _userMoved;

    public DetailWindow(string category, AppSettings s)
    {
        Category = category;
        _s = s;
        _dark = s.Theme != "Light";

        Title = "瀚誠系統監控 " + MetricsService.CategoryTitle(category);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft JhengHei UI");
        FontSize = 12.5;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(ForegroundProperty, "FgBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // 標題列
        string iconId = MetricsService.CategoryIconId(category);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8), Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        header.MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); _userMoved = true; } catch { }
        };
        header.Children.Add(new TextBlock
        {
            Text = Accents.Icon(iconId),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 17,
            Foreground = new SolidColorBrush(Accents.For(iconId)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = MetricsService.CategoryTitle(category),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(28, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var close = new Button { Content = "✕", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("IconButton") };
        close.Click += (_, _) => Close();
        header.Children.Add(close);

        _coresTitle = Section("各核心使用率");
        _infoTitle = Section("詳細資訊");
        _topTitle = Section("");
        _sensorScroll = new ScrollViewer
        {
            Content = _sensors,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");

        _info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        _info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _chart.Dark = _dark;
        _cores.Dark = _dark;
        _cores.Color = Accents.For("cpu");
        _coresTitle.Visibility = _cores.Visibility = Visibility.Collapsed;

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(_chart);
        root.Children.Add(_coresTitle);
        root.Children.Add(_cores);
        root.Children.Add(_infoTitle);
        root.Children.Add(_info);
        root.Children.Add(_topTitle);
        root.Children.Add(_top);
        root.Children.Add(_sensorScroll);
        root.Children.Add(_note);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 14),
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Child = root,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Content = border;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetToolWindow(h);
            GlassHelper.Apply(this, _s.Theme, Math.Max(0.72, _s.TintOpacity), true);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        return t;
    }

    /// <summary>顯示在某個格子旁邊（座標為 WPF 單位）。</summary>
    public void ShowNear(Rect anchor, Rect workArea)
    {
        _anchor = anchor;
        _workArea = workArea;
        Left = -20000;
        Top = -20000;
        Show();
        Place();
    }

    private void Place()
    {
        if (_userMoved) return;
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        double x = Math.Clamp(_anchor.Left + _anchor.Width / 2 - w / 2, _workArea.Left + 4, Math.Max(_workArea.Left + 4, _workArea.Right - w - 4));
        double y = _anchor.Bottom + 8;
        if (y + h > _workArea.Bottom) y = _anchor.Top - h - 8;
        if (y < _workArea.Top) y = _workArea.Top + 4;
        Left = x;
        Top = y;
    }

    public void Refresh(DetailData d)
    {
        // 折線圖
        _chart.Series.Clear();
        foreach (var c in d.Charts)
        {
            Func<double, string> fmt = c.Unit switch
            {
                "B/s" => v => $"{MetricsService.FormatSize(v)}/s",
                "°C" => v => $"{v:0} °C",
                _ => v => $"{v:0}%",
            };
            _chart.Series.Add(new ChartSeries { Name = c.Name, Values = c.Values, Color = Accents.For(c.Id), Percent = c.Percent, Format = fmt });
        }
        _chart.EmptyText = d.Charts.Count == 0 ? "" : "收集資料中…";
        _chart.Visibility = d.Charts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _chart.InvalidateVisual();

        // 各核心
        bool hasCores = d.PerCore is { Length: > 0 };
        _coresTitle.Visibility = _cores.Visibility = hasCores ? Visibility.Visible : Visibility.Collapsed;
        if (hasCores)
        {
            _coresTitle.Text = $"各核心使用率（{d.PerCore!.Length} 個邏輯處理器）";
            _cores.Values = d.PerCore;
            _cores.InvalidateVisual();
        }

        // 詳細資訊
        _info.Children.Clear();
        _info.RowDefinitions.Clear();
        _infoTitle.Visibility = d.Info.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < d.Info.Count; i++)
        {
            _info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = d.Info[i].Key, Margin = new Thickness(2, 2, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            k.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            var v = new TextBlock { Text = d.Info[i].Value, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i);
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            _info.Children.Add(k);
            _info.Children.Add(v);
        }

        // 排行
        _top.Children.Clear();
        _topTitle.Visibility = d.Top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _topTitle.Text = d.TopTitle;
        Color tc = Accents.For(d.TopColorId);
        foreach (var row in d.Top)
        {
            var g = new Grid { Height = 23, Margin = new Thickness(0, 1, 0, 1) };
            g.Children.Add(new Border
            {
                Background = new SolidColorBrush(Accents.A(tc, _dark ? (byte)0x3C : (byte)0x50)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(3, InnerWidth * Math.Clamp(row.Fraction, 0, 1)),
                CornerRadius = new CornerRadius(4),
            });
            g.Children.Add(new TextBlock { Text = row.Name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(new TextBlock
            {
                Text = row.Value,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
            });
            _top.Children.Add(g);
        }

        // 全部感測器
        _sensors.Children.Clear();
        if (d.Sensors is { Count: > 0 } sensors)
        {
            _sensorScroll.Visibility = Visibility.Visible;
            foreach (var group in sensors.GroupBy(r => r.Group))
            {
                _sensors.Children.Add(Section(group.Key));
                foreach (var r in group)
                {
                    var g = new Grid { Margin = new Thickness(4, 1, 6, 1) };
                    var k = new TextBlock { Text = r.Name };
                    k.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
                    g.Children.Add(k);
                    g.Children.Add(new TextBlock { Text = r.Value, HorizontalAlignment = HorizontalAlignment.Right, FontWeight = FontWeights.SemiBold });
                    _sensors.Children.Add(g);
                }
            }
        }
        else
        {
            _sensorScroll.Visibility = Visibility.Collapsed;
        }

        _note.Text = d.Note;
        _note.Visibility = string.IsNullOrEmpty(d.Note) ? Visibility.Collapsed : Visibility.Visible;

        Place();
    }
}
