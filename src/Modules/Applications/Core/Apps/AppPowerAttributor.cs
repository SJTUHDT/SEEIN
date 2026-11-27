namespace Seein.Core.Apps;

/// <summary>
/// Splits the measured CPU package power and GPU board power over the applications in proportion to
/// their CPU time and GPU engine utilization.  The 74 W platform baseline and the PSU loss cannot be
/// attributed to a single process, so they stay in their own bucket.
/// </summary>
public static class AppPowerAttributor
{
    /// <summary>
    /// Energy used by one application during <paramref name="seconds"/>: the package power times the
    /// application's share of the CPU time, integrated over the sample window.
    /// </summary>
    public static double CpuWh(double cpuPackageWatts, double appCpuSeconds, double totalCpuSeconds, double seconds)
    {
        if (!double.IsFinite(cpuPackageWatts) || cpuPackageWatts <= 0) return 0;
        if (!double.IsFinite(appCpuSeconds) || appCpuSeconds <= 0) return 0;
        if (!double.IsFinite(totalCpuSeconds) || totalCpuSeconds <= 0) return 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return 0;
        return cpuPackageWatts * Math.Min(1, appCpuSeconds / totalCpuSeconds) * seconds / 3600d;
    }

    public static double GpuWh(double gpuBoardWatts, double appGpuUnits, double totalGpuUnits, double seconds)
    {
        if (!double.IsFinite(gpuBoardWatts) || gpuBoardWatts <= 0) return 0;
        if (!double.IsFinite(appGpuUnits) || appGpuUnits <= 0) return 0;
        if (!double.IsFinite(totalGpuUnits) || totalGpuUnits <= 0) return 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return 0;
        return gpuBoardWatts * Math.Min(1, appGpuUnits / totalGpuUnits) * seconds / 3600d;
    }

    /// <summary>Everything outside the measured CPU/GPU rails: platform baseline, PSU loss, unattributed.</summary>
    public static double BaselineWh(double estimatedSystemWatts, double cpuPackageWatts, double gpuBoardWatts, double seconds)
    {
        if (!double.IsFinite(estimatedSystemWatts) || estimatedSystemWatts <= 0) return 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return 0;
        var cpuWatts = double.IsFinite(cpuPackageWatts) ? Math.Max(0, cpuPackageWatts) : 0;
        var gpuWatts = double.IsFinite(gpuBoardWatts) ? Math.Max(0, gpuBoardWatts) : 0;
        var remainder = estimatedSystemWatts - cpuWatts - gpuWatts;
        return Math.Max(0, remainder) * seconds / 3600d;
    }
}

/// <summary>
/// Heartbeat merge rule borrowed from ActivityWatch: a heartbeat that arrives within <c>pulse</c>
/// seconds of the previous one extends the open interval, otherwise it starts a new interval.
/// </summary>
public static class HeartbeatMerger
{
    public static bool Extends(long spanEndUtc, long heartbeatUtc, int pulseSeconds) =>
        heartbeatUtc >= spanEndUtc && heartbeatUtc - spanEndUtc <= pulseSeconds;

    public static (long StartUtc, long EndUtc) Merge(long spanStartUtc, long spanEndUtc, long heartbeatUtc, int pulseSeconds)
    {
        if (!Extends(spanEndUtc, heartbeatUtc, pulseSeconds)) return (heartbeatUtc, heartbeatUtc);
        return (spanStartUtc, Math.Max(spanEndUtc, heartbeatUtc));
    }
}
