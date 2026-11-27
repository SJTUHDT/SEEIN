namespace Seein.Core;

public static class EnergyMonthSummaryCalculator
{
    public static EnergyMonthSummary Calculate(IEnumerable<DailyEnergyEntry> entries, DateOnly today)
    {
        var valid = entries
            .Where(entry => double.IsFinite(entry.WattHours) && entry.WattHours >= 0 &&
                DateOnly.TryParseExact(entry.LocalDate, "yyyy-MM-dd", out _))
            .OrderBy(entry => entry.LocalDate, StringComparer.Ordinal)
            .ToArray();

        if (valid.Length == 0) return new EnergyMonthSummary(0, null, null, null, 0);

        var highest = valid.OrderByDescending(entry => entry.WattHours)
            .ThenBy(entry => entry.LocalDate, StringComparer.Ordinal).First();
        var lowest = valid.OrderBy(entry => entry.WattHours)
            .ThenBy(entry => entry.LocalDate, StringComparer.Ordinal).First();
        var completedDays = valid.Count(entry => DateOnly.ParseExact(entry.LocalDate, "yyyy-MM-dd") < today);
        return new EnergyMonthSummary(valid.Sum(entry => entry.WattHours), highest, lowest,
            valid.Average(entry => entry.WattHours), completedDays);
    }
}
