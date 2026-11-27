using Seein.Core.Sensors;

namespace Seein.Core.Thermal;

public static class FanPolicy
{
    public static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(8);

    public static bool IsFresh(SensorSnapshot snapshot, DateTimeOffset now) =>
        snapshot.Timestamp <= now && now - snapshot.Timestamp <= MaximumSampleAge;

    public static double? Temperature(SensorSnapshot snapshot, string key) =>
        snapshot.Get(key) is double value && double.IsFinite(value) && value is > 0 and <= 130 ? value : null;

    public static (double? Primary, double? Secondary) ReadTemperatures(FanTemperatureSource source, SensorSnapshot snapshot)
    {
        var cpu = Temperature(snapshot, SensorKeys.CpuTemperature);
        var gpu = Temperature(snapshot, SensorKeys.GpuTemperature);
        var board = Temperature(snapshot, SensorKeys.MotherboardTemp);
        return source switch
        {
            FanTemperatureSource.CpuPackage => (cpu, null),
            FanTemperatureSource.GpuCore => (gpu, null),
            FanTemperatureSource.Motherboard => (board, null),
            FanTemperatureSource.CpuAndGpu => (cpu, gpu),
            FanTemperatureSource.Maximum => (new[] { cpu, gpu, board }.Max(), null),
            _ => (null, null)
        };
    }

    public static double? Evaluate(FanChannelSettings channel, FanTemperatureSource source,
        double? primary, double? secondary, double floor, double ceiling)
    {
        if (channel.Mode == FanChannelMode.Auto || primary is not double temperature ||
            (source == FanTemperatureSource.CpuAndGpu && secondary is null))
            return null;

        if (channel.Mode == FanChannelMode.Manual)
            return FanCurveMath.ClampToChannel(channel.ManualPercent, floor, ceiling);

        var target = FanCurveMath.Evaluate(channel.Curve?.Points, temperature);
        if (source == FanTemperatureSource.CpuAndGpu)
        {
            var gpuDuty = FanCurveMath.Evaluate(channel.GpuCurve?.Points, secondary!.Value);
            if (target is null || gpuDuty is null) return null;
            target = Math.Max(target.Value, gpuDuty.Value);
        }
        return target is double percent ? FanCurveMath.ClampToChannel(percent, floor, ceiling) : null;
    }
}
