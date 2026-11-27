using Seein.Core.Power;

namespace Seein.Tests;

public sealed class PowerIntegratorTests
{
    [Fact]
    public void IntegratesUsingTrapezoids()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var integrator = new PowerIntegrator();
        Assert.Equal(0, integrator.Add(start, 100));
        var delta = integrator.Add(start.AddSeconds(1), 200);
        Assert.Equal(150d / 3600d, delta, 9);
    }

    [Fact]
    public void DoesNotBackfillSleepGap()
    {
        var start = DateTimeOffset.UtcNow;
        var integrator = new PowerIntegrator();
        integrator.Add(start, 500);
        Assert.Equal(0, integrator.Add(start.AddSeconds(11), 500));
        Assert.Equal(0, integrator.SessionWh);
    }

    [Fact]
    public async Task EstimatedSourceLabelsItselfAndUsesEfficiency()
    {
        var source = new EstimatedPowerSource { BaselineWatts = 55, Efficiency = .9 };
        var sensors = new Seein.Core.SensorSnapshot(DateTimeOffset.Now, new Dictionary<string, double?>
        {
            [Seein.Core.SensorKeys.CpuPower] = 80,
            [Seein.Core.SensorKeys.GpuPower] = 180
        });
        var value = await source.ReadWattsAsync(sensors, CancellationToken.None);
        Assert.True(source.IsEstimated);
        Assert.Equal(350, value!.Value, 6);
        Assert.Contains("估算", source.Name);
    }

    [Fact]
    public void BreakdownComponentsSumToEstimatedWholeSystemEnergy()
    {
        var timestamp = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero);
        var sensors = new Seein.Core.SensorSnapshot(timestamp, new Dictionary<string, double?>
        {
            [Seein.Core.SensorKeys.CpuPower] = 80,
            [Seein.Core.SensorKeys.GpuPower] = 180
        });
        var breakdown = EstimatedPowerSource.Estimate(sensors, 55, .9);
        var integrator = new EnergyBreakdownIntegrator();
        integrator.Add(timestamp, breakdown);
        var delta = integrator.Add(timestamp.AddSeconds(2), breakdown);

        Assert.Equal(350, breakdown.TotalWatts, 6);
        Assert.Equal(350d * 2 / 3600, delta.TotalWh, 9);
        Assert.Equal(delta.TotalWh, delta.CpuWh!.Value + delta.GpuWh!.Value + delta.OtherWh + delta.LossWh, 9);
        Assert.Equal(2, delta.CpuCoveredSeconds);
        Assert.Equal(2, delta.GpuCoveredSeconds);
    }

    [Fact]
    public void MissingSensorRemainsUnavailableAndLongGapDoesNotIntegrate()
    {
        var timestamp = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero);
        var missingCpu = new Seein.Core.SensorSnapshot(timestamp, new Dictionary<string, double?>
        {
            [Seein.Core.SensorKeys.GpuPower] = 100
        });
        var breakdown = EstimatedPowerSource.Estimate(missingCpu, 50, .9);
        Assert.Null(breakdown.CpuWatts);

        var integrator = new EnergyBreakdownIntegrator();
        integrator.Add(timestamp, breakdown);
        var gap = integrator.Add(timestamp.AddSeconds(11), breakdown);
        Assert.Equal(0, gap.TotalWh);
        Assert.Null(gap.CpuWh);

        var valid = integrator.Add(timestamp.AddSeconds(12), breakdown);
        Assert.True(valid.TotalWh > 0);
        Assert.Null(valid.CpuWh);
        Assert.Equal(valid.TotalWh, (valid.GpuWh ?? 0) + valid.OtherWh + valid.LossWh, 9);
    }
}

