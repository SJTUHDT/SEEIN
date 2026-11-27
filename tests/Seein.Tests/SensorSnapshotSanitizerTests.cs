using System.Text.Json;
using Seein.Core;
using Seein.Core.Hosting;
using Seein.Core.Sensors;

namespace Seein.Tests;

public sealed class SensorSnapshotSanitizerTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Unavailable_reading_does_not_break_the_entire_ipc_snapshot(double invalid)
    {
        var source=new SensorSnapshot(DateTimeOffset.Now,new Dictionary<string,double?>{{"cpu.total.load",12.5},{"hardware.unavailable",invalid},{"fan.stopped",0},{"unknown",null}});
        var clean=SensorSnapshotSanitizer.Normalize(source,out var keys);
        Assert.Equal(["hardware.unavailable"],keys);
        Assert.Null(clean.Get("hardware.unavailable"));
        Assert.Equal(12.5,clean.Get("cpu.total.load"));Assert.Equal(0,clean.Get("fan.stopped"));
        Assert.Equal(invalid,source.Get("hardware.unavailable"));
        var wire=JsonSerializer.Serialize(PipeEnvelope.Create("sensorSnapshot",clean),JsonDefaults.Options);
        var roundtrip=JsonSerializer.Deserialize<PipeEnvelope>(wire,JsonDefaults.Options)!.Read<SensorSnapshot>()!;
        Assert.Null(roundtrip.Get("hardware.unavailable"));Assert.Equal(12.5,roundtrip.Get("cpu.total.load"));
    }
    [Fact]
    public void Valid_samples_are_not_copied_or_changed()
    {
        var source=new SensorSnapshot(DateTimeOffset.Now,new Dictionary<string,double?>{{"stopped",0},{"unavailable",null},{"temperature",-2}});
        Assert.Same(source,SensorSnapshotSanitizer.Normalize(source,out var keys));Assert.Empty(keys);
    }
    [Fact]
    public void Plugin_enriched_readings_are_normalized_before_publication()
    {
        var hardware=new SharedHardware();
        hardware.Register("fixture",snapshot=>snapshot with{Values=new Dictionary<string,double?>{{"plugin.load",double.PositiveInfinity},{"plugin.temp",42}}});
        var clean=hardware.Read();
        Assert.Null(clean.Get("plugin.load"));Assert.Equal(42,clean.Get("plugin.temp"));Assert.Equal(["plugin.load"],hardware.InvalidSensorKeys);
        Assert.NotNull(PipeEnvelope.Create("sensorSnapshot",clean));
    }
}
