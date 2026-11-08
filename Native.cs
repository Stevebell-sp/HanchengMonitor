using System;
using System.Runtime.InteropServices;

namespace GlassMonitor;

/// <summary>Win32 / DWM 呼叫集中在這裡。</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_APPWINDOW = 0x40000;
    private const long WS_EX_LAYERED = 0x80000;

    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private static long GetEx(IntPtr h) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64() : GetWindowLong32(h, GWL_EXSTYLE);

    private static void SetEx(IntPtr h, long v)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(h, GWL_EXSTYLE, new IntPtr(v));
        else SetWindowLong32(h, GWL_EXSTYLE, (int)v);
    }

    /// <summary>不出現在 Alt+Tab 與工作列。</summary>
    public static void SetToolWindow(IntPtr h)
    {
        long ex = GetEx(h);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetEx(h, ex);
    }

    /// <summary>點擊時不搶走焦點（玩遊戲時點到小工具，遊戲也不會失去焦點）。</summary>
    public static void SetNoActivate(IntPtr h) => SetEx(h, GetEx(h) | 0x08000000 /* WS_EX_NOACTIVATE */);

    /// <summary>
    /// 滑鼠穿透：點擊直接落到後面的視窗。
    /// 只對「建立時就是透明分層視窗」（WPF AllowsTransparency=True）有效；
    /// 一般 WPF 視窗事後加不上 WS_EX_LAYERED（Windows 會直接忽略），所以切換穿透時主視窗會重建。
    /// </summary>
    public static void SetClickThrough(IntPtr h, bool on)
    {
        long ex = GetEx(h);
        if ((ex & WS_EX_LAYERED) == 0) return; // 不是分層視窗，無法穿透
        bool isOn = (ex & WS_EX_TRANSPARENT) != 0;
        if (on == isOn) return;
        SetEx(h, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    /// <summary>分層視窗沒有 DWM 圓角，用視窗區域裁成圓角（像素座標）。</summary>
    public static void SetRoundRegion(IntPtr hwnd, int width, int height, int radius)
    {
        IntPtr rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        SetWindowRgn(hwnd, rgn, true); // 系統接管 rgn，不用自己刪
    }

    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_MINIMIZE = 0xF020;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint SWP_HIDEWINDOW = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    /// <summary>被最小化或隱藏時，不搶焦點地顯示回來。</summary>
    public static void EnsureShown(IntPtr h)
    {
        if (IsIconic(h) || !IsWindowVisible(h)) ShowWindow(h, SW_SHOWNOACTIVATE);
    }

    public static void ReassertTopmost(IntPtr h) =>
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>毛玻璃。顏色格式是 AABBGGRR。</summary>
    public static void SetAccent(IntPtr hwnd, int state, uint abgr)
    {
        var accent = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = abgr };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Windows 11 圓角：2 = 一般圓角，3 = 小圓角。</summary>
    public static void SetCorner(IntPtr hwnd, int preference)
    {
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
    }

    /// <summary>關掉 DWM 自己畫的外框（改由我們畫）。</summary>
    public static void HideDwmBorder(IntPtr hwnd)
    {
        int none = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref none, sizeof(int));
    }
}
