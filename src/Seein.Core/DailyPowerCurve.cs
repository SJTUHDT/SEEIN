namespace Seein.Core;

public static class DailyPowerCurve
{
    public const int MinutesPerDay = 24 * 60;
    private const int ZeroGapMinutes = 10;

    public static double[] GetMinuteAverages(DateOnly date, IReadOnlyList<PowerHistoryPoint> samples, DateTimeOffset asOf)
    {
        var now = asOf.LocalDateTime;
        var today = DateOnly.FromDateTime(now);
        if (date > today) return [];
        var length = date == today ? now.Hour * 60 + now.Minute + 1 : MinutesPerDay;
        var averages = new double[length];
        var sampled = new bool[length];
        foreach (var sample in samples)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(sample.UnixSeconds).LocalDateTime;
            var minute = local.Hour * 60 + local.Minute;
            if (DateOnly.FromDateTime(local) != date || minute >= length) continue;
            sampled[minute] = true;
            averages[minute] = sample.AverageWatts;
        }
        for (var minute = 0; minute < length;)
        {
            if (sampled[minute]) { minute++; continue; }
            var start = minute;
            while (minute < length && !sampled[minute]) minute++;
            var missing = minute - start;
            if (missing >= ZeroGapMinutes) continue;
            if (start == 0 || minute == length)
            {
                Array.Fill(averages, double.NaN, start, missing);
                continue;
            }
            var left = averages[start - 1];
            var step = (averages[minute] - left) / (missing + 1);
            for (var offset = 0; offset < missing; offset++)
                averages[start + offset] = left + step * (offset + 1);
        }
        return averages;
    }
}
