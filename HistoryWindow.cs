using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace GlassMonitor;

/// <summary>歷史紀錄：選一天，看溫度與使用率的走勢。</summary>
public sealed class HistoryWindow : Window
{
    private readonly AppSettings _s;
    private readonly LineChart _chart = new() { Height = 260 };
    private readonly TextBlock _dateText = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 150, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0), LineHeight = 20 };
    private readonly CheckBox _cpu, _cpuTemp, _gpu, _gpuTemp, _mem;
    private DateTime _day = DateTime.Today;

    public HistoryWindow(AppSettings s)
    {
        _s = s;
        Title = "瀚誠系統監控 歷史紀錄";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
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

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10), Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        header.MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "瀚誠系統監控", FontSize = 15, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { Text = "  歷史紀錄", FontSize = 15 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        title.Children.Add(sub);
        header.Children.Add(title);
        var close = new Button { Content = "✕", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("IconButton") };
        close.Click += (_, _) => Close();
        header.Children.Add(close);

        // 日期切換
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var prev = new Button { Content = "◀", Style = (Style)FindResource("IconButton") };
        prev.Click += (_, _) => Go(-1);
        var next = new Button { Content = "▶", Style = (Style)FindResource("IconButton") };
        next.Click += (_, _) => Go(+1);
        var today = new Button { Content = "今天", Margin = new Thickness(10, 0, 0, 0), Style = (Style)FindResource("GlassButton") };
        today.Click += (_, _) => { _day = DateTime.Today; Reload(); };
        var folder = new Button { Content = "開啟資料夾", Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("GlassButton") };
        folder.Click += (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(HistoryLogger.Dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{HistoryLogger.Dir}\"") { UseShellExecute = true });
            }
            catch { }
        };
        nav.Children.Add(prev);
        nav.Children.Add(_dateText);
        nav.Children.Add(next);
        nav.Children.Add(today);
        nav.Children.Add(folder);

        // 要顯示的線
        var toggles = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _cpu = Toggle("CPU 使用率", true);
        _cpuTemp = Toggle("CPU 溫度", true);
        _gpu = Toggle("GPU 使用率", false);
        _gpuTemp = Toggle("GPU 溫度", true);
        _mem = Toggle("記憶體", false);
        foreach (var t in new[] { _cpu, _cpuTemp, _gpu, _gpuTemp, _mem }) toggles.Children.Add(t);

        _chart.Dark = s.Theme != "Light";
        _chart.XMin = 0;
        _chart.XMax = 1440;
        _chart.XLabels = new[] { "0:00", "3:00", "6:00", "9:00", "12:00", "15:00", "18:00", "21:00", "24:00" };
        _chart.EmptyText = "這一天沒有紀錄";

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(nav);
        root.Children.Add(toggles);
        root.Children.Add(_chart);
        root.Children.Add(_summary);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 12, 18, 16),
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Child = root,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Content = border;

        SourceInitialized += (_, _) => GlassHelper.Apply(this, _s.Theme, Math.Max(0.72, _s.TintOpacity), true);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Left) Go(-1);
            if (e.Key == Key.Right) Go(+1);
        };
        Reload();
    }

    private CheckBox Toggle(string text, bool on)
    {
        var c = new CheckBox { Content = text, IsChecked = on, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 0, 18, 0) };
        c.Checked += (_, _) => Reload();
        c.Unchecked += (_, _) => Reload();
        return c;
    }

    private void Go(int delta)
    {
        var d = _day.AddDays(delta);
        if (d > DateTime.Today) return;
        _day = d;
        Reload();
    }

    private void Reload()
    {
        _dateText.Text = _day == DateTime.Today ? $"{_day:yyyy/MM/dd}（今天）" : _day.ToString("yyyy/MM/dd（ddd）");
        var rows = HistoryLogger.Load(_day);
        var x = rows.Select(r => r.Time.TimeOfDay.TotalMinutes).ToArray();

        _chart.Series.Clear();
        void Add(CheckBox box, string name, string colorId, int col, string unit)
        {
            if (box.IsChecked != true) return;
            _chart.Series.Add(new ChartSeries
            {
                Name = name,
                X = x,
                Values = rows.Select(r => r.V[col]).ToArray(),
                Color = Accents.For(colorId),
                Percent = true,
                Format = unit == "°C" ? v => $"{v:0} °C" : v => $"{v:0}%",
            });
        }
        // 欄位索引見 HistoryLogger.Columns
        Add(_cpu, "CPU 使用率", "cpu", 0, "%");
        Add(_cpuTemp, "CPU 溫度", "cputemp", 3, "°C");
        Add(_gpu, "GPU 使用率", "gpu", 4, "%");
        Add(_gpuTemp, "GPU 溫度", "gputemp", 7, "°C");
        Add(_mem, "記憶體", "memused", 8, "%");
        _chart.InvalidateVisual();

        if (rows.Count == 0)
        {
            _summary.Text = _s.HistoryEnabled ? "這一天沒有紀錄。" : "歷史紀錄目前是關閉的，可以在設定裡開啟。";
            return;
        }

        string Peak(int col, string label, string unit)
        {
            var valid = rows.Where(r => !double.IsNaN(r.V[col])).ToList();
            if (valid.Count == 0) return "";
            var top = valid.MaxBy(r => r.V[col])!;
            return $"{label}最高 {top.V[col]:0}{unit}（{top.Time:HH:mm}）";
        }
        string Avg(int col, string label)
        {
            var valid = rows.Select(r => r.V[col]).Where(v => !double.IsNaN(v)).ToList();
            return valid.Count == 0 ? "" : $"{label}平均 {valid.Average():0}%";
        }
        var parts = new[]
        {
            Peak(3, "CPU 溫度", " °C"), Peak(7, "GPU 溫度", " °C"), Peak(1, "CPU 使用率", "%"),
            Avg(0, "CPU 使用率"), Avg(8, "記憶體"),
        }.Where(p => p.Length > 0);
        _summary.Text = $"共 {rows.Count} 分鐘的紀錄　·　" + string.Join("　·　", parts);
    }
}
