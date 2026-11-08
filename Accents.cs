using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace GlassMonitor;

/// <summary>配色：每個項目依類別取色，可整組換主題，也可單項自訂。</summary>
public static class Accents
{
    public static readonly Color Warn = Color.FromRgb(255, 165, 40);
    public static readonly Color Crit = Color.FromRgb(255, 60, 90);

    /// <summary>目前配色主題（由主視窗依設定更新）。</summary>
    public static string Scheme { get; set; } = "Neon";

    /// <summary>單項自訂顏色（項目 id → #RRGGBB）。</summary>
    public static Dictionary<string, string> Overrides { get; set; } = new();

    public static readonly (string Key, string Name)[] Schemes =
    {
        ("Neon", "霓虹"),
        ("Cyberpunk", "賽博龐克"),
        ("Aurora", "極光"),
        ("Sunset", "日落"),
        ("Mono", "銀白"),
    };

    /// <summary>自訂顏色用的色票。</summary>
    public static readonly Color[] Swatches =
    {
        C(40, 200, 255), C(80, 140, 255), C(175, 110, 255), C(255, 100, 200),
        C(255, 70, 90), C(255, 140, 60), C(255, 195, 60), C(240, 240, 90),
        C(160, 235, 80), C(60, 230, 150), C(70, 220, 210), C(225, 230, 240),
    };

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    // 類別順序：0 CPU、1 溫度、2 GPU、3 記憶體、4 磁碟、5 下載、6 上傳、7 變慢原因、8 功耗、9 風扇、10 時脈、11 電池、12 其他
    private static readonly Dictionary<string, Color[]> Table = new()
    {
        ["Neon"] = new[]
        {
            C(40, 200, 255), C(255, 120, 70), C(175, 110, 255), C(60, 230, 150), C(255, 195, 60), C(80, 150, 255),
            C(255, 100, 200), C(120, 230, 200), C(255, 220, 80), C(100, 210, 255), C(150, 180, 255), C(120, 230, 120), C(200, 205, 225),
        },
        ["Cyberpunk"] = new[]
        {
            C(255, 60, 200), C(255, 80, 80), C(0, 240, 255), C(180, 90, 255), C(255, 230, 0), C(0, 200, 255),
            C(255, 60, 160), C(255, 0, 170), C(255, 230, 0), C(0, 255, 200), C(200, 120, 255), C(120, 255, 120), C(230, 200, 255),
        },
        ["Aurora"] = new[]
        {
            C(80, 230, 200), C(255, 140, 120), C(120, 160, 255), C(100, 240, 140), C(190, 240, 120), C(80, 200, 255),
            C(170, 140, 255), C(100, 255, 210), C(220, 250, 140), C(120, 230, 255), C(150, 200, 255), C(140, 255, 170), C(190, 230, 230),
        },
        ["Sunset"] = new[]
        {
            C(255, 150, 60), C(255, 80, 60), C(255, 90, 140), C(255, 200, 90), C(240, 120, 90), C(255, 170, 120),
            C(255, 110, 180), C(255, 190, 120), C(255, 220, 100), C(255, 160, 140), C(255, 180, 150), C(255, 210, 140), C(255, 215, 200),
        },
        ["Mono"] = new[]
        {
            C(215, 222, 235), C(255, 175, 160), C(215, 222, 235), C(215, 222, 235), C(215, 222, 235), C(215, 222, 235),
            C(215, 222, 235), C(215, 222, 235), C(215, 222, 235), C(215, 222, 235), C(215, 222, 235), C(215, 222, 235), C(215, 222, 235),
        },
    };

    // 外框漸層四色
    private static readonly Dictionary<string, Color[]> FrameTable = new()
    {
        ["Neon"] = new[] { C(40, 200, 255), C(175, 110, 255), C(255, 100, 200), C(255, 170, 60) },
        ["Cyberpunk"] = new[] { C(255, 60, 200), C(180, 90, 255), C(0, 240, 255), C(255, 230, 0) },
        ["Aurora"] = new[] { C(80, 230, 200), C(100, 240, 140), C(80, 200, 255), C(170, 140, 255) },
        ["Sunset"] = new[] { C(255, 150, 60), C(255, 80, 60), C(255, 90, 140), C(255, 210, 100) },
        ["Mono"] = new[] { C(240, 244, 250), C(180, 188, 205), C(240, 244, 250), C(180, 188, 205) },
    };

    public static int Category(string id) => id switch
    {
        "cpu" or "cpuproc" => 0,
        "cputemp" or "gputemp" => 1,
        "gpu" or "gpumem" => 2,
        "netdown" => 5,
        "netup" => 6,
        "slow" => 7,
        "cpupower" or "gpupower" => 8,
        "fan" or "gpufan" => 9,
        "cpuclock" or "gpuclock" => 10,
        "battery" => 11,
        _ when id.StartsWith("mem") => 3,
        _ when id.StartsWith("disk") => 4,
        _ => 12,
    };

    public static Color Default(string id) =>
        (Table.TryGetValue(Scheme, out var t) ? t : Table["Neon"])[Category(id)];

    public static Color For(string id) =>
        Overrides.TryGetValue(id, out var hex) && TryParse(hex, out var c) ? c : Default(id);

    public static Color[] Frame() => FrameTable.TryGetValue(Scheme, out var f) ? f : FrameTable["Neon"];

    public static bool TryParse(string hex, out Color c)
    {
        try
        {
            c = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            c = default;
            return false;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Segoe Fluent Icons 的圖示字元。</summary>
    public static string Icon(string id)
    {
        if (id == "slow") return "";                              // 心電圖
        if (id == "cpu") return "";                               // 晶片
        if (id is "cpuproc" or "memproc") return "";              // 齒輪
        if (id is "cputemp" or "gputemp") return "";              // 溫度計
        if (id == "gpu") return "";                               // 顯示卡
        if (id is "gpumem" or "memused" or "memfree") return "";  // 記憶體
        if (id is "cpupower" or "gpupower") return "";            // 閃電
        if (id is "fan" or "gpufan") return "";                   // 旋轉
        if (id is "cpuclock" or "gpuclock") return "";            // 碼錶
        if (id == "battery") return "";                           // 電池
        if (id == "diskbusy") return "";                          // 儀表
        if (id.StartsWith("disk")) return "";                     // 硬碟
        if (id == "netdown") return "";                           // 下載
        if (id == "netup") return "";                             // 上傳
        if (id == "uptime") return "";                            // 時鐘
        return "";
    }

    public static Color A(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Color Darken(Color c, double k) =>
        Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
}
