using Seein.Core;

namespace Seein.Tests;

public sealed class EnergyStatisticsTests
{
    [Fact]
    public void EmptyMonthHasNoHighOrLowRecord()
    {
        var result = EnergyMonthSummaryCalculator.Calculate([], new DateOnly(2026, 11, 16));
        Assert.Equal(0, result.TotalWh);
        Assert.Null(result.Highest);
        Assert.Null(result.Lowest);
        Assert.Null(result.AverageWh);
    }

    [Fact]
    public void SingleRecordedDayIsBothHighestAndLowestAndCountsCurrentPartialDay()
    {
        var today = new DateOnly(2026, 11, 16);
        var entry = new DailyEnergyEntry("2026-11-16", 120.5);
        var result = EnergyMonthSummaryCalculator.Calculate([entry], today);
        Assert.Equal(120.5, result.TotalWh);
        Assert.Equal(entry, result.Highest);
        Assert.Equal(entry, result.Lowest);
        Assert.Equal(0, result.CompletedDays);
    }

    [Fact]
    public void MissingDatesAreIgnoredAndTiedExtremesChooseEarliestDate()
    {
        var entries = new[]
        {
            new DailyEnergyEntry("2026-11-02", 100),
            new DailyEnergyEntry("2026-11-05", 20),
            new DailyEnergyEntry("2026-11-09", 100),
            new DailyEnergyEntry("2026-11-20", 20)
        };
        var result = EnergyMonthSummaryCalculator.Calculate(entries, new DateOnly(2026, 11, 16));
        Assert.Equal(240, result.TotalWh);
        Assert.Equal("2026-11-02", result.Highest!.LocalDate);
        Assert.Equal("2026-11-05", result.Lowest!.LocalDate);
        Assert.Equal(3, result.CompletedDays);
    }
}
