namespace Seein.Core.Power;

public interface IPowerSource
{
    string Name { get; }
    bool IsEstimated { get; }
    ValueTask<double?> ReadWattsAsync(SensorSnapshot sensors, CancellationToken cancellationToken);
}

public sealed class EstimatedPowerSource : IPowerSource
{
    public string Name => "估算整机功耗";
    public bool IsEstimated => true;
    public double BaselineWatts { get; set; } = 55;
    public double Efficiency { get; set; } = 0.90;

    public ValueTask<double?> ReadWattsAsync(SensorSnapshot sensors, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<double?>(Estimate(sensors, BaselineWatts, Efficiency).TotalWatts);
    }

    public static EstimatedPowerBreakdown Estimate(SensorSnapshot sensors, double baselineWatts, double efficiency)
    {
        static double? Read(SensorSnapshot snapshot, string key)
        {
            var value = snapshot.Get(key);
            return value is double watts && double.IsFinite(watts) ? Math.Max(0, watts) : null;
        }

        var cpu = Read(sensors, SensorKeys.CpuPower);
        var gpu = Read(sensors, SensorKeys.GpuPower);
        var other = double.IsFinite(baselineWatts) ? Math.Max(0, baselineWatts) : 0;
        var safeEfficiency = Math.Clamp(double.IsFinite(efficiency) ? efficiency : .9, .5, 1.0);
        var total = ((cpu ?? 0) + (gpu ?? 0) + other) / safeEfficiency;
        var loss = Math.Max(0, total - (cpu ?? 0) - (gpu ?? 0) - other);
        return new EstimatedPowerBreakdown(cpu, gpu, other, loss, total);
    }
}

public sealed record EstimatedPowerBreakdown(double? CpuWatts, double? GpuWatts, double OtherWatts,
    double LossWatts, double TotalWatts);

public sealed class EnergyBreakdownIntegrator(double initialSessionWh = 0)
{
    private DateTimeOffset? _lastTimestamp;
    private EstimatedPowerBreakdown? _last;
    public double SessionWh { get; private set; } = Math.Max(0, initialSessionWh);

    public EnergyDelta Add(DateTimeOffset timestamp, EstimatedPowerBreakdown current)
    {
        var empty = new EnergyDelta(0, null, null, 0, 0, 0, 0, 0, 0, 0);
        if (_lastTimestamp is null || _last is null)
        {
            _lastTimestamp = timestamp;
            _last = current;
            return empty;
        }

        var seconds = (timestamp - _lastTimestamp.Value).TotalSeconds;
        var previous = _last;
        _lastTimestamp = timestamp;
        _last = current;
        if (seconds is <= 0 or > 10) return empty;

        static double Integrate(double first, double second, double duration) =>
            (Math.Max(0, first) + Math.Max(0, second)) * .5 * duration / 3600d;

        var total = Integrate(previous.TotalWatts, current.TotalWatts, seconds);
        double? cpu = previous.CpuWatts is double cpu0 && current.CpuWatts is double cpu1
            ? Integrate(cpu0, cpu1, seconds) : null;
        double? gpu = previous.GpuWatts is double gpu0 && current.GpuWatts is double gpu1
            ? Integrate(gpu0, gpu1, seconds) : null;
        var other = Integrate(previous.OtherWatts, current.OtherWatts, seconds);
        var loss = Integrate(previous.LossWatts, current.LossWatts, seconds);
        SessionWh += total;
        return new EnergyDelta(total, cpu, gpu, other, loss, 0, seconds,
            cpu.HasValue ? seconds : 0, gpu.HasValue ? seconds : 0,
            cpu.HasValue && gpu.HasValue ? seconds : 0);
    }

    public void ResetContinuity()
    {
        _lastTimestamp = null;
        _last = null;
    }
}

public sealed class PowerSourceSelector(IEnumerable<IPowerSource> sources)
{
    private readonly IReadOnlyList<IPowerSource> _sources = sources.OrderBy(s => s.IsEstimated).ToArray();

    public async ValueTask<(IPowerSource Source, double Watts)?> ReadAsync(SensorSnapshot snapshot, CancellationToken token)
    {
        foreach (var source in _sources)
        {
            var watts = await source.ReadWattsAsync(snapshot, token).ConfigureAwait(false);
            if (watts is >= 0 and < 10000) return (source, watts.Value);
        }
        return null;
    }
}

public sealed class PowerIntegrator
{
    private DateTimeOffset? _lastTimestamp;
    private double? _lastWatts;
    public double SessionWh { get; private set; }

    public PowerIntegrator(double initialSessionWh = 0) => SessionWh = Math.Max(0, initialSessionWh);

    public double Add(DateTimeOffset timestamp, double watts)
    {
        watts = Math.Max(0, watts);
        double deltaWh = 0;
        if (_lastTimestamp is not null && _lastWatts is not null)
        {
            var seconds = (timestamp - _lastTimestamp.Value).TotalSeconds;
            if (seconds is > 0 and <= 10)
            {
                deltaWh = ((_lastWatts.Value + watts) / 2.0) * seconds / 3600.0;
                SessionWh += deltaWh;
            }
        }
        _lastTimestamp = timestamp;
        _lastWatts = watts;
        return deltaWh;
    }

    public void ResetContinuity()
    {
        _lastTimestamp = null;
        _lastWatts = null;
    }
}

