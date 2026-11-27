using Seein.Core;
using Seein.Core.Thermal;
using Seein.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seein.Tests;

public sealed class FanControlServiceTests
{
    private sealed class FakeFanDevice : IFanControlDevice
    {
        public List<(string Id, double Percent)> Writes { get; } = [];
        public List<string> Restores { get; } = [];
        public bool RestoreAllCalled { get; private set; }
        public bool RejectRestore { get; set; }

        public IReadOnlyList<FanChannelDescriptor> DescribeFanChannels() =>
        [
            new("cpu-fan", "CPU Fan", "冷排风扇", true, 25, 100),
            new("pump-fan", "Pump Fan #1", "水冷泵", true, 55, 100),
            new("system-fan-4", "System Fan #4", "机箱风扇（前/后）", true, 0, 100)
        ];

        public bool TrySetFanPwm(string channelId, double percent, out string? error)
        {
            error = null;
            Writes.Add((channelId, percent));
            return true;
        }

        public bool TryRestoreFan(string channelId, out string? error)
        {
            error = null;
            Restores.Add(channelId);
            if (!RejectRestore) return true;
            error = "test controller failure";
            return false;
        }

        public void RestoreAllFans() => RestoreAllCalled = true;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SensorSnapshot SnapshotWithTemperature(double cpu, DateTimeOffset? at = null,
        double? gpu = 40, double? pwm = 50, double? rpm = 1200)
    {
        return new SensorSnapshot(at ?? DateTimeOffset.UtcNow, new Dictionary<string, double?>
        {
            [SensorKeys.CpuTemperature] = cpu,
            [SensorKeys.GpuTemperature] = gpu,
            [SensorKeys.MotherboardTemp] = 35,
            ["fan.cpu-fan.pwm"] = pwm,
            ["fan.cpu-fan.rpm"] = rpm
        });
    }

    private static FanSettings BalancedWithManual(double percent)
    {
        return FanDefaults.DefaultSettings() with
        {
            Enabled = true,
            ActiveProfileId = FanDefaults.BalancedId,
            Profiles =
            [
                new FanProfile(FanDefaults.BalancedId, "均衡", FanTemperatureSource.CpuPackage,
                [
                    new FanChannelSettings("cpu-fan", FanChannelMode.Manual, percent, 25, 100),
                    new FanChannelSettings("pump-fan", FanChannelMode.Manual, percent, 55, 100),
                    new FanChannelSettings("system-fan-4", FanChannelMode.Manual, percent, 0, 100)
                ])
            ]
        };
    }

    [Fact]
    public void ManualModeRespectsChannelFloors()
    {
        var state = new PanelState();
        state.Publish(SnapshotWithTemperature(50), null);
        var device = new FakeFanDevice();
        var service = new FanControlService(NullLogger<FanControlService>.Instance, state, msiProbe: () => false);
        service.Attach(device);
        service.Initialize(BalancedWithManual(10));

        service.Tick();

        Assert.Contains(device.Writes, write => write.Id == "cpu-fan" && write.Percent >= 25);
        Assert.Contains(device.Writes, write => write.Id == "pump-fan" && write.Percent >= 55);
    }

    [Fact]
    public void InitialPwmVerificationAllowsConvergingHardwareRamp()
    {
        var clock = new Clock();
        var state = new PanelState();
        var device = new FakeFanDevice();
        using var service = new FanControlService(NullLogger<FanControlService>.Instance, state, clock, () => false);
        service.Attach(device);
        service.Initialize(BalancedWithManual(75));
        foreach (var pwm in new double[] { 49, 49, 51, 55, 59, 63, 67 })
        {
            state.Publish(SnapshotWithTemperature(55, clock.Now, pwm: pwm), null);
            service.Tick();
            clock.Now += TimeSpan.FromSeconds(2);
        }
        Assert.DoesNotContain("cpu-fan", device.Restores);
        Assert.Equal("seein", service.GetStatus().Channels.Single(channel => channel.Id == "cpu-fan").Owner);
    }

    [Fact]
    public void DisablingRestoresEveryChannel()
    {
        var state = new PanelState();
        state.Publish(SnapshotWithTemperature(50), null);
        var device = new FakeFanDevice();
        var service = new FanControlService(NullLogger<FanControlService>.Instance, state, msiProbe: () => false);
        service.Attach(device);
        service.Initialize(BalancedWithManual(50));
        service.Tick();

        service.ApplySettings(service.Settings with { Enabled = false });

        Assert.False(device.RestoreAllCalled);
        Assert.Equal(3, device.Restores.Count);
    }

