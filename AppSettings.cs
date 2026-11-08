using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace GlassMonitor;

public sealed class AppSettings
{
    /// <summary>設定檔版本，用來做舊設定的轉換。新安裝為 0，轉換後設為目前版本。</summary>
    public int SettingsVersion { get; set; }

    // 外觀
    public string Theme { get; set; } = "Dark";          // Dark / Light
    public string Style { get; set; } = "Fancy";         // Fancy 華麗 / Simple 簡約
    public string ColorScheme { get; set; } = "Neon";    // 見 Accents.Schemes
    public Dictionary<string, string> ColorOverrides { get; set; } = new();
    public bool Acrylic { get; set; } = true;
    public double TintOpacity { get; set; } = 0.45;
    public double Scale { get; set; } = 1.0;
    public string Layout { get; set; } = "Horizontal";   // Horizontal / Vertical

    // 行為
    public int IntervalMs { get; set; } = 1000;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    /// <summary>滑鼠穿透時，按住這個鍵可暫時點選小工具：Alt / Ctrl / Shift。</summary>
    public string PassKey { get; set; } = "Alt";
    public bool LockPosition { get; set; }
    public bool AutoStart { get; set; }

    // 項目
    public List<string> Order { get; set; } = new();
    public List<string> Hidden { get; set; } = new() { "memproc", "cpuclock", "gpuclock", "cpupower", "gpupower", "gpufan", "fan", "battery", "gamefps", "gameping" };

    // 遊戲模式（與永遠置頂各自獨立）
    public bool GameMode { get; set; } = true;
    public bool GameDetectFullscreen { get; set; } = true;
    public List<string> GameProcesses { get; set; } = new() { "javaw" };
    public bool GameMini { get; set; } = true;
    /// <summary>遊戲中改用簡約外觀（不發光、不畫圖示，比較不搶畫面）。</summary>
    public bool GameSimple { get; set; }
    /// <summary>遊戲中測量 FPS（背景執行 PresentMon）。</summary>
    public bool GameFps { get; set; } = true;
    /// <summary>遊戲中測量伺服器延遲。</summary>
    public bool GamePing { get; set; } = true;
    /// <summary>遊戲是視窗模式時，小工具放在遊戲視窗裡面（位置規則與遊戲中位置相同）。</summary>
    public bool GameFollowWindow { get; set; } = true;
    /// <summary>視窗模式遊戲、位置選「自訂」時，相對遊戲畫面左上角的位置。</summary>
    public double GameWinLeft { get; set; } = double.NaN;
    public double GameWinTop { get; set; } = double.NaN;
    /// <summary>全螢幕遊戲、位置選「自訂」時，相對遊戲所在螢幕左上角的位置（遊戲換螢幕時跟著換）。</summary>
    public double GameScreenLeft { get; set; } = double.NaN;
    public double GameScreenTop { get; set; } = double.NaN;
    public List<string> GameItems { get; set; } = new() { "cpu", "cputemp", "gpu", "gputemp", "gpumem", "gamefps", "gameping" };
    /// <summary>遊戲中的排列：Horizontal / Vertical。</summary>
    public string GameLayout { get; set; } = "Horizontal";
    /// <summary>遊戲中的位置：Custom（自己拖曳）或固定方位 TopLeft / TopCenter / TopRight / BottomLeft / BottomCenter / BottomRight。</summary>
    public string GameAnchor { get; set; } = "Custom";
    public double GameLeft { get; set; } = double.NaN;
    public double GameTop { get; set; } = double.NaN;

    // 警示
    public bool AlertEnabled { get; set; } = true;
    public double AlertCpuTemp { get; set; } = 85;
    public double AlertGpuTemp { get; set; } = 83;
    public double AlertMemPct { get; set; } = 90;
    public bool AlertNotify { get; set; } = true;
    /// <summary>CPU 風扇／水冷泵轉速過低（停轉）警示，預設關閉。</summary>
    public bool AlertFanStall { get; set; }
    public double AlertCpuFanRpm { get; set; } = 300;
    public double AlertPumpRpm { get; set; } = 800;

    // 歷史紀錄
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryDays { get; set; } = 30;

    // 更新
    /// <summary>GitHub 儲存庫（帳號/名稱），從它的 Releases 取得新版本。</summary>
    public string UpdateRepo { get; set; } = DefaultRepo;
    public const string DefaultRepo = "Stevebell-sp/HanchengMonitor";
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>全自動更新：發現新版本就直接下載安裝，不詢問（遊戲中會等到離開遊戲）。</summary>
    public bool AutoInstallUpdate { get; set; } = true;

    // 風扇對應（主機板風扇接頭名稱；空字串＝自動判斷，"-"＝不使用）
    public string CpuFanSensor { get; set; } = "";
    public string PumpSensor { get; set; } = "";

