using Seein.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace Seein.Tray;

/// <summary>
/// Dashboard: machine power/energy on top, live CPU and GPU telemetry (the same sensors the
/// secondary display renders, without its space limits), system counters, fans and the calendar.
/// </summary>
public partial class OverviewView : System.Windows.Controls.UserControl
{
    private readonly Dictionary<string, DailyEnergyEntry> _entries = new(StringComparer.Ordinal);
    private string _month = string.Empty;
    private string? _selectedDate;

    public OverviewView() => InitializeComponent();

    public string DeviceStatus { set => DeviceText.Text = value; }
    public string CurrentPower { set => PowerText.Text = value; }
    public string TotalEnergy { set => TotalEnergyText.Text = value; }
    public string SessionEnergy { set => SessionEnergyText.Text = value; }

    /// <summary>Raised when the user picks a day in the calendar.</summary>
    public event Action<string>? DaySelected;

    public void SetHighestDay(double wattHours, string? localDate)
    {
        if (wattHours <= 0)
        {
            HighestDayText.Text = "— kWh";
            HighestDayDate.Text = "尚无记录";
            return;
        }
        HighestDayText.Text = $"{wattHours / 1000d:0.000} kWh";
        HighestDayDate.Text = DateOnly.TryParseExact(localDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? $"{date.Month} 月 {date.Day} 日"
            : "尚无记录";
    }

    /// <summary>Live telemetry from the service: the full sensor set the panel renders from.</summary>
    public void SetSensors(SensorSnapshot snapshot)
    {
        SetBar(CpuLoadBar, CpuLoadText, snapshot.Get(SensorKeys.CpuLoad));
        CpuTempText.Text = FormatValue(snapshot.Get(SensorKeys.CpuTemperature), "0", "°C");
        CpuPowerText.Text = FormatValue(snapshot.Get(SensorKeys.CpuPower), "0.0", "W");
        CpuClockText.Text = FormatClock(snapshot.Get(SensorKeys.CpuClock));
        CpuVoltageText.Text = snapshot.Get(SensorKeys.CpuVoltage) is double cpuVoltage && cpuVoltage >= 0.4
            ? FormatValue(cpuVoltage, "0.000", "V")
            : "—";

        SetBar(GpuLoadBar, GpuLoadText, snapshot.Get(SensorKeys.GpuLoad));
        GpuTempText.Text = FormatValue(snapshot.Get(SensorKeys.GpuTemperature), "0", "°C");
        GpuPowerText.Text = FormatValue(snapshot.Get(SensorKeys.GpuPower), "0.0", "W");
        GpuClockText.Text = FormatClock(snapshot.Get(SensorKeys.GpuClock));
        GpuVoltageText.Text = FormatValue(snapshot.Get(SensorKeys.GpuVoltage), "0.000", "V");
        GpuMemoryText.Text = snapshot.Get(SensorKeys.GpuMemoryUsed) is double memoryMb
            ? $"{memoryMb / 1024d:0.0} GB"
            : "—";
        GpuFanText.Text = snapshot.Get(SensorKeys.GpuFan) is double gpuFan
            ? (gpuFan > 0 ? $"{gpuFan:0} RPM" : "停转")
            : "—";

        // Confirmed wiring: CPU_FAN = 360 radiator fans, PUMP_FAN = water-block pump, SYS_FAN4 = case fans.
        FanRadiatorText.Text = FormatRpm(snapshot.Get(SensorKeys.FanRadiator));
        FanPumpText.Text = FormatRpm(snapshot.Get(SensorKeys.FanPump));
        FanCaseText.Text = FormatRpm(snapshot.Get(SensorKeys.FanCase));

        BoardTempText.Text = FormatValue(snapshot.Get(SensorKeys.MotherboardTemp), "0", "°C");
        CpuTempEchoText.Text = CpuTempText.Text;
        GpuTempEchoText.Text = GpuTempText.Text;
        StorageTempText.Text = FormatValue(snapshot.Get(SensorKeys.StorageTemp), "0", "°C");

        var memoryLoad = snapshot.Get(SensorKeys.MemoryLoad);
        SetBar(MemoryBar, MemoryText, memoryLoad);
        SetBar(DiskBar, DiskText, snapshot.Get(SensorKeys.SystemDriveLoad));

        NetDownText.Text = FormatRate(snapshot.Get(SensorKeys.NetworkDown));
        NetUpText.Text = FormatRate(snapshot.Get(SensorKeys.NetworkUp));
        DiskReadText.Text = FormatRate(snapshot.Get(SensorKeys.DiskRead));
        DiskWriteText.Text = FormatRate(snapshot.Get(SensorKeys.DiskWrite));
        UptimeText.Text = snapshot.Get(SensorKeys.Uptime) is double seconds
            ? AppFormat.LongDuration(seconds)
            : "—";
        FpsText.Text = snapshot.Get(SensorKeys.Fps) is double fps and > 0 ? $"{fps:0} FPS" : "—";
        VolumeText.Text = snapshot.Get(SensorKeys.SystemVolume) is double volume ? $"{volume:0}%" : "—";
    }

    private static void SetBar(ProgressBar bar, TextBlock label, double? value)
    {
        if (value is not double number || double.IsNaN(number))
        {
            bar.Value = 0;
            label.Text = "—";
            return;
        }
        bar.Value = Math.Clamp(number, 0, 100);
        label.Text = $"{(number > 100 ? 100 : number):0.0}%";
    }

    private static string FormatValue(double? value, string format, string unit) =>
        value is double number ? $"{number.ToString(format, CultureInfo.CurrentCulture)} {unit}" : "—";

    private static string FormatRpm(double? rpm) => rpm is > 0 ? $"{rpm.Value:0} RPM" : "—";

    private static string FormatClock(double? megahertz) =>
        megahertz is double value and > 0 ? $"{value / 1000d:0.00} GHz" : "—";

    private static string FormatRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not double value || value < 0) return "—";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    /// <summary>Month heat map: the day number sits inside a square whose shade follows the time in use.</summary>
    public void SetCalendar(string month, IReadOnlyList<DailyEnergyEntry> entries)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return;

