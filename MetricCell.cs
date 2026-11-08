using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GlassMonitor;

/// <summary>一個監控格子：標題、數值、背景填色、迷你折線圖。全部自己畫，省資源。</summary>
public sealed class MetricCell : FrameworkElement
{
    private static readonly Typeface TitleFace =
        new(new FontFamily("Microsoft JhengHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface ValueFace =
        new(new FontFamily("Segoe UI, Microsoft JhengHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Typeface IconFace =
        new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private MetricState _state;
    private double _sticky; // 寬度只增不減，避免數字跳動時整條抖動
    private readonly bool _fancy;
    private readonly bool _dark;
    private readonly Color _accent;
    private bool _blinkOn;

    // 填色動畫
    private double _shown, _from, _target;
    private DateTime _animStart;
    private static readonly HashSet<MetricCell> Animating = new();
    private static bool _hooked;
    private const double AnimMs = 450;

    public MetricCell(MetricState initial, Palette palette, bool horizontal, double scale, bool separator, bool fancy, bool dark)
    {
        _state = initial;
        Palette = palette;
        Horizontal = horizontal;
        UiScale = scale;
        ShowSeparator = separator;
        _fancy = fancy;
        _dark = dark;
        _accent = Accents.For(initial.Id);
        _shown = initial.Fraction;
    }

    public string Id => _state.Id;
    public Palette Palette { get; }
    public bool Horizontal { get; }
    public double UiScale { get; }
    public bool ShowSeparator { get; }

    private double TitleSize => 11 * UiScale;
    private double ValueSize => 14 * UiScale;

    public void Update(MetricState s)
    {
        _state = s;
        _blinkOn = s.Alert && !_blinkOn;

        double t = s.Fraction;
        if (_fancy && !double.IsNaN(t) && !double.IsNaN(_shown) && Math.Abs(t - _shown) > 0.003)
        {
            _from = _shown;
            _target = t;
            _animStart = DateTime.UtcNow;
            Animating.Add(this);
            if (!_hooked)
            {
                CompositionTarget.Rendering += OnFrame;
                _hooked = true;
            }
        }
        else
        {
            _shown = t;
        }
        InvalidateVisual();
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        List<MetricCell>? done = null;
        foreach (var c in Animating)
        {
            double p = Math.Clamp((now - c._animStart).TotalMilliseconds / AnimMs, 0, 1);
            double ease = 1 - Math.Pow(1 - p, 3);
            c._shown = c._from + (c._target - c._from) * ease;
            c.InvalidateVisual();
            if (p >= 1) (done ??= new()).Add(c);
        }
        if (done != null)
            foreach (var c in done) Animating.Remove(c);
        if (Animating.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }

    private FormattedText Fmt(string text, Typeface face, double size, Brush brush) =>
        new(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>
    /// 每種項目用「可能出現的最寬數值」決定格子寬度，
    /// 數字怎麼變、格子怎麼重建，整條的長度都固定不動。太長的程式名稱會以「…」截斷。
    /// </summary>
    private static string WidthTemplate(string id) => id switch
    {
        "cpu" or "gpu" or "diskbusy" => "100%",
        "battery" => "100% ⚡",
        "cputemp" or "gputemp" => "100 °C",
        "cpuclock" => "8.88 GHz",
        "gpuclock" => "8888 MHz",
        "cpupower" or "gpupower" => "888 W",
        "fan" or "gpufan" => "8888 RPM",
        "uptime" => "88d 88h",
        "cpuproc" or "memproc" or "slow" => "msedgewebvi",
        _ => "88.8 GB", // 記憶體、磁碟、網路流量
    };

    private double NeededWidth()
    {
        var t = Fmt(_state.Title, TitleFace, TitleSize, Palette.Title);
        var v = Fmt(WidthTemplate(_state.Id), ValueFace, ValueSize, Palette.Value);
        double text = Math.Max(t.WidthIncludingTrailingWhitespace, v.WidthIncludingTrailingWhitespace);
        if (_fancy) return Math.Ceiling(Math.Max(text + 52 * UiScale, 92 * UiScale));
        return Math.Ceiling(Math.Max(text + 16 * UiScale, 62 * UiScale));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Horizontal)
        {
            _sticky = NeededWidth();
            return new Size(_sticky, Math.Round(40 * UiScale));
        }
        return new Size(Math.Round(250 * UiScale), Math.Round((_fancy ? 30 : 24) * UiScale));
    }

    private static Brush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private Color LevelColor(int level) => level == 2 ? Accents.Crit : level == 1 ? Accents.Warn : _accent;

    protected override void OnRender(DrawingContext dc)
    {
        if (_fancy) RenderFancy(dc);
        else RenderSimple(dc);
        if (_state.Alert && _blinkOn) DrawAlert(dc);
    }

    /// <summary>超過警示門檻：紅色閃爍外框與底色。</summary>
    private void DrawAlert(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double r = _fancy ? (Horizontal ? 6 : 5) * UiScale : 2;
        var pen = new Pen(Solid(Accents.A(Accents.Crit, 0xE0)), 1.5);
        pen.Freeze();
        dc.DrawRoundedRectangle(Solid(Accents.A(Accents.Crit, 0x38)), pen, new Rect(0.75, 0.75, Math.Max(0, w - 1.5), Math.Max(0, h - 1.5)), r, r);
    }

    private void RenderSimple(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var p = Palette;
        var s = _state;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // 讓整格可以接滑鼠

        double frac = double.IsNaN(_shown) ? s.Fraction : _shown;
        if (!double.IsNaN(frac) && frac > 0.001)
            dc.DrawRectangle(p.Fill[Math.Clamp(s.Level, 0, 2)], null, new Rect(0, 0, Math.Round(w * Math.Clamp(frac, 0, 1)), h));

        if (s.History is { Length: > 1 } hist) DrawSpark(dc, hist, s.HistoryIsPercent, w, h);

        Brush valueBrush = p.Value;
        if (double.IsNaN(s.Fraction) && s.Level == 2) valueBrush = p.Alert;
        else if (double.IsNaN(s.Fraction) && s.Level == 1) valueBrush = p.Warn;

        var title = Fmt(s.Title, TitleFace, TitleSize, p.Title);
        var value = Fmt(s.Text, ValueFace, ValueSize, valueBrush);

        if (Horizontal)
        {
            double pad = 2 * UiScale;
            value.MaxTextWidth = Math.Max(10, w - 6);
            value.MaxLineCount = 1;
            value.Trimming = TextTrimming.CharacterEllipsis;
            DrawText(dc, title, Math.Round((w - title.Width) / 2), Math.Round(pad), p.Title, null);
            DrawText(dc, value, Math.Round((w - value.Width) / 2), Math.Round(h - value.Height - pad), valueBrush, null);
            if (ShowSeparator) dc.DrawRectangle(p.Separator, null, new Rect(w - 1, 4 * UiScale, 1, h - 8 * UiScale));
        }
        else
        {
            double pad = 10 * UiScale;
            value.MaxTextWidth = Math.Max(10, w * 0.6);
            value.MaxLineCount = 1;
            value.Trimming = TextTrimming.CharacterEllipsis;
            DrawText(dc, title, pad, Math.Round((h - title.Height) / 2), p.Title, null);
            DrawText(dc, value, Math.Round(w - pad - value.Width), Math.Round((h - value.Height) / 2), valueBrush, null);
            if (ShowSeparator) dc.DrawRectangle(p.Separator, null, new Rect(pad / 2, h - 1, w - pad, 1));
        }
    }

    /// <summary>華麗風格：每項一顆圓角發光晶片，左邊圖示、右邊標題與數值。</summary>
    private void RenderFancy(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var s = _state;
        double r = (Horizontal ? 6 : 5) * UiScale;
        var rect = new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1));
        Color lc = LevelColor(s.Level);

        var chipBg = _dark
            ? new LinearGradientBrush(Color.FromArgb(0x18, 255, 255, 255), Color.FromArgb(0x04, 255, 255, 255), 90)
            : new LinearGradientBrush(Color.FromArgb(0x70, 255, 255, 255), Color.FromArgb(0x28, 255, 255, 255), 90);
        chipBg.Freeze();
        byte top = s.Level > 0 ? (byte)0x90 : _dark ? (byte)0x46 : (byte)0x60;
        var edgeBrush = new LinearGradientBrush(Accents.A(lc, top), Accents.A(lc, (byte)(top / 4)), 90);
        edgeBrush.Freeze();
        var edge = new Pen(edgeBrush, 1);
        edge.Freeze();
        dc.DrawRoundedRectangle(chipBg, edge, rect, r, r);

        dc.PushClip(new RectangleGeometry(rect, r, r));
        double frac = double.IsNaN(_shown) ? s.Fraction : _shown;
        if (!double.IsNaN(frac) && frac > 0.001) DrawFancyFill(dc, Math.Clamp(frac, 0, 1), w, h, lc);
        if (s.History is { Length: > 1 } hist) DrawSpark(dc, hist, s.HistoryIsPercent, w, h);
        dc.Pop();

        double pad = 10 * UiScale;
        double iconSize = (Horizontal ? 18 : 14) * UiScale;
        Brush iconBrush = Solid(_dark ? lc : Accents.Darken(lc, 0.75));
        var icon = Fmt(Accents.Icon(s.Id), IconFace, iconSize, iconBrush);
        DrawText(dc, icon, Math.Round(pad), Math.Round((h - icon.Height) / 2), iconBrush, lc);
        double tx = Math.Round(pad + iconSize + 8 * UiScale);

        Brush titleBrush = Solid(_dark ? Color.FromArgb(0xC8, 255, 255, 255) : Color.FromArgb(0xB0, 0, 0, 0));
        Brush valueBrush = Palette.Value;
        if (double.IsNaN(s.Fraction) && s.Level == 2) valueBrush = Palette.Alert;
        else if (double.IsNaN(s.Fraction) && s.Level == 1) valueBrush = Palette.Warn;

        var title = Fmt(s.Title, TitleFace, TitleSize, titleBrush);
        var value = Fmt(s.Text, ValueFace, ValueSize, valueBrush);

        if (Horizontal)
        {
            value.MaxTextWidth = Math.Max(10, w - tx - 6 * UiScale);
            value.MaxLineCount = 1;
            value.Trimming = TextTrimming.CharacterEllipsis;
            DrawText(dc, title, tx, Math.Round(3 * UiScale), titleBrush, null);
            DrawText(dc, value, tx, Math.Round(h - value.Height - 3 * UiScale), valueBrush, lc);
        }
        else
        {
            value.MaxTextWidth = Math.Max(10, w * 0.5);
            value.MaxLineCount = 1;
            value.Trimming = TextTrimming.CharacterEllipsis;
            DrawText(dc, title, tx, Math.Round((h - title.Height) / 2), titleBrush, null);
            DrawText(dc, value, Math.Round(w - pad - value.Width), Math.Round((h - value.Height) / 2), valueBrush, lc);
        }
    }

    private void DrawFancyFill(DrawingContext dc, double f, double w, double h, Color c)
    {
        double fw = Math.Round(w * f);
        var grad = new LinearGradientBrush(Accents.A(c, 0x06), Accents.A(c, _dark ? (byte)0x6A : (byte)0x58), 0);
        grad.Freeze();
        dc.DrawRectangle(grad, null, new Rect(0, 0, fw, h));
        double bar = Math.Max(2, Math.Round(2 * UiScale));
        dc.DrawRectangle(Solid(Accents.A(c, 0x40)), null, new Rect(0, h - bar * 2.5, fw, bar * 2.5));
        dc.DrawRectangle(Solid(Accents.A(c, 0xF0)), null, new Rect(0, h - bar, fw, bar));
        dc.DrawRectangle(Solid(Accents.A(c, 0xC8)), null, new Rect(Math.Max(0, fw - 1), 0, 1, h));
    }

    private void DrawText(DrawingContext dc, FormattedText ft, double x, double y, Brush fg, Color? glow)
    {
        if (_fancy && glow is Color gc)
        {
            ft.SetForegroundBrush(Solid(Accents.A(_dark ? gc : Colors.White, _dark ? (byte)0x46 : (byte)0xA0)));
            dc.DrawText(ft, new Point(x - 1, y));
            dc.DrawText(ft, new Point(x + 1, y));
            dc.DrawText(ft, new Point(x, y - 1));
            dc.DrawText(ft, new Point(x, y + 1));
        }
        else if (Palette.Shadow != null)
        {
            ft.SetForegroundBrush(Palette.Shadow);
            dc.DrawText(ft, new Point(x, y + 1));
        }
        ft.SetForegroundBrush(fg);
        dc.DrawText(ft, new Point(x, y));
    }

    private void DrawSpark(DrawingContext dc, double[] hist, bool percent, double w, double h)
    {
        double max = 100;
        if (!percent)
        {
            max = 50 * 1024; // 低於 50 KB/s 不放大，避免雜訊看起來像滿格
            foreach (var v in hist) if (v > max) max = v;
        }

        double top = Horizontal ? h * 0.42 : 0;
        double area = h - top;
        double step = w / (MetricsService.HistoryLength - 1);
        double x0 = w - (hist.Length - 1) * step;

        var pts = new Point[hist.Length];
        for (int i = 0; i < hist.Length; i++)
        {
            double v = Math.Clamp(hist[i] / max, 0, 1);
            pts[i] = new Point(x0 + i * step, h - v * area);
        }

        var fill = new StreamGeometry();
        using (var c = fill.Open())
        {
            c.BeginFigure(new Point(x0, h), true, true);
            foreach (var pt in pts) c.LineTo(pt, false, false);
            c.LineTo(new Point(w, h), false, false);
        }
        fill.Freeze();

        if (!_fancy)
        {
            dc.DrawGeometry(Palette.Spark, null, fill);
            return;
        }

        Color col = LevelColor(_state.Level);
        var grad = new LinearGradientBrush(Accents.A(col, _dark ? (byte)0x70 : (byte)0x60), Accents.A(col, 0x00), 90);
        grad.Freeze();
        dc.DrawGeometry(grad, null, fill);

        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true);
        }
        line.Freeze();
        var pen = new Pen(Solid(Accents.A(col, 0xE6)), 1.2 * UiScale) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
