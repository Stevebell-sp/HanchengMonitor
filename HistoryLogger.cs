using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GlassMonitor;

public sealed record HistoryRow(DateTime Time, double[] V);

/// <summary>每分鐘把平均值與最高值寫進當天的 CSV。</summary>
public sealed class HistoryLogger
{
    // CSV 欄位（time 之後）
    public static readonly string[] Columns =
    {
        "cpu_avg", "cpu_max", "cputemp_avg", "cputemp_max", "gpu_avg", "gpu_max",
        "gputemp_avg", "gputemp_max", "mem_avg", "mem_max", "netdown_avg", "netup_avg",
    };

    private static readonly string[] Ids = { "cpu", "cputemp", "gpu", "gputemp", "memused", "netdown", "netup" };

    private readonly AppSettings _s;
    private DateTime _minute;
    private readonly Dictionary<string, (double Sum, double Max, int N)> _acc = new();

    public static string Dir => Path.Combine(AppSettings.DataDir, "history");

    public HistoryLogger(AppSettings s)
    {
        _s = s;
        _minute = Trunc(DateTime.Now);
        Cleanup();
    }

    private static DateTime Trunc(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);

    public void Add(Dictionary<string, MetricState> states)
    {
        if (!_s.HistoryEnabled) return;
        var m = Trunc(DateTime.Now);
        if (m != _minute)
        {
            Flush();
            _minute = m;
        }
        foreach (var id in Ids)
        {
            if (!states.TryGetValue(id, out var st) || double.IsNaN(st.Value)) continue;
            var a = _acc.GetValueOrDefault(id, (0, double.MinValue, 0));
            _acc[id] = (a.Sum + st.Value, Math.Max(a.Max, st.Value), a.N + 1);
        }
    }

    private string Cell(string id, bool max)
    {
        if (!_acc.TryGetValue(id, out var a) || a.N == 0) return "";
        double v = max ? a.Max : a.Sum / a.N;
        return v.ToString("0.#", CultureInfo.InvariantCulture);
    }

    public void Flush()
    {
        if (_acc.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, $"{_minute:yyyy-MM-dd}.csv");
            var sb = new StringBuilder();
            if (!File.Exists(file)) sb.AppendLine("time," + string.Join(",", Columns));
            sb.Append(_minute.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(',');
            sb.AppendJoin(',', new[]
            {
                Cell("cpu", false), Cell("cpu", true), Cell("cputemp", false), Cell("cputemp", true),
                Cell("gpu", false), Cell("gpu", true), Cell("gputemp", false), Cell("gputemp", true),
                Cell("memused", false), Cell("memused", true), Cell("netdown", false), Cell("netup", false),
            });
            sb.AppendLine();
            File.AppendAllText(file, sb.ToString());
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        _acc.Clear();
    }

    private void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var limit = DateTime.Today.AddDays(-Math.Max(1, _s.HistoryDays));
            foreach (var f in Directory.GetFiles(Dir, "*.csv"))
            {
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day) && day < limit)
                    File.Delete(f);
            }
        }
        catch { }
    }

    public static List<HistoryRow> Load(DateTime day)
    {
        var rows = new List<HistoryRow>();
        string file = Path.Combine(Dir, $"{day:yyyy-MM-dd}.csv");
        if (!File.Exists(file)) return rows;
        try
        {
            foreach (var line in File.ReadAllLines(file).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (!DateTime.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                var v = new double[Columns.Length];
                for (int i = 0; i < v.Length; i++)
                    v[i] = i + 1 < parts.Length && double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN;
                rows.Add(new HistoryRow(day.Date + t.TimeOfDay, v));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return rows;
    }

    public static List<DateTime> AvailableDays()
    {
        try
        {
            if (!Directory.Exists(Dir)) return new();
            return Directory.GetFiles(Dir, "*.csv")
                .Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d) ? d : DateTime.MinValue)
                .Where(d => d != DateTime.MinValue)
                .OrderBy(d => d)
                .ToList();
        }
        catch
        {
            return new();
        }
    }
}