        _month = month;
        _entries.Clear();
        foreach (var entry in entries) _entries[entry.LocalDate] = entry;
        if (_selectedDate is null || !_selectedDate.StartsWith(month, StringComparison.Ordinal))
            _selectedDate = DateTime.Now.ToString("yyyy-MM-dd");
        Render();
    }

    internal void RefreshAppearance() => Render();

    private void Render()
    {
        if (!DateOnly.TryParseExact(_month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            return;

        CalendarMonth.Text = $"{first.Year} 年 {first.Month} 月";
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var accentBrush = TryFindResource("AccentBrush") as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(0, 103, 192));
        var panel = TryFindResource("PanelBrush") as SolidColorBrush ?? Brushes.White;
        var muted = TryFindResource("SubtleTextBrush") as Brush ?? Brushes.DimGray;
        var contrastMode = SystemParameters.HighContrast;

        var cells = new List<CalendarCell>();
        var lead = ((int)first.DayOfWeek + 6) % 7;   // Monday first
        for (var blank = 0; blank < lead; blank++)
            cells.Add(new CalendarCell { Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0) });

        var days = DateTime.DaysInMonth(first.Year, first.Month);
        for (var day = 1; day <= days; day++)
        {
            var key = $"{first.Year:0000}-{first.Month:00}-{day:00}";
            _entries.TryGetValue(key, out var entry);
            var seconds = entry?.CoveredSeconds ?? 0;
            var isSelected = string.Equals(key, _selectedDate, StringComparison.Ordinal);
            var isToday = string.Equals(key, today, StringComparison.Ordinal);

            var highlight = entry is not null && !contrastMode;
            var background = highlight ? BandBrush(accentBrush.Color, panel.Color, seconds) : Brushes.Transparent;
            var foreground = highlight ? ReadableText(background.Color) : muted;

            cells.Add(new CalendarCell
            {
                Date = key,
                DayText = day.ToString(CultureInfo.InvariantCulture),
                Background = background,
                Foreground = foreground,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                BorderBrush = isSelected || isToday ? accentBrush : Brushes.Transparent,
                BorderThickness = new Thickness(isSelected ? 2 : isToday ? 1 : 0),
                Tip = entry is null
                    ? $"{first.Month} 月 {day} 日 · 无记录"
                    : $"{first.Month} 月 {day} 日 · 使用 {AppFormat.Duration(seconds)} · {entry.WattHours / 1000d:0.00} kWh"
            });
        }

        CalendarGrid.ItemsSource = cells;

        var monthSeconds = _entries.Where(pair => pair.Key.StartsWith(_month, StringComparison.Ordinal))
            .Sum(pair => pair.Value.CoveredSeconds);
        var monthWh = _entries.Where(pair => pair.Key.StartsWith(_month, StringComparison.Ordinal))
            .Sum(pair => pair.Value.WattHours);
        CalendarTotals.Text = $"本月合计 使用 {AppFormat.LongDuration(monthSeconds)} · 耗电 {monthWh / 1000d:0.00} kWh";
        FooterHint.Text = $"共 {_entries.Count} 天有记录 · 单日最高 {_entries.Values.Select(entry => entry.WattHours).DefaultIfEmpty(0).Max() / 1000d:0.00} kWh";
    }

    /// <summary>Four fixed bands: under 10 h, 10-15 h, 15-20 h and 20-24 h.</summary>
    private static byte BandAlpha(double seconds)
    {
        var hours = seconds / 3600d;
        if (hours < 10) return 0x33;
        if (hours < 15) return 0x66;
        if (hours < 20) return 0xAA;
        return 0xFF;
    }

    private static SolidColorBrush BandBrush(Color accent, Color background, double seconds)
    {
        var alpha = BandAlpha(seconds) / 255d;
        return new SolidColorBrush(Color.FromRgb(Blend(accent.R, background.R), Blend(accent.G, background.G),
            Blend(accent.B, background.B)));

        byte Blend(byte high, byte low) => (byte)Math.Round(low + (high - low) * alpha);
    }

    private static Brush ReadableText(Color background)
    {
        var luminance = .2126 * Linear(background.R) + .7152 * Linear(background.G) + .0722 * Linear(background.B);
        // At this threshold the two foregrounds have equal WCAG contrast against the cell.
        return luminance > .179 ? Brushes.Black : Brushes.White;

        static double Linear(byte channel)
        {
            var normal = channel / 255d;
            return normal <= .04045 ? normal / 12.92 : Math.Pow((normal + .055) / 1.055, 2.4);
        }
    }

    private void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string date } || string.IsNullOrEmpty(date)) return;
        _selectedDate = date;
        Render();
        DaySelected?.Invoke(date);
    }

    public event RoutedEventHandler? OpenEnergyRequested;

    private void OpenEnergy_Click(object sender, RoutedEventArgs e) => OpenEnergyRequested?.Invoke(sender, e);

    public sealed class CalendarCell
    {
        public string? Date { get; init; }
        public bool IsEnabled => !string.IsNullOrEmpty(Date);
        public string DayText { get; init; } = string.Empty;
        public Brush Background { get; init; } = Brushes.Transparent;
        public Brush Foreground { get; init; } = Brushes.Black;
        public Brush BorderBrush { get; init; } = Brushes.Transparent;
        public Thickness BorderThickness { get; init; } = new(0);
        public FontWeight FontWeight { get; init; } = FontWeights.Normal;
        public string? Tip { get; init; }
    }
}
