using Microsoft.Extensions.Logging.Abstractions;
using Seein.Core;
using Seein.Core.Thermal;
using Seein.Service;

namespace Seein.Tests;

public sealed class GpuFanServiceTests
{
    private sealed class Device : IAmdFanDevice
    {
        public AmdFanSnapshot Snapshot = new("RX 9070 XT", "test adapter", "1.5", false,
            new(25, 100, 1), new(15, 100, 1), [new(30, 25), new(70, 60), new(91, 100)], true);
        public bool RejectZero;
        public int Writes;
        public AmdFanSnapshot Read() => Snapshot;
        public (int?, double?) ReadMetrics() => (0, 41);
        public void ValidateCurve(IReadOnlyList<FanCurvePoint> points) { }
        public void SetCurve(IReadOnlyList<FanCurvePoint> points)
        {
            Writes++;
            Snapshot = Snapshot with { Points = points.ToArray() };
        }
        public void SetZeroRpm(bool enabled)
        {
            if (RejectZero) throw new InvalidOperationException("Zero RPM rejected");
            Writes++;
            Snapshot = Snapshot with { ZeroRpm = enabled };
        }
        public void Dispose() { }
    }

    private sealed class Harness(bool accepted = true)
    {
        public Device Device { get; } = new();
        public GpuFanService.Recovery? Journal { get; set; }
        public GpuFanService Service()
        {
            var service = new GpuFanService(NullLogger<GpuFanService>.Instance, new PanelState(), () => Device,
                _ => accepted, () => Journal, value => Journal = value);
            service.Probe();
            return service;
        }
    }

    [Fact]
    public async Task UnacceptedOrFactoryDriverAndStaleDraftStayReadOnly()
    {
        var harness = new Harness(false);
        using var service = harness.Service();
        var original = harness.Device.Snapshot;
        var desired = original.Points.Select(point => point with { Percent = Math.Min(100, point.Percent + 5) }).ToArray();
        Assert.NotNull(await service.ApplyAsync(new(desired, BaseCurve: original.Points, BaseZeroRpm: true)));
        Assert.Equal(0, harness.Device.Writes);
        Assert.Null(harness.Journal);

        var accepted = new Harness();
        accepted.Device.Snapshot = original with { Factory = true };
        using var acceptedService = accepted.Service();
        Assert.NotNull(await acceptedService.ApplyAsync(new(desired, BaseCurve: original.Points, BaseZeroRpm: true)));
        accepted.Device.Snapshot = original;
        Assert.NotNull(await acceptedService.ApplyAsync(new(desired, BaseCurve: desired, BaseZeroRpm: true)));
        Assert.Equal(0, accepted.Device.Writes);
    }

    [Fact]
    public async Task RestartRestoresFullOriginalCurveAndZeroRpm()
    {
        var harness = new Harness();
        var original = harness.Device.Snapshot;
        using var service = harness.Service();
        FanCurvePoint[] desired = [new(30, 26), new(70, 63), new(91, 100)];

        Assert.Null(await service.ApplyAsync(new(desired, false, original.Points, true)));
        Assert.Equal("seein", service.Status.Owner);
        Assert.Equal(false, harness.Device.Snapshot.ZeroRpm);
        Assert.NotNull(harness.Journal);

        using var restarted = harness.Service();
        Assert.Equal(original.Points, harness.Device.Snapshot.Points);
        Assert.Equal(original.ZeroRpm, harness.Device.Snapshot.ZeroRpm);
        Assert.Null(harness.Journal);
        Assert.Equal("driver", restarted.Status.Owner);
    }

    [Fact]
    public async Task PartialFailureRestoresAndExternalChangesAreNeverOverwritten()
    {
        var harness = new Harness();
        using var service = harness.Service();
        var original = harness.Device.Snapshot;
        FanCurvePoint[] desired = [new(30, 27), new(70, 64), new(91, 100)];
        harness.Device.RejectZero = true;

        Assert.NotNull(await service.ApplyAsync(new(desired, false, original.Points, true)));
        Assert.Equal(original.Points, harness.Device.Snapshot.Points);
        Assert.Null(harness.Journal);

        Assert.Null(await service.ApplyAsync(new(desired, BaseCurve: original.Points, BaseZeroRpm: true)));
        Assert.True(harness.Device.Snapshot.ZeroRpm);
        harness.Device.Snapshot = harness.Device.Snapshot with { Points = [new(30, 30), new(70, 70), new(91, 100)] };
        service.Probe();
        var writes = harness.Device.Writes;
        Assert.Equal("suspended", service.Status.Owner);
        Assert.NotNull(await service.RestoreAsync());
        Assert.Equal(writes, harness.Device.Writes);
        Assert.NotNull(harness.Journal);
    }
}
