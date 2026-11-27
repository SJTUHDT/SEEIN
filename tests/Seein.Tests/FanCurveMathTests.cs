using Seein.Core.Thermal;

namespace Seein.Tests;

public sealed class FanCurveMathTests
{
    private static readonly FanCurvePoint[] Curve =
    [
        new(30, 30),
        new(60, 60),
        new(90, 100)
    ];

    [Fact]
    public void InterpolatesBetweenPoints()
    {
        Assert.Equal(30, FanCurveMath.Evaluate(Curve, 30));
        Assert.Equal(45, FanCurveMath.Evaluate(Curve, 45)!.Value, 3);
        Assert.Equal(80, FanCurveMath.Evaluate(Curve, 75)!.Value, 3);
    }

    [Fact]
    public void ClampsOutsideTheCurveRange()
    {
        Assert.Equal(30, FanCurveMath.Evaluate(Curve, 0));
        Assert.Equal(100, FanCurveMath.Evaluate(Curve, 200));
    }

    [Fact]
    public void EmptyCurveHasNoValue()
    {
        Assert.Null(FanCurveMath.Evaluate([], 50));
        Assert.Null(FanCurveMath.Evaluate(null, 50));
    }

    [Fact]
    public void SinglePointCurveIsConstant()
    {
        Assert.Equal(42, FanCurveMath.Evaluate([new FanCurvePoint(40, 42)], 90));
    }

    [Fact]
    public void ChannelClampKeepsPumpAboveItsFloor()
    {
        Assert.Equal(55, FanCurveMath.ClampToChannel(10, 55, 100));
        Assert.Equal(100, FanCurveMath.ClampToChannel(140, 25, 100));
        // A ceiling below the floor collapses to the floor: the pump safety wins.
        Assert.Equal(80, FanCurveMath.ClampToChannel(55, 80, 60));
    }

    [Fact]
    public void RisingTemperatureAppliesImmediately()
    {
        var target = FanCurveMath.ApplyHysteresis(previousTarget: 50, nextTarget: 70, hysteresisC: 3,
            previousTemperature: 60, temperature: 61, currentPercent: 50, floorPercent: 0);
        Assert.Equal(70, target);
    }

    [Fact]
    public void SmallCoolingStepIsHeldBack()
    {
        var target = FanCurveMath.ApplyHysteresis(previousTarget: 70, nextTarget: 50, hysteresisC: 3,
            previousTemperature: 61, temperature: 60, currentPercent: 70, floorPercent: 0);
        Assert.Equal(70, target);
    }

    [Fact]
    public void CoolingBeyondTheBandApplies()
    {
        var target = FanCurveMath.ApplyHysteresis(previousTarget: 70, nextTarget: 50, hysteresisC: 3,
            previousTemperature: 61, temperature: 50, currentPercent: 70, floorPercent: 0);
        Assert.Equal(50, target);
    }

    [Fact]
    public void StepLimitRampsInsteadOfJumping()
    {
        Assert.Equal(70, FanCurveMath.LimitStep(50, 100, 20));
        Assert.Equal(30, FanCurveMath.LimitStep(50, 0, 20));
        Assert.Equal(54, FanCurveMath.LimitStep(50, 54, 20));
    }

    [Fact]
    public void WriteThrottleHonoursIntervalAndDelta()
    {
        var now = DateTimeOffset.Now;
        Assert.True(FanCurveMath.ShouldWrite(null, null, 50, now, TimeSpan.FromSeconds(2), 3));
        Assert.False(FanCurveMath.ShouldWrite(50, now, 51, now.AddSeconds(1), TimeSpan.FromSeconds(2), 3));
        Assert.False(FanCurveMath.ShouldWrite(50, now, 51, now.AddSeconds(3), TimeSpan.FromSeconds(2), 3));
        Assert.True(FanCurveMath.ShouldWrite(50, now, 60, now.AddSeconds(3), TimeSpan.FromSeconds(2), 3));
    }
}
