using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace GlassMonitor;

public sealed class ChartSeries
{
    public string Name = "";
    public double[] Values = Array.Empty<double>();
    /// <summary>有值時依 X 定位（例如一天中的第幾分鐘），否則依索引靠右排列。</summary>
    public double[]? X;
    public Color Color;
    /// <summary>true：固定 0~100 刻度（百分比或溫度）。</summary>
    public bool Percent;
    public Func<double, string> Format = v => v.ToString("0");
}

/// <summary>簡單的發光折線圖。</summary>
public sealed class LineChart : FrameworkElement
{
    private static readonly Typeface Face =
        new(new FontFamily("Microsoft JhengHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public List<ChartSeries> Series { get; } = new();
    public bool Dark { get; set; } = true;
    public double XMin { get; set; } = double.NaN;
    public double XMax { get; set; } = double.NaN;
    public string[]? XLabels { get; set; }
    public int Capacity { get; set; } = MetricsService.LongLength;
    public string EmptyText { get; set; } = "讀取中…";

    private FormattedText Text(string s, double size, Brush b) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;

        Color fg = Dark ? Colors.White : Colors.Black;
        dc.DrawRoundedRectangle(Solid(Dark ? Color.FromArgb(0x12, 255, 255, 255) : Color.FromArgb(0x50, 255, 255, 255)), null,
            new Rect(0, 0, w, h), 6, 6);

        double legendH = 20, axisH = XLabels != null ? 16 : 4;
        bool anyPercent = Series.Any(s => s.Percent);
        double leftPad = anyPercent ? 30 : 10;
        var plot = new Rect(leftPad, legendH + 4, Math.Max(10, w - leftPad - 10), Math.Max(10, h - legendH - axisH - 8));

        // 格線與刻度
        var gridPen = new Pen(Solid(Color.FromArgb(0x18, fg.R, fg.G, fg.B)), 1);
        gridPen.Freeze();
        Brush dim = Solid(Color.FromArgb(0x90, fg.R, fg.G, fg.B));
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Top + plot.Height * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (anyPercent)
            {
                var t = Text($"{100 - i * 25}", 9.5, dim);
                dc.DrawText(t, new Point(plot.Left - t.Width - 5, y - t.Height / 2));
            }
        }

        if (XLabels is { Length: > 1 } labels)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                double x = plot.Left + plot.Width * i / (labels.Length - 1);
                var t = Text(labels[i], 9.5, dim);
                dc.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, 0, w - t.Width), plot.Bottom + 3));
            }
        }

        bool anyData = Series.Any(s => s.Values.Any(v => !double.IsNaN(v)));
        if (!anyData)
        {
            var t = Text(EmptyText, 12, dim);
            dc.DrawText(t, new Point((w - t.Width) / 2, plot.Top + (plot.Height - t.Height) / 2));
        }
        else
        {
            foreach (var s in Series) DrawSeries(dc, s, plot);
        }

        // 圖例：名稱 + 最新數值
        double lx = 10;
        foreach (var s in Series)
        {
            dc.DrawEllipse(Solid(s.Color), null, new Point(lx + 4, 11), 4, 4);
            double last = s.Values.LastOrDefault(v => !double.IsNaN(v), double.NaN);
            string label = double.IsNaN(last) ? s.Name : $"{s.Name}  {s.Format(last)}";
            var t = Text(label, 11, Solid(Color.FromArgb(0xD8, fg.R, fg.G, fg.B)));
            dc.DrawText(t, new Point(lx + 12, 11 - t.Height / 2));
            lx += 12 + t.Width + 16;
        }
    }

    private void DrawSeries(DrawingContext dc, ChartSeries s, Rect plot)
    {
        var vals = s.Values;
        if (vals.Length == 0) return;
        double max = 100;
        if (!s.Percent)
        {
            double m = vals.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            max = Math.Max(1, m * 1.15);
        }

        double X(int i)
        {
            if (s.X != null && !double.IsNaN(XMin) && !double.IsNaN(XMax) && XMax > XMin)
                return plot.Left + (s.X[i] - XMin) / (XMax - XMin) * plot.Width;
            double step = plot.Width / Math.Max(1, Capacity - 1);
            return plot.Right - (vals.Length - 1 - i) * step;
        }
        double Y(double v) => plot.Bottom - Math.Clamp(v / max, 0, 1) * plot.Height;

        // 依 NaN 斷開成多段
        var segments = new List<List<Point>>();
        List<Point>? cur = null;
        for (int i = 0; i < vals.Length; i++)
        {
            if (double.IsNaN(vals[i]))
            {
                cur = null;
                continue;
            }
            if (cur == null) segments.Add(cur = new List<Point>());
            cur.Add(new Point(X(i), Y(vals[i])));
        }

        var fill = new LinearGradientBrush(Accents.A(s.Color, Dark ? (byte)0x50 : (byte)0x40), Accents.A(s.Color, 0), 90);
        fill.Freeze();
        var pen = new Pen(Solid(Accents.A(s.Color, 0xF0)), 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        foreach (var seg in segments)
        {
            if (seg.Count == 1)
            {
                dc.DrawEllipse(Solid(s.Color), null, seg[0], 1.5, 1.5);
                continue;
            }
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(seg[0].X, plot.Bottom), true, true);
                foreach (var p in seg) c.LineTo(p, false, false);
                c.LineTo(new Point(seg[^1].X, plot.Bottom), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(fill, null, area);

            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(seg[0], false, false);
                for (int i = 1; i < seg.Count; i++) c.LineTo(seg[i], true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, pen, line);
        }
    }
}

/// <summary>各核心使用率的小長條。</summary>
public sealed class CoreBars : FrameworkElement
{
    public double[] Values { get; set; } = Array.Empty<double>();
    public Color Color { get; set; } = Colors.DeepSkyBlue;
    public bool Dark { get; set; } = true;

    protected override void OnRender(DrawingContext dc)
    {
        int n = Values.Length;
        double w = ActualWidth, h = ActualHeight;
        if (n == 0 || w < 10) return;
        double gap = n > 24 ? 2 : 3;
        double bw = (w - gap * (n - 1)) / n;
        var bg = new SolidColorBrush(Dark ? Color.FromArgb(0x16, 255, 255, 255) : Color.FromArgb(0x40, 255, 255, 255));
        bg.Freeze();
        var fill = new LinearGradientBrush(Accents.A(Color, 0xF0), Accents.A(Color, 0x60), 90);
        fill.Freeze();
        for (int i = 0; i < n; i++)
        {
            double x = i * (bw + gap);
            dc.DrawRoundedRectangle(bg, null, new Rect(x, 0, bw, h), 2, 2);
            double fh = Math.Clamp(Values[i], 0, 100) / 100 * h;
            if (fh > 0.5) dc.DrawRoundedRectangle(fill, null, new Rect(x, h - fh, bw, fh), 2, 2);
        }
    }
}