    [Fact]
    public void CurveModeWritesAValueInsideTheSafeWindow()
    {
        var state = new PanelState();
        state.Publish(SnapshotWithTemperature(70), null);
        var device = new FakeFanDevice();
        var service = new FanControlService(NullLogger<FanControlService>.Instance, state, msiProbe: () => false);
        service.Attach(device);
        var settings = FanDefaults.DefaultSettings() with { Enabled = true };
        service.Initialize(settings);

        service.Tick();

        var pump = device.Writes.FirstOrDefault(write => write.Id == "pump-fan");
        Assert.True(pump.Percent >= 55);
        var radiator = device.Writes.FirstOrDefault(write => write.Id == "cpu-fan");
        Assert.True(radiator.Percent >= 25 && radiator.Percent <= 100);
    }

    [Fact]
    public void TemperatureResolutionReturnsUnavailableWhenSensorsAreMissing()
    {
        var empty = SensorSnapshot.Empty;
        Assert.Null(FanControlService.ResolveTemperature(FanTemperatureSource.CpuPackage, empty));
        Assert.Null(FanControlService.ResolveTemperature(FanTemperatureSource.Maximum, empty));
    }

    [Theory]
    [InlineData(FanTemperatureSource.CpuPackage)]
    [InlineData(FanTemperatureSource.CpuAndGpu)]
    public void ReapplyingALowerCurveAtStableTemperatureRampsAllTheWayDown(FanTemperatureSource source)
    {
        var clock = new Clock();
        var state = new PanelState();
        var device = new FakeFanDevice();
        using var service = new FanControlService(NullLogger<FanControlService>.Instance, state, clock, () => false);
        service.Attach(device);
        FanSettings Settings(double duty) => new(true, "test", [new("test", "测试", source,
            [new("cpu-fan", FanChannelMode.Curve, Curve: new([new(30, duty), new(80, duty)]),
                GpuCurve: new([new(30, duty), new(80, duty)]))])]);
        service.Initialize(Settings(85));
        state.Publish(SnapshotWithTemperature(60, clock.Now, pwm: 85), null);
        service.Tick();
        service.ApplySettings(Settings(35));
        for (var tick = 0; tick < 12; tick++)
        {
            clock.Now += TimeSpan.FromSeconds(2);
            state.Publish(SnapshotWithTemperature(60, clock.Now, pwm: device.Writes.Last().Percent), null);
            service.Tick();
        }
        var writes = device.Writes.Where(write => write.Id == "cpu-fan").Select(write => write.Percent).ToArray();
        Assert.Equal(35, writes[^1]);
        Assert.All(writes.Zip(writes.Skip(1)), pair => Assert.InRange(pair.First - pair.Second, 0, 8));
        Assert.DoesNotContain("cpu-fan", device.Restores);
    }

    [Fact]
    public void CoolingPastHysteresisContinuesRampingWithoutAnotherTemperatureDrop()
    {
        var clock = new Clock();
        var state = new PanelState();
        var device = new FakeFanDevice();
        using var service = new FanControlService(NullLogger<FanControlService>.Instance, state, clock, () => false);
        service.Attach(device);
        service.Initialize(new(true, "test", [new("test", "测试", FanTemperatureSource.CpuPackage,
            [new("cpu-fan", FanChannelMode.Curve, Curve: new([new(50, 35), new(70, 85)]))])]));
        state.Publish(SnapshotWithTemperature(70, clock.Now, pwm: 85), null);
        service.Tick();
        clock.Now += TimeSpan.FromSeconds(2);
        state.Publish(SnapshotWithTemperature(68, clock.Now, pwm: 85), null);
        service.Tick();
        Assert.Single(device.Writes); // Still inside the 3°C band.
        for (var tick = 0; tick < 12; tick++)
        {
            clock.Now += TimeSpan.FromSeconds(2);
            state.Publish(SnapshotWithTemperature(50, clock.Now, pwm: device.Writes.Last().Percent), null);
            service.Tick();
        }
        Assert.Equal(35, device.Writes.Last().Percent);
        Assert.DoesNotContain("cpu-fan", device.Restores);
    }

    [Fact]
    public void DefaultProfilesKeepPumpAboveItsFloor()
    {
        foreach (var profile in FanDefaults.DefaultProfiles())
        {
            var pump = profile.Channels.Single(channel => channel.Id == "pump-fan");
            Assert.True(pump.FloorPercent >= 55);
            Assert.Equal(FanChannelMode.Manual, pump.Mode);
            Assert.True(pump.ManualPercent >= 55);
        }
    }

