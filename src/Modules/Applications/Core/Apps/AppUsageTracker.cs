using Seein.Core.Storage;

namespace Seein.Core.Apps;

/// <summary>
/// Keeps the live application state for the current sampling tick: open runtime intervals (one per
/// application, extended while heartbeats keep arriving), hourly accumulators and daily accumulators.
/// Pure in-memory bookkeeping so it can be unit tested without a database.
/// </summary>
public sealed class AppUsageTracker
{
    private sealed class OpenSpan
    {
        public long StartUtc;
        public long EndUtc;
        public double CpuSeconds;
        public double GpuUnits;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, OpenSpan> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, long Hour), (double Seconds, double Active, double Cpu, double Gpu, double Wh)> _hours = new();
    private readonly Dictionary<(string Key, string Date), (double Seconds, double Active, double Cpu, double Wh, double CpuWh, double GpuWh, double Gpu, double NetUp, double NetDown, double CpuPeak, double GpuPeak, double CpuMin, double GpuMin)> _days = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public void BeginTick()
    {
        lock (_gate) _seen.Clear();
    }

    /// <summary>Records one heartbeat for an application and adds its share to the hour/day buckets.</summary>
    /// <summary>keeps the smallest positive sample of the day.</summary>
    private static double AppendMin(double current, double sample)
    {
        if (sample <= 0) return current;
        return current <= 0 ? sample : Math.Min(current, sample);
    }

    public void Observe(string key, long nowUtc, string localDate, double dtSeconds, double cpuSeconds,
        double gpuUnits, double cpuWh, double gpuWh, int pulseSeconds, bool countsAsRuntime = true, bool isActive = false,
        double netUpBytes = 0, double netDownBytes = 0, double cpuPeak = 0, double gpuPeak = 0, double cpuMin = 0, double gpuMin = 0)
    {
        if (key.Length == 0) return;
        lock (_gate)
        {
            _seen.Add(key);

            if (_open.TryGetValue(key, out var span))
            {
                var (start, end) = HeartbeatMerger.Merge(span.StartUtc, span.EndUtc, nowUtc, pulseSeconds);
                if (start != span.StartUtc)
                {
                    span.StartUtc = start;
                    span.CpuSeconds = 0;
                    span.GpuUnits = 0;
                }
                span.EndUtc = end;
                span.CpuSeconds += cpuSeconds;
                span.GpuUnits += gpuUnits;
            }
            else
            {
                span = new OpenSpan { StartUtc = nowUtc, EndUtc = nowUtc, CpuSeconds = cpuSeconds, GpuUnits = gpuUnits };
                _open[key] = span;
            }

            var runtime = countsAsRuntime ? dtSeconds : 0;
            var active = isActive ? dtSeconds : 0;

            var hourUtc = nowUtc / 3600 * 3600;
            var hour = _hours.TryGetValue((key, hourUtc), out var h) ? h : (0, 0, 0, 0, 0);
            _hours[(key, hourUtc)] = (hour.Seconds + runtime, hour.Active + active,
                hour.Cpu + cpuSeconds, hour.Gpu + gpuUnits, hour.Wh + cpuWh + gpuWh);

            var day = _days.TryGetValue((key, localDate), out var d) ? d : (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            _days[(key, localDate)] = (day.Seconds + runtime, day.Active + active,
                day.Cpu + cpuSeconds, day.Wh + cpuWh + gpuWh, day.CpuWh + cpuWh, day.GpuWh + gpuWh,
                day.Gpu + gpuUnits, day.NetUp + netUpBytes, day.NetDown + netDownBytes,
                Math.Max(day.CpuPeak, cpuPeak), Math.Max(day.GpuPeak, gpuPeak),
                AppendMin(day.CpuMin, cpuMin), AppendMin(day.GpuMin, gpuMin));
        }
    }

    /// <summary>Drops the application from the live set once it is no longer visible in a tick.</summary>
    public void EndTick()
    {
        lock (_gate)
        {
            if (_open.Count == _seen.Count) return;
            foreach (var key in _open.Keys.Where(key => !_seen.Contains(key)).ToArray()) _open.Remove(key);
        }
    }

    public IReadOnlyList<AppEventRow> SnapshotSpans()
    {
        lock (_gate)
        {
            return _open.Select(pair => new AppEventRow(pair.Key, pair.Value.StartUtc, pair.Value.EndUtc,
                pair.Value.CpuSeconds, pair.Value.GpuUnits)).ToArray();
        }
    }

    public IReadOnlyList<AppHourRow> DrainHours()
    {
        lock (_gate)
        {
            var rows = _hours.Select(pair => new AppHourRow(pair.Key.Key, pair.Key.Hour,
                pair.Value.Seconds, pair.Value.Cpu, pair.Value.Gpu, pair.Value.Wh, pair.Value.Active)).ToArray();
            _hours.Clear();
            return rows;
        }
    }

    public IReadOnlyList<AppDayRow> DrainDays()
    {
        lock (_gate)
        {
            var rows = _days.Select(pair => new AppDayRow(pair.Key.Key, pair.Key.Date, pair.Value.Seconds,
                pair.Value.Cpu, pair.Value.Wh, pair.Value.CpuWh, pair.Value.GpuWh, pair.Value.Active,
                pair.Value.Gpu, (long)pair.Value.NetUp, (long)pair.Value.NetDown,
                pair.Value.CpuPeak, pair.Value.GpuPeak, pair.Value.CpuMin, pair.Value.GpuMin)).ToArray();
            _days.Clear();
            return rows;
        }
    }
}
