namespace Seein.Core.Thermal;

/// <summary>
/// Pure curve maths: interpolation, clamping, hysteresis and step limiting. Everything here is
/// deterministic so the control loop can be unit tested without touching hardware.
/// </summary>
public static class FanCurveMath
{
    /// <summary>Linear interpolation over the sorted points; null when the curve has no usable point.</summary>
    public static double? Evaluate(IReadOnlyList<FanCurvePoint>? points, double temperatureC)
    {
        if (points is null || points.Count == 0 || double.IsNaN(temperatureC)) return null;
        var sorted = points.Where(point => double.IsFinite(point.TemperatureC) && double.IsFinite(point.Percent))
            .OrderBy(point => point.TemperatureC)
            .ToArray();
        if (sorted.Length == 0) return null;
        if (sorted.Length == 1) return ClampPercent(sorted[0].Percent);
        if (temperatureC <= sorted[0].TemperatureC) return ClampPercent(sorted[0].Percent);
        if (temperatureC >= sorted[^1].TemperatureC) return ClampPercent(sorted[^1].Percent);

        for (var index = 1; index < sorted.Length; index++)
        {
            var right = sorted[index];
            if (temperatureC > right.TemperatureC) continue;
            var left = sorted[index - 1];
            var span = right.TemperatureC - left.TemperatureC;
            if (span <= 0) return ClampPercent(right.Percent);
            var ratio = (temperatureC - left.TemperatureC) / span;
            return ClampPercent(left.Percent + (right.Percent - left.Percent) * ratio);
        }
        return ClampPercent(sorted[^1].Percent);
    }

    public static double ClampPercent(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;

    /// <summary>Clamps to the channel's safe window (pump floor, ceiling).</summary>
    public static double ClampToChannel(double percent, double floorPercent, double ceilingPercent)
    {
        var floor = ClampPercent(floorPercent);
        var ceiling = ClampPercent(ceilingPercent);
        if (ceiling < floor) ceiling = floor;
        return Math.Clamp(ClampPercent(percent), floor, ceiling);
    }

    /// <summary>
    /// Hysteresis: a rising temperature applies immediately, a falling one only once it drops below
    /// the last decision point minus the hysteresis band. Keeps fans from oscillating at a boundary.
    /// </summary>
    public static double ApplyHysteresis(double previousTarget, double nextTarget, double hysteresisC,
        double previousTemperature, double temperature, double currentPercent, double floorPercent)
    {
        if (!double.IsFinite(previousTarget)) return nextTarget;
        if (temperature >= previousTemperature) return nextTarget;
        if (nextTarget >= previousTarget) return nextTarget;
        // Cooling down: hold the higher value until the temperature leaves the band.
        return previousTemperature - temperature >= Math.Max(0, hysteresisC) ? nextTarget : Math.Max(currentPercent, previousTarget);
    }

    /// <summary>Limits one write to <paramref name="maxStep"/> percent so the fan ramps instead of jumping.</summary>
    public static double LimitStep(double currentPercent, double targetPercent, double maxStep)
    {
        if (!double.IsFinite(currentPercent)) return targetPercent;
        var step = Math.Abs(maxStep) <= 0 ? 100 : Math.Abs(maxStep);
        var delta = targetPercent - currentPercent;
        if (Math.Abs(delta) <= step) return targetPercent;
        return currentPercent + Math.Sign(delta) * step;
    }

    /// <summary>True when the write is worth sending: the value moved enough and the interval elapsed.</summary>
    public static bool ShouldWrite(double? lastWritten, DateTimeOffset? lastWriteAt, double target,
        DateTimeOffset now, TimeSpan minimumInterval, double minimumDelta)
    {
        if (lastWritten is null || lastWriteAt is null) return true;
        if (now - lastWriteAt.Value < minimumInterval) return false;
        return Math.Abs(target - lastWritten.Value) >= minimumDelta;
    }
}