    [Fact]
    public void NormalizingStoredSettingsFillsMissingCurves()
    {
        var stored = new FanSettings(true, "custom", [new FanProfile("custom", "自定义", FanTemperatureSource.GpuCore,
            [new FanChannelSettings("cpu-fan")])]);
        var normalized = FanDefaults.Normalize(stored);
        var channel = normalized.Profiles!.Single().Channels.Single();
        Assert.Equal(25, channel.FloorPercent);
        Assert.Equal(FanTemperatureSource.GpuCore, channel.Source);
        Assert.NotNull(channel.Curve);
        Assert.NotEmpty(channel.Curve!.Points);
    }
    [Fact]
    public void MixedPolicyTakesTheGreaterDutyAndNeedsBothSensors()
    {
        var setting = new FanChannelSettings("system-fan-4", FanChannelMode.Curve,
            Curve: new([new(40, 35), new(70, 50)]), Source: FanTemperatureSource.CpuAndGpu,
            GpuCurve: new([new(40, 45), new(55, 90)]));
        Assert.Equal(90, FanPolicy.Evaluate(setting, FanTemperatureSource.CpuAndGpu, 65, 55, 0, 100));
        Assert.Null(FanPolicy.Evaluate(setting, FanTemperatureSource.CpuAndGpu, 65, null, 0, 100));
    }

    [Fact]
    public void OnlyDistinctReadbacksCountAndMismatchRestoresBeforeAnotherWrite()
    {
        var clock = new Clock();
        var state = new PanelState();
        var device = new FakeFanDevice();
        var service = new FanControlService(NullLogger<FanControlService>.Instance, state, clock, () => false);
        service.Attach(device);
        service.Initialize(BalancedWithManual(50));
        state.Publish(SnapshotWithTemperature(50, clock.Now), null);
        service.Tick();

        clock.Now = clock.Now.AddSeconds(2);
        state.Publish(SnapshotWithTemperature(50, clock.Now, pwm: 20), null);
        service.Tick();
        clock.Now = clock.Now.AddSeconds(2);
        service.Tick();
        Assert.DoesNotContain("cpu-fan", device.Restores);
        Assert.Equal("verifying", service.GetStatus().Channels.First().Owner);

        state.Publish(SnapshotWithTemperature(50, clock.Now, pwm: 20), null);
        service.Tick();
        Assert.Contains("cpu-fan", device.Restores);
        Assert.Single(device.Writes, write => write.Id == "cpu-fan");
    }

    [Fact]
    public void HotCpuBypassesPendingReadbackAndStaleSamplesHandBackControl()
    {
        var clock = new Clock();
        var state = new PanelState();
        var device = new FakeFanDevice();
        var service = new FanControlService(NullLogger<FanControlService>.Instance, state, clock, () => false);
        service.Attach(device);
        service.Initialize(BalancedWithManual(50));
        state.Publish(SnapshotWithTemperature(50, clock.Now), null);
        service.Tick();

        clock.Now = clock.Now.AddSeconds(1);
        state.Publish(SnapshotWithTemperature(92, clock.Now), null);
        service.Tick();
        Assert.Contains(("cpu-fan", 100d), device.Writes);

        clock.Now = clock.Now.AddSeconds(9);
        service.Tick();
        Assert.Contains("cpu-fan", device.Restores);
        Assert.Null(service.GetStatus().Channels.First().TemperatureC);
    }

    [Fact]
    public void ContentionLatchesAndRestoreFailureIsReportedUntilRetried()
    {
        var clock = new Clock();
        var state = new PanelState();
        var device = new FakeFanDevice();
        var msi = false;
        var service = new FanControlService(NullLogger<FanControlService>.Instance, state, clock, () => msi);
        service.Attach(device);
        service.Initialize(BalancedWithManual(50));
        state.Publish(SnapshotWithTemperature(50, clock.Now), null);
        service.Tick();

        device.RejectRestore = true;
        msi = true;
        clock.Now = clock.Now.AddSeconds(2);
        state.Publish(SnapshotWithTemperature(50, clock.Now), null);
        service.Tick();
        Assert.NotNull(service.RestoreError);
        Assert.Equal("restore-failed", service.GetStatus().Channels.First().Owner);

        device.RejectRestore = false;
        msi = false;
        clock.Now = clock.Now.AddSeconds(12);
        state.Publish(SnapshotWithTemperature(52, clock.Now), null);
        service.Tick();
        service.Tick();
        Assert.Null(service.RestoreError);
        Assert.Single(device.Writes, write => write.Id == "cpu-fan");
    }
}
