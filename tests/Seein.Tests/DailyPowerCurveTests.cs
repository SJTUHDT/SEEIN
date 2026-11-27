using Seein.Core;

namespace Seein.Tests;

public sealed class DailyPowerCurveTests
{
    private static readonly DateTimeOffset Midnight = new(new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Local));
    private static readonly DateOnly Date = DateOnly.FromDateTime(Midnight.LocalDateTime);

    private static PowerHistoryPoint Sample(int minute, double watts) =>
        new(Midnight.AddMinutes(minute).ToUnixTimeSeconds(), 60, watts, watts, watts, 60);

    [Fact]
    public void ShortInternalGapsConnectActualSamplesIncludingMeasuredZero()
    {
        var samples = new[] { Sample(8, 90), Sample(1, 50), Sample(4, 0), Sample(3, 80), Sample(9, 70) };
        var curve = DailyPowerCurve.GetMinuteAverages(Date, samples, Midnight.AddDays(1));
        Assert.Equal(1440, curve.Length);
        Assert.True(double.IsNaN(curve[0]));
        Assert.Equal(new double[] { 50, 65, 80, 0, 22.5, 45, 67.5, 90, 70 }, curve[1..10]);
        Assert.All(curve[10..], watts => Assert.Equal(0d, watts));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void ThresholdCountsOnlyMissingMinuteBars(int missing)
    {
        var end = 601 + missing;
        var curve = DailyPowerCurve.GetMinuteAverages(Date, [Sample(600, 50), Sample(end, 50 + (missing + 1) * 5)], Midnight.AddDays(1));
        Assert.Equal(50d, curve[600]);
        for (var offset = 1; offset <= missing; offset++)
            Assert.Equal(missing < 10 ? 50d + offset * 5 : 0d, curve[600 + offset]);
        Assert.Equal(50d + (missing + 1) * 5, curve[end]);
    }

    [Fact]
    public void LeadingAndTrailingGapsOfTenMinutesAreZeroWithoutASecondEndpoint()
    {
        var curve = DailyPowerCurve.GetMinuteAverages(Date, [Sample(10, 120)], Midnight.AddMinutes(20));
        Assert.All(curve[..10], watts => Assert.Equal(0d, watts));
        Assert.Equal(120d, curve[10]);
        Assert.All(curve[11..], watts => Assert.Equal(0d, watts));
    }

    [Fact]
    public void EmptyHistoricalDayShowsOnlyZeroWatts()
    {
        var curve = DailyPowerCurve.GetMinuteAverages(Date, [], Midnight.AddDays(1));
        Assert.Equal(1440, curve.Length);
        Assert.All(curve, watts => Assert.Equal(0d, watts));
    }

    [Fact]
    public void CurrentDayStopsAtCurrentMinuteAndDoesNotBorrowAnotherDaysSamples()
    {
        var curve = DailyPowerCurve.GetMinuteAverages(Date,
            [Sample(0, 10), Sample(1440, 99), Sample(3, 30)], Midnight.AddMinutes(2));
        Assert.Equal(new double[] { 10, double.NaN, double.NaN }, curve);
        Assert.Empty(DailyPowerCurve.GetMinuteAverages(Date.AddDays(1), [], Midnight));
    }
}
