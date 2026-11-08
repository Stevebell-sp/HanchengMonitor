using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassMonitor;

/// <summary>監控格子用的顏色。</summary>
public sealed class Palette
{
    public Brush Title = null!;
    public Brush Value = null!;
    public Brush Alert = null!;
    public Brush Warn = null!;
    public Brush Separator = null!;
    public Brush Spark = null!;
    public Brush? Shadow;
    public Brush Border = null!;
    public Brush[] Fill = null!; // 0 正常、1 偏高、2 危險

    public static Palette For(string theme) => theme == "Light" ? Light : Dark;

    private static SolidColorBrush B(byte a, byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        x.Freeze();
        return x;
    }

    private static readonly Palette Dark = new()
    {
        Title = B(0xB4, 255, 255, 255),
        Value = B(0xFF, 255, 255, 255),
        Alert = B(0xFF, 255, 120, 120),
        Warn = B(0xFF, 255, 185, 80),
        Separator = B(0x26, 255, 255, 255),
        Spark = B(0x55, 110, 220, 140),
        Shadow = B(0x80, 0, 0, 0),
        Border = B(0x38, 255, 255, 255),
        Fill = new Brush[] { B(0x48, 90, 205, 115), B(0x66, 255, 165, 50), B(0x88, 255, 65, 65) },
    };

    private static readonly Palette Light = new()
    {
        Title = B(0xA0, 0, 0, 0),
        Value = B(0xFF, 22, 24, 28),
        Alert = B(0xFF, 200, 40, 40),
        Warn = B(0xFF, 190, 110, 0),
        Separator = B(0x22, 0, 0, 0),
        Spark = B(0x50, 30, 150, 70),
        Shadow = B(0x70, 255, 255, 255),
        Border = B(0x90, 255, 255, 255),
        Fill = new Brush[] { B(0x50, 60, 180, 90), B(0x70, 245, 150, 30), B(0x80, 230, 50, 50) },
    };
}

/// <summary>選單、設定視窗用的資源刷子（DynamicResource）。</summary>
public static class ThemeManager
{
    public static void Apply(string theme)
    {
        bool dark = theme != "Light";
        var r = Application.Current.Resources;
        r["FgBrush"] = B(dark ? "#FFFFFFFF" : "#FF16181C");
        r["FgDimBrush"] = B(dark ? "#A8FFFFFF" : "#99000000");
        r["LineBrush"] = B(dark ? "#33FFFFFF" : "#26000000");
        r["HoverBrush"] = B(dark ? "#22FFFFFF" : "#14000000");
        r["PanelBrush"] = B(dark ? "#14FFFFFF" : "#66FFFFFF");
        r["MenuBgBrush"] = B(dark ? "#F2202228" : "#F8F6F7FA");
        r["AccentBrush"] = B(dark ? "#FF6EDC8C" : "#FF2E9E4F");
        r["AccentSoftBrush"] = B(dark ? "#336EDC8C" : "#332E9E4F");
    }

    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>把 WPF 視窗變成毛玻璃。</summary>
public static class GlassHelper
{
    public static void Apply(Window w, string theme, double tintOpacity, bool acrylic)
    {
        IntPtr hwnd = new WindowInteropHelper(w).EnsureHandle();
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;

        Native.SetCorner(hwnd, 2);
        Native.HideDwmBorder(hwnd);

        bool dark = theme != "Light";
        byte a = (byte)Math.Clamp(Math.Round(tintOpacity * 255), 1, 255);
        byte r = dark ? (byte)18 : (byte)240;
        byte g = dark ? (byte)20 : (byte)242;
        byte b = dark ? (byte)26 : (byte)247;
        uint abgr = ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        Native.SetAccent(hwnd, acrylic ? Native.ACCENT_ENABLE_ACRYLICBLURBEHIND : Native.ACCENT_ENABLE_BLURBEHIND, abgr);
    }
}
