namespace Seein.Core.Thermal;

public sealed record GpuFanRange(int Minimum, int Maximum, int Step)
{
    public bool Contains(double value) => Step > 0 && double.IsFinite(value) &&
        value >= Minimum && value <= Maximum && (value - Minimum) % Step == 0;
}

public sealed record GpuFanStatus(string Name, string Owner, string Message,
    GpuFanRange? TemperatureRange = null, GpuFanRange? SpeedRange = null,
    IReadOnlyList<FanCurvePoint>? Curve = null, bool? ZeroRpm = null,
    int? Rpm = null, double? TemperatureC = null, bool CanWrite = false,
    IReadOnlyList<FanTrendSample>? Trend = null);

public sealed record GpuFanCommand(IReadOnlyList<FanCurvePoint> Points, bool? ZeroRpm = null,
    IReadOnlyList<FanCurvePoint>? BaseCurve = null, bool? BaseZeroRpm = null);
