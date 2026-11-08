using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassMonitor;

/// <summary>判斷前景視窗是不是遊戲：程式名稱在清單裡，或是全螢幕程式（可關閉）。</summary>
public sealed class GameDetector
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>全螢幕也不算遊戲的程式：瀏覽器（看影片）、影片播放器、簡報、遠端桌面等。</summary>
    private static readonly HashSet<string> NonGameFullscreen = new(StringComparer.OrdinalIgnoreCase)
    {
        // 瀏覽器
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "arc", "whale", "librewolf", "waterfox", "chromium",
        // 影片播放器、串流
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "PotPlayer", "PotPlayer64", "PotPlayerMini", "PotPlayerMini64",
        "wmplayer", "Video.UI", "Microsoft.Media.Player", "mpv", "KMPlayer", "KMPlayer64X", "GOM", "Netflix", "Disney+",
        "ApplicationFrameHost", "kodi", "Plex", "Spotify",
        // 簡報、圖片、遠端
        "POWERPNT", "Microsoft.Photos", "PhotosApp", "mstsc", "msrdc",
    };

    private readonly Dictionary<uint, string> _names = new();
    private bool _last;

    /// <summary>目前偵測到的遊戲程式名稱（沒有則為 null）。</summary>
    public string? CurrentGame { get; private set; }

    /// <summary>目前遊戲的程序 ID（沒有則為 0）。</summary>
    public int CurrentPid { get; private set; }

    /// <summary>目前遊戲的視窗（沒有則為 0）。</summary>
    public IntPtr CurrentWindow { get; private set; }

    /// <summary>
    /// 遊戲是視窗模式時，回傳遊戲畫面（不含標題列與邊框）在螢幕上的範圍（實際像素）；
    /// 全螢幕、最小化或找不到視窗時回傳 null。
    /// </summary>
    public System.Drawing.Rectangle? WindowedGameArea()
    {
        IntPtr h = CurrentWindow;
        if (h == IntPtr.Zero || !IsWindow(h) || IsIconic(h) || IsFullscreen(h)) return null;
        if (!GetClientRect(h, out var c)) return null;
        var p = new POINT();
        if (!ClientToScreen(h, ref p)) return null;
        int w = c.Right - c.Left, hh = c.Bottom - c.Top;
        if (w < 200 || hh < 150) return null; // 太小的視窗（啟動器、對話框）不算
        return new System.Drawing.Rectangle(p.X, p.Y, w, hh);
    }

    public bool IsGameForeground(AppSettings s, IntPtr self)
    {
        IntPtr fg = GetForegroundWindow();
        // 使用者點了小工具本身時，維持原本的判斷
        if (fg == IntPtr.Zero || fg == self) return _last;

        GetWindowThreadProcessId(fg, out uint pid);
        string name = NameOf(pid);
        if (pid == (uint)Environment.ProcessId) return _last;

        bool isGame = s.GameProcesses.Any(g => string.Equals(Normalize(g), name, StringComparison.OrdinalIgnoreCase));
        if (!isGame && s.GameDetectFullscreen && !NonGameFullscreen.Contains(name) && !IsShell(fg) && IsFullscreen(fg)) isGame = true;

        CurrentGame = isGame ? name : null;
        CurrentPid = isGame ? (int)pid : 0;
        CurrentWindow = isGame ? fg : IntPtr.Zero;
        _last = isGame;
        return isGame;
    }

    public static string Normalize(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private string NameOf(uint pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        if (_names.Count > 500) _names.Clear();
        try
        {
            using var p = Process.GetProcessById((int)pid);
            n = p.ProcessName;
        }
        catch
        {
            n = "";
        }
        _names[pid] = n;
        return n;
    }

    private static bool IsShell(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return ShellClasses.Contains(sb.ToString());
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}
