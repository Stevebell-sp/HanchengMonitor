using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace GlassMonitor;

public sealed record MetricDef(string Id, string Title);

public sealed class MetricState
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; set; } = "—";
    /// <summary>0~1：背景填色比例；NaN 表示不填。</summary>
    public double Fraction { get; set; } = double.NaN;
    /// <summary>0 正常、1 偏高、2 危險。</summary>
    public int Level { get; set; }
    public double[]? History { get; set; }
    public bool HistoryIsPercent { get; set; }
    /// <summary>原始數值（警示、歷史紀錄用）。</summary>
    public double Value { get; set; } = double.NaN;
    /// <summary>超過使用者設定的警示門檻。</summary>
    public bool Alert { get; set; }
}

public sealed class ChartData
{
    public string Id = "";
    public string Name = "";
    public double[] Values = Array.Empty<double>();
    public bool Percent;
    public string Unit = "%";
}

public sealed record KV(string Key, string Value);
public sealed record TopRow(string Name, string Value, double Fraction);

/// <summary>點格子後的詳細面板資料。</summary>
public sealed class DetailData
{
    public string Category = "";
    public List<ChartData> Charts = new();
    public double[]? PerCore;
    public List<KV> Info = new();
    public string TopTitle = "";
    public string TopColorId = "cpu";
    public List<TopRow> Top = new();
    public List<SensorRow>? Sensors;
    public string Note = "";
}

public sealed class SampleResult
{
    public Dictionary<string, MetricState> States = new();
    public DetailData? Detail;
}

/// <summary>收集系統數據。只在背景執行緒、依序呼叫 Sample()。</summary>
public sealed class MetricsService : IDisposable
{
    public const int HistoryLength = 40;
    public const int LongLength = 300;

    private readonly List<MetricDef> _defs = new();
    private readonly Dictionary<string, string> _titles = new();
    private readonly Dictionary<string, Queue<double>> _hist = new();
    private readonly Dictionary<string, Queue<double>> _long = new();

    public IReadOnlyList<MetricDef> Definitions => _defs;

    /// <summary>詳細面板目前要看的類別（null 表示沒開），由 UI 設定。</summary>
    public volatile string? DetailWanted;

    // CPU
    private long _pIdle, _pKernel, _pUser;
    private bool _cpuReady;
    private List<PerformanceCounter>? _cores;
    private readonly string _cpuName;

    // 程序
    private Dictionary<int, TimeSpan> _procPrev = new();
    private DateTime _procTime = DateTime.MinValue;
    private List<(string Name, double Pct)> _topCpu = new();
    private List<(string Name, long Bytes)> _topMem = new();
    private Dictionary<int, ulong> _ioPrev = new();
    private DateTime _ioTime = DateTime.MinValue;
    private List<(string Name, double Bps)> _topIo = new();

    // 磁碟
    private sealed class DriveCounters
    {
        public string Letter = "";
        public PerformanceCounter? Read;
        public PerformanceCounter? Write;
        public double LastRead, LastWrite;
    }
    private readonly List<DriveCounters> _drives = new();
    private readonly List<PerformanceCounter> _diskIdle = new();

    // GPU
    private readonly PerformanceCounterCategory? _gpuEngine;
    private readonly PerformanceCounterCategory? _gpuMem;
    private Dictionary<string, CounterSample> _gpuPrev = new();
    private Dictionary<int, double> _gpuByPid = new();

    // 硬體感測
    private readonly HardwareReader? _hw;
    private HwSnapshot _hwSnap = new();
    private DateTime _hwTime = DateTime.MinValue;

    // 網路
    private long _netRx = -1, _netTx = -1;
    private DateTime _netTime;
    private Dictionary<string, (long Rx, long Tx)> _netPrevAd = new();
    private List<(string Name, double Down, double Up)> _netAdapters = new();

    // 電池
    private readonly bool _hasBattery;

    // 給詳細面板用的最近一次數值
    private double _lastCpu, _lastGpu = double.NaN, _lastMemPct;
    private ulong _memTotal, _memAvail, _commitTotal, _commitAvail;

