using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace GlassMonitor;

public sealed record SensorRow(string Group, string Name, string Value);

public sealed class HwSnapshot
{
    public double CpuTemp = double.NaN, GpuTemp = double.NaN;
    public double CpuPower = double.NaN, GpuPower = double.NaN;
    public double CpuClock = double.NaN, GpuClock = double.NaN; // MHz
    public double FanMax = double.NaN, GpuFan = double.NaN;     // RPM
    public double GpuMemUsedMB = double.NaN, GpuMemTotalMB = double.NaN;
    public List<SensorRow>? All;
}

/// <summary>用 LibreHardwareMonitor 讀溫度、功耗、時脈、風扇、顯示記憶體。</summary>
public sealed class HardwareReader : IDisposable
{
    private readonly Computer _pc;
    private readonly IHardware? _cpuHw;
    private readonly IHardware? _gpuHw;
    private readonly List<IHardware> _boards;

    public bool HasGpu => _gpuHw != null;
    public bool HasFans { get; }
    public string CpuName => _cpuHw?.Name ?? "";
    public string GpuName => _gpuHw?.Name ?? "";

    public HardwareReader()
    {
        _pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
        _pc.Open();
        foreach (var hw in _pc.Hardware)
        {
            try { Update(hw); } catch { }
        }

        _cpuHw = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        _gpuHw = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
              ?? _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
              ?? _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
        _boards = _pc.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard).ToList();
        HasFans = _boards.SelectMany(AllSensors).Any(s => s.SensorType == SensorType.Fan && s.Value > 0);

        WriteLog();
    }

    private static void Update(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));

    /// <summary>依名稱關鍵字找感測器（找不到回傳 null）。</summary>
    private static ISensor? Find(IHardware? hw, SensorType type, params string[] keys)
    {
        if (hw == null) return null;
        var list = AllSensors(hw).Where(s => s.SensorType == type && s.Value.HasValue).ToList();
        foreach (var k in keys)
        {
            var s = list.FirstOrDefault(x => x.Name.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s;
        }
        return null;
    }

    private static ISensor? First(IHardware? hw, SensorType type) =>
        hw == null ? null : AllSensors(hw).FirstOrDefault(s => s.SensorType == type && s.Value.HasValue);

    private static double V(ISensor? s, double min = 0.0001) =>
        s?.Value is float f && f > min ? f : double.NaN;

    public HwSnapshot Read(bool includeAll)
    {
        try { if (_cpuHw != null) Update(_cpuHw); } catch { }
        try { if (_gpuHw != null) Update(_gpuHw); } catch { }
        foreach (var b in _boards)
        {
            try { Update(b); } catch { }
        }

        var r = new HwSnapshot();
        // 沒有驅動時 CPU 會回報 0 °C，所以門檻設 1
        r.CpuTemp = V(Find(_cpuHw, SensorType.Temperature, "Tctl", "Package", "Tdie", "Core (Average)", "Core Max")
                      ?? First(_cpuHw, SensorType.Temperature), 1);
        r.CpuPower = V(Find(_cpuHw, SensorType.Power, "Package", "CPU Cores"));
        if (_cpuHw != null)
        {
            var clocks = AllSensors(_cpuHw)
                .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #") && s.Value > 0)
                .Select(s => (double)s.Value!.Value)
                .ToList();
            if (clocks.Count > 0) r.CpuClock = clocks.Average();
        }

        r.GpuTemp = V(Find(_gpuHw, SensorType.Temperature, "GPU Core") ?? First(_gpuHw, SensorType.Temperature), 1);
        r.GpuPower = V(Find(_gpuHw, SensorType.Power, "GPU Package", "GPU Power", "Board") ?? First(_gpuHw, SensorType.Power));
        r.GpuClock = V(Find(_gpuHw, SensorType.Clock, "GPU Core"));
        r.GpuFan = V(First(_gpuHw, SensorType.Fan));
        r.GpuMemUsedMB = V(Find(_gpuHw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"));
        r.GpuMemTotalMB = V(Find(_gpuHw, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"));

        var fans = _boards.SelectMany(AllSensors)
            .Where(s => s.SensorType == SensorType.Fan && s.Value > 0)
            .Select(s => (double)s.Value!.Value)
            .ToList();
        if (fans.Count > 0) r.FanMax = fans.Max();

        if (includeAll) r.All = BuildAll();
        return r;
    }

    private static string Format(ISensor s)
    {
        float v = s.Value ?? 0;
        return s.SensorType switch
        {
            SensorType.Temperature => $"{v:0.0} °C",
            SensorType.Fan => $"{v:0} RPM",
            SensorType.Power => $"{v:0.0} W",
            SensorType.Control => $"{v:0} %",
            SensorType.Clock => $"{v:0} MHz",
            SensorType.Load => $"{v:0} %",
            SensorType.SmallData => $"{v:0} MB",
            SensorType.Voltage => $"{v:0.000} V",
            _ => $"{v:0.##}",
        };
    }

    private List<SensorRow> BuildAll()
    {
        var types = new[] { SensorType.Temperature, SensorType.Fan, SensorType.Control, SensorType.Power };
        var rows = new List<SensorRow>();
        var hws = new List<IHardware>();
        if (_cpuHw != null) hws.Add(_cpuHw);
        if (_gpuHw != null) hws.Add(_gpuHw);
        hws.AddRange(_boards);
        foreach (var hw in hws)
        {
            foreach (var h in new[] { hw }.Concat(hw.SubHardware))
            {
                foreach (var s in h.Sensors.Where(s => types.Contains(s.SensorType) && s.Value.HasValue))
                {
                    if (s.SensorType == SensorType.Fan && s.Value <= 0) continue;
                    if (s.SensorType == SensorType.Temperature && s.Value <= 0) continue;
                    rows.Add(new SensorRow(h.Name, s.Name, Format(s)));
                }
            }
        }
        return rows;
    }

    private void WriteLog()
    {
        try
        {
            var log = new StringBuilder();
            foreach (var hw in _pc.Hardware)
            {
                foreach (var h in new[] { hw }.Concat(hw.SubHardware))
                {
                    log.AppendLine($"[{h.HardwareType}] {h.Name}");
                    foreach (var s in h.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan
                                 or SensorType.Power or SensorType.Clock or SensorType.SmallData))
                        log.AppendLine($"    {s.SensorType,-12} {s.Name} = {(s.Value.HasValue ? Format(s) : "（讀不到）")}");
                }
            }
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(Path.Combine(AppSettings.DataDir, "sensors.log"), log.ToString(), new UTF8Encoding(true));
        }
        catch { }
    }

    public void Dispose()
    {
        try { _pc.Close(); } catch { }
    }
}
