using Seein.Core;
using Seein.Core.Apps;
using Seein.Core.Storage;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Seein.Tray;

public partial class AppUsageView : System.Windows.Controls.UserControl
{
    private const double MinVisibleSeconds = 30 * 60;
    private readonly ObservableCollection<AppListRow> _rows = [];
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);
    private bool _week;
    private readonly bool _initialized;
    private IReadOnlyList<AppTimelineRow> _timeline = [];

    public AppUsageView()
    {
        InitializeComponent();
        AppList.ItemsSource = _rows;
        SortSelector.SelectedIndex = 0;
        _initialized = true;
        UpdateHeader();
    }

    public event Action? DataRequested;
    public event Action? SummaryRequested;

    public string Kind => _week ? "week" : "day";
    public string LocalDate => _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public bool IncludeHidden => ShowAllToggle.IsChecked == true;
    private string SortKey => (SortSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "duration";

    public void RequestData() => DataRequested?.Invoke();

    public void Load(AppStatsResult result)
    {
        if (result.Error is not null)
        {
            _rows.Clear();
            ChartEmpty.Text = result.Error;
            ChartEmpty.Visibility = Visibility.Visible;
            return;
        }

        _timeline = result.Timeline;
        UpdateCards(result);
        if (_week)
        {
            Gantt.Visibility = Visibility.Collapsed;
            WeekChart.Visibility = Visibility.Visible;
            ChartEmpty.Visibility = Visibility.Collapsed;
            RenderWeek(result);
        }
        else
        {
            Gantt.Visibility = Visibility.Visible;
            WeekChart.Visibility = Visibility.Collapsed;
            RenderDay(result);
        }
        RenderList(result);
    }

    private void RenderDay(AppStatsResult result)
    {
        var day = DateOnly.ParseExact(result.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (startUtc, endUtc) = EnergyRepository.GetLocalDayUtcRange(day, TimeZoneInfo.Local);
        var nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var systemStart = result.SystemStartUtc ?? startUtc.ToUnixTimeSeconds();
        var systemEnd = Math.Min(nowUtc, endUtc.ToUnixTimeSeconds());

        var rows = new List<GanttRow>
        {
            new() { Name = "系统运行", Spans = [], IsSystem = true }
        };
        var visible = result.Timeline.Where(row => row.Seconds >= MinVisibleSeconds).ToArray();
        foreach (var item in visible.Take(40))
            rows.Add(new GanttRow
            {
                Name = item.Name,
                Spans = item.Spans,
                Seconds = item.Seconds,
                Color = item.Color
            });

        // Only the recorded part of the day is drawn; the empty head and tail are trimmed and the
        // remaining span is stretched.  As soon as new samples arrive the window grows with them.
        var dayStartUnix = startUtc.ToUnixTimeSeconds();
        var dayEndUnix = endUtc.ToUnixTimeSeconds();
        var spans = result.Timeline.SelectMany(row => row.Spans).ToArray();
        var hasSpans = spans.Length > 0;
        var windowStart = hasSpans ? Math.Max(dayStartUnix, Math.Min(spans.Min(span => span.StartUtc), systemStart)) : dayStartUnix;
        var windowEnd = hasSpans ? Math.Min(dayEndUnix, Math.Max(spans.Max(span => span.EndUtc), systemEnd)) : dayEndUnix;
        if (windowEnd - windowStart < 900) windowEnd = Math.Min(dayEndUnix, windowStart + 900);

        UpdateHiddenHint(result.Timeline.Count - visible.Length);
        Gantt.SetData(rows, windowStart, windowEnd, systemStart, systemEnd);
        _ = dayEndUnix;
        ChartEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChartEmpty.Text = result.Summary.WindowSeconds > 0
            ? "没有应用达到 30 分钟，可勾选「显示全部应用」查看"
            : "今天还没有采集到数据";
    }

    private void UpdateHiddenHint(int hidden) =>
        HiddenHint.Text = hidden > 0 ? $"已隐藏 {hidden} 个不足 30 分钟的应用" : string.Empty;

    private void RenderWeek(AppStatsResult result)
    {
        var series = result.WeekSeries.TakeLast(7).ToArray();
        var values = series.Select(point => point.Seconds).ToArray();
        var labels = series.Select(point => point.LocalDate.Length >= 10
            ? point.LocalDate[5..].Replace('-', '/')
            : point.LocalDate).ToArray();
        WeekChart.SetData(values, labels, AppFormat.DurationShort);
        ChartEmpty.Visibility = values.All(v => v <= 0) ? Visibility.Visible : Visibility.Collapsed;
        ChartEmpty.Text = "这一周还没有采集到数据";
    }

    private void RenderList(AppStatsResult result)
    {
        var visible = result.Timeline.Where(row => row.Seconds >= MinVisibleSeconds).ToArray();
        var source = SortKey == "power"
            ? visible.OrderByDescending(row => result.Power.FirstOrDefault(p => p.Key == row.Key)?.Wh ?? 0)
            : visible.OrderByDescending(row => row.Seconds);
        var list = source.ToArray();
        var maxSeconds = Math.Max(1, list.Select(row => row.Seconds).DefaultIfEmpty(1).Max());
        var powerByKey = result.Power.ToDictionary(row => row.Key, row => row, StringComparer.Ordinal);
        UpdateHiddenHint(result.Timeline.Count - visible.Length);

        _rows.Clear();
        foreach (var row in list)
        {
            powerByKey.TryGetValue(row.Key, out var power);
            var color = row.Color;
            _rows.Add(new AppListRow
            {
                Key = row.Key,
                Name = row.Name,
                Icon = AppIconCache.Get(row.ExePath),
                DurationText = AppFormat.Duration(row.Seconds),
                ActiveText = row.ActiveSeconds > 0 ? $"活跃 {AppFormat.Duration(row.ActiveSeconds)}" : "活跃 —",
                PowerText = AppFormat.Energy(row.Wh),
                SharePercent = Math.Clamp(row.Seconds / maxSeconds * 100d, 0, 100),
                CategoryBrush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color))
            });
        }
    }

    private void UpdateCards(AppStatsResult result)
    {
        if (!_week)
        {
            Card1Caption.Text = "应用运行时长合计";
            Card1Value.Text = AppFormat.Duration(result.Summary.TotalSeconds);
            Card2Caption.Text = "打开应用个数";
            Card2Value.Text = result.Summary.AppCount.ToString(CultureInfo.InvariantCulture);
            Card3Caption.Text = "系统已运行";
            Card3Value.Text = AppFormat.Duration(result.Summary.SystemSeconds);
            Card4Caption.Text = "活跃时长";
            Card4Value.Text = AppFormat.Duration(result.Summary.ActiveSeconds);
            return;
        }

        var series = result.WeekSeries.TakeLast(7).ToArray();
        var previous = result.WeekSeries.Take(7).ToArray();
        var currentSeconds = series.Sum(point => point.Seconds);
        var previousSeconds = previous.Sum(point => point.Seconds);
        var delta = previousSeconds > 0 ? (currentSeconds - previousSeconds) / previousSeconds * 100d : 0;

        Card1Caption.Text = "近 7 天合计";
        Card1Value.Text = AppFormat.Duration(currentSeconds);
        Card2Caption.Text = "日均时长";
        Card2Value.Text = AppFormat.Duration(currentSeconds / 7d);
        Card3Caption.Text = "与上周比";
        Card3Value.Text = previousSeconds > 0 ? $"{delta:+0.#;-0.#;0}%" : "—";
        Card4Caption.Text = "活跃时长";
        Card4Value.Text = AppFormat.Duration(result.Summary.ActiveSeconds);
    }

    private void UpdateHeader()
    {
        DateText.Text = _week ? $"{_date.AddDays(-6):M/d} – {_date:M/d}" : AppFormat.DateLabel(_date);
        NextButton.IsEnabled = _date < DateOnly.FromDateTime(DateTime.Now);
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _week = WeekModeButton.IsChecked == true;
        UpdateHeader();
        RequestData();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _date = _date.AddDays(_week ? -7 : -1);
        UpdateHeader();
        RequestData();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        var next = _date.AddDays(_week ? 7 : 1);
        if (next > DateOnly.FromDateTime(DateTime.Now)) return;
        _date = next;
        UpdateHeader();
        RequestData();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RequestData();

    private void Summary_Click(object sender, RoutedEventArgs e) => SummaryRequested?.Invoke();

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        RequestData();
    }

    private void ShowAll_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        RequestData();
    }

    // ---------------------------------------------------------------- row context menu

    public sealed class AppListRow
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ImageSource? Icon { get; init; }
        public string DurationText { get; init; } = string.Empty;
        public string ActiveText { get; init; } = string.Empty;
        public string PowerText { get; init; } = string.Empty;
        public double SharePercent { get; init; }
        public Brush? CategoryBrush { get; init; }
    }
}
