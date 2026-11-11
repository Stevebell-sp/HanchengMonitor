using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace GlassMonitor;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 被其他程式（例如 MSIX 封裝的 Claude 桌面版）以容器方式啟動時，
        // 寫到 AppData 的設定會被導向到那個程式的虛擬資料夾，開機後就讀不到。
        // 這種情況改由 Windows 檔案總管在容器外重新啟動自己，然後結束。
        if (ContainerGuard.RelaunchOutsideIfNeeded())
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, @"Local\HanchengSystemMonitor", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("瀚誠系統監控已經在執行中。", "瀚誠系統監控");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            LogError(ex.Exception);
            ex.Handled = true;
        };

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
        var w = new MainWindow(settings);
        MainWindow = w;
        if (Array.Exists(e.Args, a => a == "--settings")) w.Loaded += (_, _) => w.OpenSettingsWhenReady();
        w.Show();
    }

    public static void LogInfo(string msg)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