    public MetricsService()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string letter = d.Name.Substring(0, 2);
                _drives.Add(new DriveCounters
                {
                    Letter = letter,
                    Read = TryCounter("LogicalDisk", "Disk Read Bytes/sec", letter),
                    Write = TryCounter("LogicalDisk", "Disk Write Bytes/sec", letter),
                });
            }
            catch { }
        }

        try
        {
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            foreach (var inst in cat.GetInstanceNames())
            {
                if (inst == "_Total") continue;
                var c = TryCounter("PhysicalDisk", "% Idle Time", inst);
                if (c != null) _diskIdle.Add(c);
            }
        }
        catch { }

        try { if (PerformanceCounterCategory.Exists("GPU Engine")) _gpuEngine = new PerformanceCounterCategory("GPU Engine"); } catch { }
        try { if (PerformanceCounterCategory.Exists("GPU Adapter Memory")) _gpuMem = new PerformanceCounterCategory("GPU Adapter Memory"); } catch { }
        try { _hw = new HardwareReader(); } catch (Exception ex) { App.LogError(ex); }

        try
        {
            var ps = WF.SystemInformation.PowerStatus;
            _hasBattery = (ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) == 0
                          && ps.BatteryChargeStatus != WF.BatteryChargeStatus.Unknown;
        }
        catch { }

        _cpuName = _hw?.CpuName is { Length: > 0 } n ? n : ReadCpuName();

        Def("slow", "變慢原因");
        Def("cpu", "CPU 使用率");
        Def("cpuproc", "CPU 最忙程式");
        if (_hw != null)
        {
            Def("cputemp", "CPU 溫度");
            Def("cpuclock", "CPU 時脈");
            Def("cpupower", "CPU 功耗");
        }
        Def("memproc", "記憶體最多程式");
        if (_gpuEngine != null) Def("gpu", "GPU 使用率");
        if (_hw?.HasGpu == true) Def("gputemp", "GPU 溫度");
        if (_hw?.HasGpu == true || _gpuMem != null) Def("gpumem", "GPU 記憶體");
        if (_hw?.HasGpu == true)
        {
            Def("gpuclock", "GPU 時脈");
            Def("gpupower", "GPU 功耗");
            Def("gpufan", "GPU 風扇");
        }
        if (_hw?.HasFans == true) Def("fan", "風扇轉速");
        Def("memused", "已用記憶體");
        Def("memfree", "可用記憶體");
        foreach (var d in _drives) Def($"disk.{d.Letter}.read", $"{d.Letter} 讀取");
        foreach (var d in _drives) Def($"disk.{d.Letter}.write", $"{d.Letter} 寫入");
        if (_diskIdle.Count > 0) Def("diskbusy", "硬碟繁忙");
        Def("netdown", "網路下載");
        Def("netup", "網路上傳");
        if (_hasBattery) Def("battery", "電池");
        Def("uptime", "開機時間");

        Sample(); // 先取一次當基準
    }

    private void Def(string id, string title)
    {
        _defs.Add(new MetricDef(id, title));
        _titles[id] = title;
    }

    private static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, true);
            c.NextValue();
            return c;
        }
        catch
        {
            return null;
        }
    }

    // ───── 分類 ─────

    public static string CategoryOf(string id) => id switch
    {
        "cpu" or "cpuproc" or "cpuclock" or "cpupower" or "slow" => "cpu",
        "cputemp" or "fan" or "gpufan" => "sensors",
        "gpu" or "gpumem" or "gputemp" or "gpuclock" or "gpupower" => "gpu",
        "memused" or "memfree" or "memproc" => "mem",
        "netdown" or "netup" => "net",
        _ when id.StartsWith("disk") => "disk",
        _ => "system",
    };

    public static string CategoryTitle(string cat) => cat switch
    {
        "cpu" => "處理器",
        "sensors" => "溫度與風扇",
        "gpu" => "顯示卡",
        "mem" => "記憶體",
        "disk" => "磁碟",
        "net" => "網路",
        _ => "系統",
    };

    public static string CategoryIconId(string cat) => cat switch
    {
        "cpu" => "cpu",
        "sensors" => "cputemp",
        "gpu" => "gpu",
        "mem" => "memused",
        "disk" => "diskbusy",
        "net" => "netdown",
        _ => "uptime",
    };

    // ───── 取樣 ─────

    public SampleResult Sample()
    {
        var r = new Dictionary<string, MetricState>();
        MetricState S(string id)
        {
            var s = new MetricState { Id = id, Title = _titles.TryGetValue(id, out var t) ? t : id };
            r[id] = s;
            return s;
        }
        string? detail = DetailWanted;

        // CPU
        double cpu = SampleCpu();
        _lastCpu = cpu;
        var sc = S("cpu");
        sc.Text = $"{cpu:0}%";
        sc.Value = cpu;
        sc.Fraction = cpu / 100;
        sc.Level = Lv(cpu, 80, 95);
        sc.History = Push("cpu", cpu);
        sc.HistoryIsPercent = true;
        PushLong("cpu", cpu);

        // 程序（比較耗資源，約 2 秒一次）
        if ((DateTime.UtcNow - _procTime).TotalSeconds >= 1.9) SampleProcesses();
        S("cpuproc").Text = _topCpu.Count > 0 ? _topCpu[0].Name : "—";
        S("memproc").Text = _topMem.Count > 0 ? _topMem[0].Name : "—";

        // 記憶體
        double memPct = 0;
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref ms) && ms.ullTotalPhys > 0)
        {
            _memTotal = ms.ullTotalPhys;
            _memAvail = ms.ullAvailPhys;
            _commitTotal = ms.ullTotalPageFile;
            _commitAvail = ms.ullAvailPageFile;
            ulong used = ms.ullTotalPhys - ms.ullAvailPhys;
            memPct = used * 100.0 / ms.ullTotalPhys;
            var mu = S("memused");
            mu.Text = FormatSize(used);
            mu.Value = memPct;
            mu.Fraction = memPct / 100;
            mu.Level = Lv(memPct, 85, 95);
            var mf = S("memfree");
            mf.Text = FormatSize(ms.ullAvailPhys);
            mf.Fraction = 1 - memPct / 100;
            mf.Level = Lv(memPct, 85, 95);
            PushLong("memused", memPct);
        }
        _lastMemPct = memPct;

        // 硬體感測（約 2 秒一次；看感測器面板時每次都讀）
        if (_hw != null && ((DateTime.UtcNow - _hwTime).TotalSeconds >= 1.9 || detail == "sensors"))
        {
            try { _hwSnap = _hw.Read(detail == "sensors"); } catch { }
            _hwTime = DateTime.UtcNow;
        }
        var hw = _hwSnap;

        // GPU
        double gpu = double.NaN;
        if (_gpuEngine != null)
        {
            gpu = SampleGpu();
            var g = S("gpu");
            if (!double.IsNaN(gpu))
            {
                g.Text = $"{gpu:0}%";
                g.Value = gpu;
                g.Fraction = gpu / 100;
                g.Level = Lv(gpu, 80, 95);
                g.History = Push("gpu", gpu);
                g.HistoryIsPercent = true;
                PushLong("gpu", gpu);
            }
        }
        _lastGpu = gpu;

        if (_titles.ContainsKey("gpumem"))
        {
            var gm = S("gpumem");
            if (!double.IsNaN(hw.GpuMemUsedMB))
            {
                double used = hw.GpuMemUsedMB * 1024 * 1024;
                gm.Text = FormatSize(used);
                if (!double.IsNaN(hw.GpuMemTotalMB) && hw.GpuMemTotalMB > 0)
                {
                    double pct = hw.GpuMemUsedMB / hw.GpuMemTotalMB * 100;
                    gm.Value = pct;
                    gm.Fraction = pct / 100;
                    gm.Level = Lv(pct, 85, 95);
                }
            }
            else if (_gpuMem != null)
            {
                long bytes = SampleGpuMem();
                if (bytes >= 0) gm.Text = FormatSize(bytes);
            }
        }

        // 溫度、時脈、功耗、風扇
        if (_hw != null)
        {
            TempCell(S("cputemp"), hw.CpuTemp, 80, 90);
            PushLong("cputemp", hw.CpuTemp);
            Simple(S("cpuclock"), hw.CpuClock, v => $"{v / 1000:0.00} GHz");
            Simple(S("cpupower"), hw.CpuPower, v => $"{v:0} W");
            if (_hw.HasGpu)
            {
                TempCell(S("gputemp"), hw.GpuTemp, 80, 88);
                PushLong("gputemp", hw.GpuTemp);
                Simple(S("gpuclock"), hw.GpuClock, v => $"{v:0} MHz");
                Simple(S("gpupower"), hw.GpuPower, v => $"{v:0} W");
                Simple(S("gpufan"), hw.GpuFan, v => $"{v:0} RPM");
            }
            if (_hw.HasFans) Simple(S("fan"), hw.FanMax, v => $"{v:0} RPM");
        }

        // 磁碟
        foreach (var d in _drives)
        {
            double rd = Next(d.Read), wr = Next(d.Write);
            d.LastRead = rd;
            d.LastWrite = wr;
            var a = S($"disk.{d.Letter}.read");
            if (!double.IsNaN(rd)) { a.Text = FormatSize(rd); a.Value = rd; a.History = Push(a.Id, rd); }
            var b = S($"disk.{d.Letter}.write");
            if (!double.IsNaN(wr)) { b.Text = FormatSize(wr); b.Value = wr; b.History = Push(b.Id, wr); }
        }

        double busy = double.NaN;
        if (_diskIdle.Count > 0)
        {
            busy = 0;
            foreach (var c in _diskIdle)
            {
                double idle = Next(c);
                if (!double.IsNaN(idle)) busy = Math.Max(busy, Math.Clamp(100 - idle, 0, 100));
            }
            var db = S("diskbusy");
            db.Text = $"{busy:0}%";
            db.Value = busy;
            db.Fraction = busy / 100;
            db.Level = Lv(busy, 70, 90);
            db.History = Push("diskbusy", busy);
            db.HistoryIsPercent = true;
            PushLong("diskbusy", busy);
        }

        // 網路
        var (down, up) = SampleNet();
        var nd = S("netdown");
        nd.Text = FormatSize(down);
        nd.Value = down;
        nd.History = Push("netdown", down);
        var nu = S("netup");
        nu.Text = FormatSize(up);
        nu.Value = up;
        nu.History = Push("netup", up);
        PushLong("netdown", down);
        PushLong("netup", up);

        // 電池
        if (_hasBattery)
        {
            try
            {
                var ps = WF.SystemInformation.PowerStatus;
                double pct = ps.BatteryLifePercent * 100;
                bool charging = ps.PowerLineStatus == WF.PowerLineStatus.Online;
                var bt = S("battery");
                bt.Text = charging ? $"{pct:0}% ⚡" : $"{pct:0}%";
                bt.Value = pct;
                bt.Fraction = pct / 100;
                bt.Level = charging ? 0 : pct <= 10 ? 2 : pct <= 20 ? 1 : 0;
            }
            catch { }
        }

        // 開機時間
        S("uptime").Text = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));

        // 變慢原因：挑最接近滿載的那一項
        var cands = new List<(double score, string text)> { (cpu, _topCpu.Count > 0 ? _topCpu[0].Name : "CPU") };
        if (!double.IsNaN(busy)) cands.Add((busy, "硬碟"));
        if (!double.IsNaN(gpu)) cands.Add((gpu, "GPU"));
        if (memPct >= 80) cands.Add((memPct, "記憶體"));
        var best = cands.MaxBy(c => c.score);
        var sl = S("slow");
        if (best.score < 30)
        {
            sl.Text = "順暢";
        }
        else
        {
            sl.Text = best.text;
            sl.Level = best.score >= 80 ? 2 : best.score >= 50 ? 1 : 0;
            if (sl.Level > 0) sl.Fraction = 1;
        }

        var result = new SampleResult { States = r };
        if (detail != null)
        {
            try { result.Detail = BuildDetail(detail, hw); } catch (Exception ex) { App.LogError(ex); }
        }
        return result;
    }

    private static int Lv(double v, double warn, double crit) => v >= crit ? 2 : v >= warn ? 1 : 0;

    private void TempCell(MetricState s, double t, double warn, double crit)
    {
        if (double.IsNaN(t))
        {
            s.Text = "-----";
            return;
        }
        s.Text = $"{t:0} °C";
        s.Value = t;
        s.Level = Lv(t, warn, crit);
        s.History = Push(s.Id, t);
        s.HistoryIsPercent = true; // 以 0~100 °C 為刻度
    }

    private static void Simple(MetricState s, double v, Func<double, string> fmt)
    {
        s.Text = double.IsNaN(v) ? "-----" : fmt(v);
        s.Value = v;
    }

    public static string FormatUptime(TimeSpan ut) =>
        ut.TotalDays >= 1 ? $"{(int)ut.TotalDays}d {ut.Hours}h" : $"{ut.Hours}h {ut.Minutes:00}m";

    private double[] Push(string id, double v)
    {
        if (!_hist.TryGetValue(id, out var q)) _hist[id] = q = new Queue<double>();
        q.Enqueue(double.IsNaN(v) ? 0 : v);
        while (q.Count > HistoryLength) q.Dequeue();
        return q.ToArray();
    }

    private void PushLong(string id, double v)
    {
        if (!_long.TryGetValue(id, out var q)) _long[id] = q = new Queue<double>();
        q.Enqueue(v);
        while (q.Count > LongLength) q.Dequeue();
    }

    private double[] Long(string id) => _long.TryGetValue(id, out var q) ? q.ToArray() : Array.Empty<double>();

    private static double Next(PerformanceCounter? c)
    {
        if (c == null) return double.NaN;
        try { return c.NextValue(); } catch { return double.NaN; }
    }

    private double SampleCpu()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        double v = 0;
        if (_cpuReady)
        {
            long di = idle - _pIdle, dk = kernel - _pKernel, du = user - _pUser;
            long total = dk + du; // kernel 時間已包含 idle
            if (total > 0) v = Math.Clamp((total - di) * 100.0 / total, 0, 100);
        }
        _pIdle = idle; _pKernel = kernel; _pUser = user; _cpuReady = true;
        return v;
    }

    private void SampleProcesses()
    {
        var now = DateTime.UtcNow;
        double elapsed = (now - _procTime).TotalSeconds;
        var cur = new Dictionary<int, TimeSpan>();
        var cpuByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var memByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0) continue;
                    string name = p.ProcessName;
                    try { memByName[name] = memByName.GetValueOrDefault(name) + p.WorkingSet64; } catch { }
                    TimeSpan t;
                    try { t = p.TotalProcessorTime; } catch { continue; }
                    cur[p.Id] = t;
                    if (_procPrev.TryGetValue(p.Id, out var prev) && elapsed > 0 && elapsed < 30)
                    {
                        double pct = (t - prev).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100;
                        if (pct > 0) cpuByName[name] = cpuByName.GetValueOrDefault(name) + pct;
                    }
                }
                catch { }
            }
        }

        _procPrev = cur;
        _procTime = now;
        _topCpu = cpuByName.OrderByDescending(kv => kv.Value).Take(10).Select(kv => (Short(kv.Key), kv.Value)).ToList();
        _topMem = memByName.OrderByDescending(kv => kv.Value).Take(10).Select(kv => (Short(kv.Key), kv.Value)).ToList();
    }

    /// <summary>各程式的 I/O 流量（只有開磁碟面板時才算）。</summary>
    private void SampleIo()
    {
        var now = DateTime.UtcNow;
        double elapsed = (now - _ioTime).TotalSeconds;
        var cur = new Dictionary<int, ulong>();
        var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue;
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (!Native.GetProcessIoCounters(h, out var c)) continue;
                    ulong total = c.ReadTransferCount + c.WriteTransferCount;
                    cur[p.Id] = total;
                    if (_ioPrev.TryGetValue(p.Id, out var prev) && total >= prev && elapsed > 0 && elapsed < 30)
                    {
                        double bps = (total - prev) / elapsed;
                        if (bps > 0) byName[p.ProcessName] = byName.GetValueOrDefault(p.ProcessName) + bps;
                    }
                }
                catch { }
                finally
                {
                    Native.CloseHandle(h);
                }
            }
        }

        _ioPrev = cur;
        _ioTime = now;
        _topIo = byName.OrderByDescending(kv => kv.Value).Take(10).Select(kv => (Short(kv.Key), kv.Value)).ToList();
    }

    private static string Short(string s) => s.Length > 16 ? s[..15] + "…" : s;

    private static (int, int) CoreKey(string name)
    {
        var parts = name.Split(',');
        int a = parts.Length > 0 && int.TryParse(parts[0], out var x) ? x : 0;
        int b = parts.Length > 1 && int.TryParse(parts[1], out var y) ? y : 0;
        return (a, b);
    }

    private double[]? SampleCores()
    {
        try
        {
            if (_cores == null)
            {
                var cat = new PerformanceCounterCategory("Processor Information");
                var names = cat.GetInstanceNames().Where(n => !n.Contains("_Total")).OrderBy(CoreKey).ToList();
                _cores = names.Select(n => new PerformanceCounter("Processor Information", "% Processor Utility", n, true)).ToList();
                foreach (var c in _cores) c.NextValue();
                return new double[_cores.Count];
            }
            return _cores.Select(c => Math.Clamp((double)c.NextValue(), 0, 100)).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private double SampleGpu()
    {
        try
        {
            var data = _gpuEngine!.ReadCategory();
            var col = data["Utilization Percentage"];
            if (col == null) return double.NaN;
            var cur = new Dictionary<string, CounterSample>();
            var engines = new Dictionary<string, double>();
            var byPid = new Dictionary<int, double>();
            foreach (InstanceData inst in col.Values)
            {
                string name = inst.InstanceName;
                var s = inst.Sample;
                cur[name] = s;
                if (!_gpuPrev.TryGetValue(name, out var prev)) continue;
                double v = CounterSample.Calculate(prev, s);
                int idx = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                string key = idx >= 0 ? name[idx..] : name;
                engines[key] = engines.GetValueOrDefault(key) + v;
                // pid_1234_luid_...
                if (v > 0 && name.StartsWith("pid_"))
                {
                    int end = name.IndexOf('_', 4);
                    if (end > 4 && int.TryParse(name.AsSpan(4, end - 4), out int pid))
                        byPid[pid] = Math.Max(byPid.GetValueOrDefault(pid), v);
                }
            }
            _gpuPrev = cur;
            _gpuByPid = byPid;
            return engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
        }
        catch
        {
            return double.NaN;
        }
    }

    private long SampleGpuMem()
    {
        try
        {
            var data = _gpuMem!.ReadCategory();
            var col = data["Dedicated Usage"];
            if (col == null) return -1;
            long sum = 0;
            foreach (InstanceData i in col.Values) sum += i.RawValue;
            return sum;
        }
        catch
        {
            return -1;
        }
    }

    private (double down, double up) SampleNet()
    {
        long rx = 0, tx = 0;
        var now = DateTime.UtcNow;
        double sec = _netRx >= 0 ? (now - _netTime).TotalSeconds : 0;
        var prevAd = _netPrevAd;
        var curAd = new Dictionary<string, (long, long)>();
        var adapters = new List<(string, double, double)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                    curAd[ni.Id] = (st.BytesReceived, st.BytesSent);
                    double d = 0, u = 0;
                    if (sec > 0 && prevAd.TryGetValue(ni.Id, out var p))
                    {
                        d = Math.Max(0, (st.BytesReceived - p.Rx) / sec);
                        u = Math.Max(0, (st.BytesSent - p.Tx) / sec);
                    }
                    adapters.Add((ni.Name, d, u));
                }
                catch { }
            }
        }
        catch { }

        double down = 0, up = 0;
        if (sec > 0)
        {
            down = Math.Max(0, (rx - _netRx) / sec);
            up = Math.Max(0, (tx - _netTx) / sec);
        }
        _netRx = rx; _netTx = tx; _netTime = now;
        _netPrevAd = curAd;
        _netAdapters = adapters;
        return (down, up);
    }

    // ───── 詳細面板 ─────

    private DetailData BuildDetail(string cat, HwSnapshot hw)
    {
        var d = new DetailData { Category = cat };
        switch (cat)
        {
            case "cpu":
                d.Charts.Add(new ChartData { Id = "cpu", Name = "使用率", Values = Long("cpu"), Percent = true });
                if (_hw != null) d.Charts.Add(new ChartData { Id = "cputemp", Name = "溫度", Values = Long("cputemp"), Percent = true, Unit = "°C" });
                d.PerCore = SampleCores();
                d.Info.Add(new KV("型號", _cpuName));
                d.Info.Add(new KV("邏輯處理器", $"{Environment.ProcessorCount} 個"));
                d.Info.Add(new KV("使用率", $"{_lastCpu:0}%"));
                if (!double.IsNaN(hw.CpuClock)) d.Info.Add(new KV("平均時脈", $"{hw.CpuClock / 1000:0.00} GHz"));
                if (!double.IsNaN(hw.CpuTemp)) d.Info.Add(new KV("溫度", $"{hw.CpuTemp:0.0} °C"));
                if (!double.IsNaN(hw.CpuPower)) d.Info.Add(new KV("功耗", $"{hw.CpuPower:0.0} W"));
                d.TopTitle = "最耗 CPU 的程式";
                d.TopColorId = "cpu";
                foreach (var (name, pct) in _topCpu) d.Top.Add(new TopRow(name, $"{pct:0.0}%", pct / 100));
                break;

            case "gpu":
                d.Charts.Add(new ChartData { Id = "gpu", Name = "使用率", Values = Long("gpu"), Percent = true });
                d.Charts.Add(new ChartData { Id = "gputemp", Name = "溫度", Values = Long("gputemp"), Percent = true, Unit = "°C" });
                d.Info.Add(new KV("型號", _hw?.GpuName ?? ""));
                if (!double.IsNaN(_lastGpu)) d.Info.Add(new KV("使用率", $"{_lastGpu:0}%"));
                if (!double.IsNaN(hw.GpuMemUsedMB))
                {
                    string total = double.IsNaN(hw.GpuMemTotalMB) ? "" : $" / {FormatSize(hw.GpuMemTotalMB * 1024 * 1024)}";
                    d.Info.Add(new KV("顯示記憶體", $"{FormatSize(hw.GpuMemUsedMB * 1024 * 1024)}{total}"));
                }
                if (!double.IsNaN(hw.GpuTemp)) d.Info.Add(new KV("溫度", $"{hw.GpuTemp:0.0} °C"));
                if (!double.IsNaN(hw.GpuClock)) d.Info.Add(new KV("核心時脈", $"{hw.GpuClock:0} MHz"));
                if (!double.IsNaN(hw.GpuPower)) d.Info.Add(new KV("功耗", $"{hw.GpuPower:0.0} W"));
                if (!double.IsNaN(hw.GpuFan)) d.Info.Add(new KV("風扇", $"{hw.GpuFan:0} RPM"));
                d.TopTitle = "使用 GPU 的程式";
                d.TopColorId = "gpu";
                var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var (pid, v) in _gpuByPid)
                {
                    string name;
                    try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { continue; }
                    byName[name] = Math.Max(byName.GetValueOrDefault(name), v);
                }
                foreach (var kv in byName.OrderByDescending(kv => kv.Value).Take(10))
                    d.Top.Add(new TopRow(Short(kv.Key), $"{kv.Value:0.0}%", kv.Value / 100));
                break;

            case "mem":
                d.Charts.Add(new ChartData { Id = "memused", Name = "使用率", Values = Long("memused"), Percent = true });
                d.Info.Add(new KV("總容量", FormatSize(_memTotal)));
                d.Info.Add(new KV("已使用", $"{FormatSize(_memTotal - _memAvail)}（{_lastMemPct:0}%）"));
                d.Info.Add(new KV("可用", FormatSize(_memAvail)));
                if (_commitTotal > 0)
                    d.Info.Add(new KV("認可大小", $"{FormatSize(_commitTotal - _commitAvail)} / {FormatSize(_commitTotal)}"));
                d.TopTitle = "最佔記憶體的程式";
                d.TopColorId = "memused";
                foreach (var (name, bytes) in _topMem)
                    d.Top.Add(new TopRow(name, FormatSize(bytes), _memTotal > 0 ? bytes / (double)_memTotal : 0));
                break;

            case "disk":
                d.Charts.Add(new ChartData { Id = "diskbusy", Name = "繁忙程度", Values = Long("diskbusy"), Percent = true });
                foreach (var dr in _drives)
                {
                    string free = "";
                    try
                    {
                        var di = new DriveInfo(dr.Letter);
                        free = $"，可用 {FormatSize(di.AvailableFreeSpace)} / {FormatSize(di.TotalSize)}";
                    }
                    catch { }
                    d.Info.Add(new KV(dr.Letter, $"讀 {FormatSize(Z(dr.LastRead))}/s　寫 {FormatSize(Z(dr.LastWrite))}/s{free}"));
                }
                SampleIo();
                d.TopTitle = "I/O 流量最大的程式";
                d.TopColorId = "diskbusy";
                double maxIo = _topIo.Count > 0 ? Math.Max(1, _topIo[0].Bps) : 1;
                foreach (var (name, bps) in _topIo) d.Top.Add(new TopRow(name, $"{FormatSize(bps)}/s", bps / maxIo));
                d.Note = "I/O 流量包含磁碟與其他輸入輸出，與工作管理員的「磁碟」欄位可能略有差異。";
                break;

            case "net":
                d.Charts.Add(new ChartData { Id = "netdown", Name = "下載", Values = Long("netdown"), Unit = "B/s" });
                d.Charts.Add(new ChartData { Id = "netup", Name = "上傳", Values = Long("netup"), Unit = "B/s" });
                foreach (var (name, down, up) in _netAdapters)
                    d.Info.Add(new KV(name, $"↓ {FormatSize(down)}/s　↑ {FormatSize(up)}/s"));
                d.Note = "各程式的網路流量需要額外的系統追蹤，目前只列出各網路卡。";
                break;

            case "sensors":
                d.Charts.Add(new ChartData { Id = "cputemp", Name = "CPU", Values = Long("cputemp"), Percent = true, Unit = "°C" });
                d.Charts.Add(new ChartData { Id = "gputemp", Name = "GPU", Values = Long("gputemp"), Percent = true, Unit = "°C" });
                d.Sensors = hw.All;
                break;

            default:
                var up2 = TimeSpan.FromMilliseconds(Environment.TickCount64);
                d.Info.Add(new KV("作業系統", RuntimeInformation.OSDescription));
                d.Info.Add(new KV("電腦名稱", Environment.MachineName));
                d.Info.Add(new KV("開機時間", (DateTime.Now - up2).ToString("yyyy/MM/dd HH:mm")));
                d.Info.Add(new KV("已開機", FormatUptime(up2)));
                d.Info.Add(new KV("處理器", _cpuName));
                if (_hw?.HasGpu == true) d.Info.Add(new KV("顯示卡", _hw.GpuName));
                d.Info.Add(new KV("記憶體", FormatSize(_memTotal)));
                if (_hasBattery)
                {
                    var ps = WF.SystemInformation.PowerStatus;
                    d.Info.Add(new KV("電池", $"{ps.BatteryLifePercent * 100:0}%（{(ps.PowerLineStatus == WF.PowerLineStatus.Online ? "接上電源" : "使用電池")}）"));
                }
                break;
        }
        return d;
    }

    private static double Z(double v) => double.IsNaN(v) ? 0 : v;

    public static string FormatSize(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1000 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        string num = i == 0 ? bytes.ToString("0")
            : bytes < 10 ? bytes.ToString("0.00")
            : bytes < 100 ? bytes.ToString("0.0")
            : bytes.ToString("0");
        return $"{num} {units[i]}";
    }

    public void Dispose()
    {
        foreach (var d in _drives) { d.Read?.Dispose(); d.Write?.Dispose(); }
        foreach (var c in _diskIdle) c.Dispose();
        if (_cores != null) foreach (var c in _cores) c.Dispose();
        _hw?.Dispose();
    }
}
