using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GlassMonitor;

/// <summary>
/// 遊戲伺服器延遲：找出遊戲程式自己建立的 TCP 連線，
/// 讀 Windows 為每條連線統計的平滑往返時間（Smoothed RTT），不額外發送任何封包。
/// 只適用 TCP 連線的遊戲（例如 Minecraft Java 版）；純 UDP 的遊戲讀不到伺服器位址。
/// </summary>
public sealed class GamePing
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const int TcpConnectionEstatsPath = 3;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

    // TCP_ESTATS_PATH_ROD_v0 全部是 ULONG：SmoothedRtt 是第 28 個欄位
    private const int RodSize = 40 * 4;
    private const int SmoothedRttOffset = 27 * 4;

    private readonly HashSet<string> _enabled = new();

    /// <summary>目前量測的伺服器（ip:port），沒有則為空字串。</summary>
    public string Server { get; private set; } = "";

    /// <summary>沒有延遲數值時的原因（給詳細面板顯示）。</summary>
    public string Note { get; private set; } = "";

    /// <summary>回傳毫秒；量不到回傳 NaN。</summary>
    public double Measure(int pid)
    {
        Server = "";
        if (pid <= 0)
        {
            Note = "目前不在遊戲中";
            return double.NaN;
        }
        var conns = Connections(pid);
        if (conns.Count == 0)
        {
            Note = "這款遊戲沒有 TCP 連線（單人模式，或使用 UDP 連線，無法自動偵測伺服器）";
            return double.NaN;
        }
        // 網頁、登入用的 80／443 連線排後面，遊戲本身的長連線排前面
        var best = conns.OrderBy(c => c.RemotePort is 80 or 443 ? 1 : 0)
                        .ThenBy(c => IsPrivate(c.RemoteAddr) ? 1 : 0)
                        .First();
        Server = $"{new IPAddress(best.RemoteAddr)}:{best.RemotePort}";

        var row = new MIB_TCPROW
        {
            State = best.State,
            LocalAddr = best.LocalAddr,
            LocalPort = Swap(best.LocalPort),
            RemoteAddr = best.RemoteAddr,
            RemotePort = Swap(best.RemotePort),
        };
        if (_enabled.Add(Server + "/" + best.LocalPort))
        {
            // 第一次看到這條連線：打開它的延遲統計（需要系統管理員權限）
            IntPtr rw = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(rw, 1);
                SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, rw, 0, 1, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(rw);
            }
            if (_enabled.Count > 200) _enabled.Clear();
        }

        IntPtr rod = Marshal.AllocHGlobal(RodSize);
        try
        {
            for (int i = 0; i < RodSize; i++) Marshal.WriteByte(rod, i, 0);
            uint err = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, RodSize);
            if (err != 0)
            {
                Note = $"讀不到連線統計（錯誤碼 {err}）";
                return double.NaN;
            }
            uint rtt = (uint)Marshal.ReadInt32(rod, SmoothedRttOffset);
            if (rtt == 0 || rtt > 10000)
            {
                Note = "測量中…（連線要有資料往返後才會有數值）";
                return double.NaN;
            }
            Note = "";
            return rtt;
        }
        finally
        {
            Marshal.FreeHGlobal(rod);
        }
    }

    private static uint Swap(uint port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    private static bool IsPrivate(uint addr)
    {
        byte a = (byte)(addr & 0xFF), b = (byte)((addr >> 8) & 0xFF);
        return a == 10 || a == 127 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
    }

    private sealed record Conn(uint State, uint LocalAddr, uint LocalPort, uint RemoteAddr, uint RemotePort);

    /// <summary>某個程式已建立的 IPv4 TCP 連線（埠號已轉成一般數字）。</summary>
    private static List<Conn> Connections(int pid)
    {
        var list = new List<Conn>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        if (size <= 0) return list;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0) return list;
            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                if (r.OwningPid != pid || r.State != MIB_TCP_STATE_ESTAB) continue;
                if ((r.RemoteAddr & 0xFF) == 127) continue; // 本機迴路
                list.Add(new Conn(r.State, r.LocalAddr, Swap(r.LocalPort), r.RemoteAddr, Swap(r.RemotePort)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }
}

/// <summary>
/// 遊戲 FPS：在背景執行 Intel 的 PresentMon（隨安裝程式附上），
/// 只錄目前遊戲的畫面更新，由每幀間隔換算 FPS。離開遊戲就停止。
/// </summary>
public sealed class GameFps : IDisposable
{
    private const string Session = "HanchengMonitorFPS";
    private Process? _proc;
    private int _pid;
    private int _colApp = -1, _colSwap = -1, _colMs = -1;
    private readonly object _lock = new();
    private readonly Queue<(DateTime At, string Swap, double Ms)> _frames = new();

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");
    public static bool Available => File.Exists(ExePath);

    public string Note { get; private set; } = "";

    /// <summary>平均幀時間（毫秒），給詳細面板用。</summary>
    public double FrameTimeMs { get; private set; } = double.NaN;

    /// <summary>1% Low FPS（最慢的 1% 幀換算），給詳細面板用。</summary>
    public double Low1 { get; private set; } = double.NaN;

    /// <summary>回傳最近約 1 秒的 FPS；量不到回傳 NaN。</summary>
    public double Measure(int pid)
    {
        if (pid <= 0)
        {
            Stop();
            Note = "目前不在遊戲中";
            return double.NaN;
        }
        if (!Available)
        {
            Note = "找不到 PresentMon.exe（請重新安裝）";
            return double.NaN;
        }
        if (_pid != pid || _proc == null || _proc.HasExited) Start(pid);

        List<(DateTime At, string Swap, double Ms)> recent;
        lock (_lock)
        {
            var cut = DateTime.UtcNow.AddSeconds(-1.5);
            while (_frames.Count > 0 && _frames.Peek().At < cut) _frames.Dequeue();
            recent = _frames.ToList();
        }
        if (recent.Count < 2)
        {
            Note = "測量中…（遊戲畫面要有更新才會有數值）";
            FrameTimeMs = Low1 = double.NaN;
            return double.NaN;
        }
        // 同一個程式可能有好幾個畫面（啟動器、覆蓋層），取更新最多的那個
        var main = recent.GroupBy(f => f.Swap).OrderByDescending(g => g.Count()).First().Select(f => f.Ms).Where(ms => ms > 0).ToList();
        if (main.Count == 0) return double.NaN;
        double avg = main.Average();
        FrameTimeMs = avg;
        var slow = main.OrderByDescending(x => x).Take(Math.Max(1, main.Count / 100)).Average();
        Low1 = 1000 / slow;
        Note = "";
        return 1000 / avg;
    }

    private void Start(int pid)
    {
        Stop();
        _pid = pid;
        _colApp = _colSwap = _colMs = -1;
        try
        {
            var psi = new ProcessStartInfo(ExePath,
                $"--process_id {pid} --output_stdout --v1_metrics --no_track_input --no_track_gpu --no_track_display " +
                $"--session_name {Session} --stop_existing_session --terminate_on_proc_exit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, _) => { };
            p.Start();
            ChildJob.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
        }
        catch (Exception ex)
        {
            Note = $"無法啟動 PresentMon：{ex.Message}";
            App.LogError(ex);
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var cols = line.Split(',');
        if (_colMs < 0)
        {
            // 第一行是欄位名稱
            _colApp = Array.IndexOf(cols, "Application");
            _colSwap = Array.IndexOf(cols, "SwapChainAddress");
            _colMs = Array.IndexOf(cols, "msBetweenPresents");
            return;
        }
        if (_colMs >= cols.Length) return;
        if (!double.TryParse(cols[_colMs], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) return;
        string swap = _colSwap >= 0 && _colSwap < cols.Length ? cols[_colSwap] : "";
        lock (_lock)
        {
            _frames.Enqueue((DateTime.UtcNow, swap, ms));
            if (_frames.Count > 5000) _frames.Dequeue();
        }
    }

    public void Stop()
    {
        _pid = 0;
        var p = _proc;
        _proc = null;
        lock (_lock) _frames.Clear();
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill();
        }
        catch { }
        p.Dispose();
        // 強制結束可能留下 ETW 錄製工作階段，順手關掉
        _ = Task.Run(() =>
        {
            try
            {
                using var t = Process.Start(new ProcessStartInfo(ExePath, $"--session_name {Session} --terminate_existing_session")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                t?.WaitForExit(5000);
            }
            catch { }
        });
    }

    public void Dispose() => Stop();
}

/// <summary>
/// 把子程序（PresentMon）綁在本程式身上：本程式不管是正常結束、當機或被強制關閉，
/// Windows 都會連同子程序一起結束，不會殘留在背景。
/// </summary>
internal static class ChildJob
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attr, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private static IntPtr _job;

    public static void Add(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
