using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GlassMonitor;

/// <summary>
/// 偵測自己是不是在別的 App 的 MSIX 封裝容器裡執行（例如從 Claude 桌面版裡啟動）。
/// 在容器裡時，寫到 %LOCALAPPDATA% 的檔案會被導向到那個 App 的虛擬資料夾，
/// 開機時由工作排程器啟動的本程式（在容器外）就讀不到這些設定，看起來像設定被還原。
/// </summary>
public static class ContainerGuard
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, char[]? name);

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>
    /// 寫到 %LOCALAPPDATA% 的檔案是否被導向到某個 MSIX App 的虛擬資料夾。
    /// Claude 桌面版啟動的程式沒有套件身分，但檔案一樣會被導向，所以直接用探測檔檢查。
    /// </summary>
    public static bool Redirected()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string name = $"probe-{Guid.NewGuid():N}.tmp";
        string probe = Path.Combine(AppSettings.DataDir, name);
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(probe, "");
            string packages = Path.Combine(local, "Packages");
            if (!Directory.Exists(packages)) return false;
            foreach (var pkg in Directory.EnumerateDirectories(packages))
            {
                string redirected = Path.Combine(pkg, "LocalCache", "Local", "GlassMonitor", name);
                if (File.Exists(redirected)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    /// <summary>目前程序是否帶有 MSIX 套件身分（也就是在別人的容器裡）。</summary>
    public static bool InPackage()
    {
        try
        {
            uint len = 0;
            return GetCurrentPackageFullName(ref len, null) != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch
        {
            return false; // 舊版 Windows 沒有這個 API，也就不會有 MSIX 容器
        }
    }

    /// <summary>
    /// 在容器裡就請檔案總管（在容器外）重新啟動本程式，回傳 true 表示呼叫端應該結束。
    /// 為避免無限重啟：30 秒內已經嘗試過一次就不再嘗試，照常執行。
    /// </summary>
    public static bool RelaunchOutsideIfNeeded()
    {
        if (!InPackage() && !Redirected()) return false;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        string marker = Path.Combine(Path.GetTempPath(), "HanchengMonitor.relaunch");
        try
        {
            if (File.Exists(marker) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker)).TotalSeconds < 30)
            {
                App.LogInfo("[container] 在其他程式的容器中執行，重新啟動失敗，照常執行（設定可能存到虛擬位置）");
                return false;
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch { }

        try
        {
            // explorer.exe 會把啟動交給目前登入的桌面殼層，新程序就不在容器裡
            App.LogInfo("[container] 偵測到在其他程式的容器中啟動，改由檔案總管在容器外重新啟動");
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return false;
        }
    }
}
