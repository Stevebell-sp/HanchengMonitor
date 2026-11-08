using System;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace GlassMonitor;

/// <summary>系統匣圖示。開啟滑鼠穿透時，只能從這裡叫出選單。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _ni;

    public event Action? RightClick;
    public event Action? DoubleClick;

    public TrayIcon()
    {
        _ni = new WF.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "瀚誠系統監控",
            Visible = true,
        };
        _ni.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Right) RightClick?.Invoke();
        };
        _ni.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) DoubleClick?.Invoke();
        };
    }

    public void SetTooltip(string text) => _ni.Text = text.Length > 63 ? text[..63] : text;

    public void Balloon(string title, string text) => _ni.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.Info);

    public static SD.Icon CreateIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (res != null) return new SD.Icon(res.Stream, 32, 32);
        }
        catch { }
        return DrawFallbackIcon();
    }

    private static SD.Icon DrawFallbackIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var path = new SD.Drawing2D.GraphicsPath();
            const int r = 8;
            var rc = new SD.Rectangle(1, 1, 29, 29);
            path.AddArc(rc.Left, rc.Top, r * 2, r * 2, 180, 90);
            path.AddArc(rc.Right - r * 2, rc.Top, r * 2, r * 2, 270, 90);
            path.AddArc(rc.Right - r * 2, rc.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rc.Left, rc.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var bg = new SD.SolidBrush(SD.Color.FromArgb(255, 32, 36, 46));
            g.FillPath(bg, path);
            using var edge = new SD.Pen(SD.Color.FromArgb(140, 255, 255, 255), 1);
            g.DrawPath(edge, path);
            using var green = new SD.SolidBrush(SD.Color.FromArgb(255, 110, 220, 140));
            g.FillRectangle(green, 7, 18, 4, 8);
            g.FillRectangle(green, 14, 11, 4, 15);
            g.FillRectangle(green, 21, 6, 4, 20);
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _ni.Visible = false;
        _ni.Dispose();
    }
}