    // 位置
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;

    private const int CurrentVersion = 3;

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassMonitor");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        AppSettings s = new();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opt) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        s.Migrate();
        s.Normalize();
        s.AutoStart = GlassMonitor.AutoStart.IsEnabled(); // 以工作排程器實際狀態為準（安裝程式也可能建立）
        return s;
    }

    /// <summary>舊版設定轉換：v2 起 GPU 記憶體預設顯示，新增的項目預設隱藏。</summary>
    private void Migrate()
    {
        if (SettingsVersion < 2)
        {
            Hidden.Remove("gpumem");
            foreach (var id in new[] { "cpuclock", "gpuclock", "cpupower", "gpupower", "gpufan", "fan", "battery" })
                if (!Hidden.Contains(id)) Hidden.Add(id);
        }
        if (SettingsVersion < 3)
        {
            // v3：FPS、伺服器延遲只在遊戲中顯示
            foreach (var id in new[] { "gamefps", "gameping" })
            {
                if (!Hidden.Contains(id)) Hidden.Add(id);
                if (!GameItems.Contains(id)) GameItems.Add(id);
            }
        }
        SettingsVersion = CurrentVersion;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private void Normalize()
    {
        if (IntervalMs != 500 && IntervalMs != 2000) IntervalMs = 1000;
        Scale = Math.Clamp(Scale, 0.7, 2.0);
        TintOpacity = Math.Clamp(TintOpacity, 0.05, 0.95);
        if (Theme != "Light") Theme = "Dark";
        if (Style != "Simple") Style = "Fancy";
        if (PassKey is not ("Alt" or "Ctrl" or "Shift")) PassKey = "Alt";
        if (Layout != "Vertical") Layout = "Horizontal";
        if (GameLayout != "Vertical") GameLayout = "Horizontal";
        if (GameAnchor is not ("Custom" or "TopLeft" or "TopCenter" or "TopRight" or "BottomLeft" or "BottomCenter" or "BottomRight")) GameAnchor = "Custom";
        if (!Accents.Schemes.Any(x => x.Key == ColorScheme)) ColorScheme = "Neon";
        if (HistoryDays is not (7 or 30 or 90)) HistoryDays = 30;
        AlertCpuTemp = Math.Clamp(AlertCpuTemp, 50, 105);
        AlertGpuTemp = Math.Clamp(AlertGpuTemp, 50, 105);
        AlertMemPct = Math.Clamp(AlertMemPct, 50, 99);
        AlertCpuFanRpm = Math.Clamp(AlertCpuFanRpm, 100, 2000);
        AlertPumpRpm = Math.Clamp(AlertPumpRpm, 100, 3000);
        Order ??= new();
        Hidden ??= new();
        GameProcesses ??= new();
        GameItems ??= new();
        ColorOverrides ??= new();
        if (string.IsNullOrWhiteSpace(UpdateRepo)) UpdateRepo = DefaultRepo;
        CpuFanSensor ??= "";
        PumpSensor ??= "";
    }

    /// <summary>匯出成 JSON 檔。</summary>
    public void ExportTo(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opt));

    /// <summary>從 JSON 檔匯入（經過舊版轉換與檢查），覆蓋目前設定並存檔。</summary>
    public void ImportFrom(string path)
    {
        var o = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opt)
                ?? throw new InvalidDataException("檔案內容不是設定檔");
        o.Migrate();
        o.Normalize();
        CopyFrom(o);
        Save();
    }

    /// <summary>把另一份設定的內容複製過來（恢復預設用）。</summary>
    public void CopyFrom(AppSettings o)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(o, Opt), Opt)!;
        foreach (var p in typeof(AppSettings).GetProperties())
            if (p.CanWrite && p.CanRead) p.SetValue(this, p.GetValue(clone));
    }

    /// <summary>依使用者排序列出全部項目（新項目接在後面）。</summary>
    public List<string> GetOrderedIds(IReadOnlyList<MetricDef> defs)
    {
        var all = defs.Select(d => d.Id).ToList();
        var result = Order.Where(all.Contains).Distinct().ToList();
        result.AddRange(all.Where(id => !result.Contains(id)));
        return result;
    }
}

/// <summary>
/// 開機自動啟動。程式需要系統管理員權限，Windows 不會從登錄檔 Run 機碼啟動這種程式，
/// 所以改用工作排程器（登入時、以最高權限執行）。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "瀚誠系統監控";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            // 清掉舊版留下的 Run 機碼
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                key?.DeleteValue(TaskName, false);

            string args = on
                ? $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{Environment.ProcessPath}\\\"\" /SC ONLOGON /RL HIGHEST"
                : $"/Delete /F /TN \"{TaskName}\"";
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
