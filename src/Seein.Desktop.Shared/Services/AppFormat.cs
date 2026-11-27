using System.Globalization;

namespace Seein.Tray;

public static class AppFormat
{
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "0 分钟";
        var total = TimeSpan.FromSeconds(seconds);
        if (total.TotalHours >= 1)
            return $"{(int)total.TotalHours} 小时 {total.Minutes:00} 分";
        return $"{Math.Max(1, (int)total.TotalMinutes)} 分钟";
    }

    /// <summary>
    /// Compact two-unit duration used by the application list: below one day hours + minutes,
    /// then days + hours, months + days and finally years + months.
    /// </summary>
    public static string LongDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "0 分钟";
        var total = TimeSpan.FromSeconds(seconds);
        var days = total.TotalDays;
        if (days >= 365)
        {
            var years = (int)(days / 365);
            var months = (int)((days - years * 365) / 30);
            return months > 0 ? $"{years} 年 {months} 月" : $"{years} 年";
        }
        if (days >= 30)
        {
            var months = (int)(days / 30);
            var rest = (int)(days - months * 30);
            return rest > 0 ? $"{months} 月 {rest} 天" : $"{months} 月";
        }
        if (days >= 1)
            return total.Hours > 0 ? $"{(int)days} 天 {total.Hours} 小时" : $"{(int)days} 天";
        if (total.Hours >= 1)
            return total.Minutes > 0 ? $"{(int)total.TotalHours} 小时 {total.Minutes} 分" : $"{(int)total.TotalHours} 小时";
        return $"{Math.Max(1, total.Minutes)} 分";
    }

    public static string DurationShort(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "0";
        var total = TimeSpan.FromSeconds(seconds);
        return total.TotalHours >= 1 ? $"{total.TotalHours:0.#}h" : $"{total.TotalMinutes:0}m";
    }

    public static string Energy(double wattHours)
    {
        if (!double.IsFinite(wattHours) || wattHours <= 0) return "0 Wh";
        return wattHours >= 1000
            ? $"{wattHours / 1000d:0.00} kWh"
            : $"{wattHours:0.0} Wh";
    }

    public static string Percent(double value) => $"{value:0.#}%";

    public static string DateLabel(DateOnly date) => date.ToString("M月d日 ddd", CultureInfo.CurrentCulture);
}
