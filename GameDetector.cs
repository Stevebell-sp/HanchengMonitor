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

    private readonly Dictionary<uint, string> _names = new();
    private bool _last;

    /// <summary>目前偵測到的遊戲程式名稱（沒有則為 null）。</summary>
    public string? CurrentGame { get; private set; }

    public bool IsGameForeground(AppSettings s, IntPtr self)
    {
        IntPtr fg = GetForegroundWindow();
        // 使用者點了小工具本身時，維持原本的判斷
        if (fg == IntPtr.Zero || fg == self) return _last;

        GetWindowThreadProcessId(fg, out uint pid);
        string name = NameOf(pid);
        if (pid == (uint)Environment.ProcessId) return _last;

        bool isGame = s.GameProcesses.Any(g => string.Equals(Normalize(g), name, StringComparison.OrdinalIgnoreCase));
        if (!isGame && s.GameDetectFullscreen && !IsShell(fg) && IsFullscreen(fg)) isGame = true;

        CurrentGame = isGame ? name : null;
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
