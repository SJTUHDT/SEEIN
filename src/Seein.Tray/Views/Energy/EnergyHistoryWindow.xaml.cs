using Seein.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;

namespace Seein.Tray;

public partial class EnergyHistoryWindow : System.Windows.Controls.UserControl
{
    private DateOnly _month = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private readonly Dictionary<string, double> _energyByDate = new(StringComparer.Ordinal);
    private DateOnly? _firstMonth;
    private DateOnly? _lastMonth;
    private DateOnly? _selectedDate;
    private readonly LatestRequestTracker _monthRequest = new();
    private readonly LatestRequestTracker _dayRequest = new();

    public EnergyHistoryWindow()
    {
        InitializeComponent();
        RenderCalendar();
    }

    public string Month => _month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public event Func<string, string, Task>? MonthRequested;
    public event Func<string, string, Task>? DayRequested;
    public event Action? BackRequested;
    public Task RefreshMonthAsync() => RequestCurrentMonthAsync();

    // Re-reads whichever view is on screen; the service answers with the current data.
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (DayView.Visibility == Visibility.Visible)
        {
            if (_selectedDate is DateOnly date) await SelectDayAsync(date);
            return;
        }
        MonthTotalText.Text = "读取中…";
        HighestDayText.Text = "—";
        LowestDayText.Text = "—";
        await RequestCurrentMonthAsync();
    }

    public void LoadHistory(EnergyHistoryResult result)
    {
        if (!string.Equals(result.Month, Month, StringComparison.Ordinal) || !_monthRequest.IsCurrent(result.RequestId)) return;
        if (result.Error is not null)
        {
            MonthTotalText.Text = "暂时无法读取";
            HighestDayText.Text = "—";
            LowestDayText.Text = "—";
            return;
        }
        _firstMonth = ParseMonth(result.FirstMonth);
        _lastMonth = ParseMonth(result.LastMonth);
        if (_firstMonth is not null && _month < _firstMonth)
        {
            _month = _firstMonth.Value;
            _ = RequestCurrentMonthAsync();
            return;
        }
        if (_lastMonth is not null && _month > _lastMonth)
        {
            _month = _lastMonth.Value;
            _ = RequestCurrentMonthAsync();
            return;
        }
        _energyByDate.Clear();
        foreach (var entry in result.Entries) _energyByDate[entry.LocalDate] = entry.WattHours;
        var summary = result.Summary ?? EnergyMonthSummaryCalculator.Calculate(result.Entries, DateOnly.FromDateTime(DateTime.Now));
        MonthTotalText.Text = FormatEnergy(summary.TotalWh);
        HighestDayText.Text = summary.Highest is null ? "无数据" : $"{DayOfMonth(summary.Highest.LocalDate)}  ·  {FormatEnergy(summary.Highest.WattHours)}";
        LowestDayText.Text = summary.Lowest is null ? "无数据" : $"{DayOfMonth(summary.Lowest.LocalDate)}  ·  {FormatEnergy(summary.Lowest.WattHours)}";
        RenderCalendar();
    }

    public void LoadDay(EnergyDayResult result)
    {
        if (!_dayRequest.IsCurrent(result.RequestId) || _selectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != result.LocalDate) return;
        if (result.Error is not null)
        {
            DayTotalText.Text = "读取失败";
            CurveHintText.Text = result.Error;
            return;
        }

        DayTotalText.Text = FormatEnergy(result.TotalWh);
        CpuEnergyText.Text = FormatCategory(result.CpuWh);
        GpuEnergyText.Text = FormatCategory(result.GpuWh);
        OtherEnergyText.Text = FormatCategory(result.OtherWh);
        LossEnergyText.Text = FormatCategory(result.LossWh);
        CpuCoverageText.Text = CoverageText(result.CpuCoveredSeconds, result.CoveredSeconds);
        GpuCoverageText.Text = CoverageText(result.GpuCoveredSeconds, result.CoveredSeconds);
        LegacyNotice.Visibility = result.IsLegacy ? Visibility.Visible : Visibility.Collapsed;
        LegacyText.Text = result.UnclassifiedWh > 0
            ? $"未分类历史总量：{FormatEnergy(result.UnclassifiedWh)}。该部分没有 CPU/GPU 等分项数据，未进行反推。"
            : "当天的估算分项来自传感器积分与配置基线，不是插座实测。";
        PowerChart.SetDay(_selectedDate, result.Points);
        UpdateChartHint();
    }

    private void ToggleChartMode_Click(object sender, RoutedEventArgs e)
    {
        PowerChart.DisplayMode = PowerChart.DisplayMode == PowerHistoryChartDisplayMode.Curve
            ? PowerHistoryChartDisplayMode.Samples
            : PowerHistoryChartDisplayMode.Curve;
        ChartModeButton.Content = PowerChart.DisplayMode == PowerHistoryChartDisplayMode.Curve
            ? "切换到采样条" : "切换到曲线";
        UpdateChartHint();
    }

    private void UpdateChartHint()
    {
        var mode = PowerChart.DisplayMode == PowerHistoryChartDisplayMode.Curve
            ? "平均功率：短缺口连线，连续缺采 ≥10 分钟归零" : "仅显示实际采样的最小至最大功率";
        CurveHintText.Text = $"{PowerChart.SampleCount} 个分钟采样 · {mode}";
    }

    private async void PreviousMonth_Click(object sender, RoutedEventArgs e) => await ChangeMonthAsync(-1);
    private async void NextMonth_Click(object sender, RoutedEventArgs e) => await ChangeMonthAsync(1);
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void CalendarBack_Click(object sender, RoutedEventArgs e)
    {
        DayView.Visibility = Visibility.Collapsed;
        CalendarView.Visibility = Visibility.Visible;
    }

    private async void PreviousDay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDate is not null) await SelectDayAsync(_selectedDate.Value.AddDays(-1));
    }

    private async void NextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDate is not null && _selectedDate.Value < DateOnly.FromDateTime(DateTime.Now))
            await SelectDayAsync(_selectedDate.Value.AddDays(1));
    }

    private async Task ChangeMonthAsync(int offset)
    {
        var requested = _month.AddMonths(offset);
        if (_firstMonth is not null && requested < _firstMonth || _lastMonth is not null && requested > _lastMonth) return;
        _month = requested;
        _energyByDate.Clear();
        MonthTotalText.Text = "读取中…";
        HighestDayText.Text = "无数据";
        LowestDayText.Text = "无数据";
        RenderCalendar();
        await RequestCurrentMonthAsync();
    }

    private async Task RequestCurrentMonthAsync()
    {
        RenderCalendar();
        var requestId = _monthRequest.Begin();
        if (MonthRequested is not null) await MonthRequested.Invoke(Month, requestId);
    }

    private async Task SelectDayAsync(DateOnly date)
    {
        _selectedDate = date;
        var requestId = _dayRequest.Begin();
        CalendarView.Visibility = Visibility.Collapsed;
        DayView.Visibility = Visibility.Visible;
        DayDateText.Text = date.ToString("yyyy 年 M 月 d 日", CultureInfo.CurrentCulture);
        DayTotalText.Text = "读取中…";
        CpuEnergyText.Text = GpuEnergyText.Text = OtherEnergyText.Text = LossEnergyText.Text = "—";
        CpuCoverageText.Text = GpuCoverageText.Text = string.Empty;
        LegacyNotice.Visibility = Visibility.Collapsed;
        PowerChart.SetDay(null, []);
        CurveHintText.Text = "正在读取分钟功率数据…";
        if (DayRequested is not null) await DayRequested.Invoke(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), requestId);
    }

    private void DayButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DateOnly date) _ = SelectDayAsync(date);
    }

    private static DateOnly? ParseMonth(string? month) =>
        month is not null && DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private void RenderCalendar()
    {
        MonthText.Text = $"{_month.Year} 年 {_month.Month} 月";
        var currentMonth = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        PreviousMonthButton.IsEnabled = _firstMonth is null ? string.CompareOrdinal(Month, currentMonth) > 0 : _month > _firstMonth;
        NextMonthButton.IsEnabled = _lastMonth is null ? string.CompareOrdinal(Month, currentMonth) < 0 : _month < _lastMonth;
        CalendarGrid.Children.Clear();
        var firstDay = _month.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)_month.DayOfWeek - 1;
        var days = DateTime.DaysInMonth(_month.Year, _month.Month);
        var weekCount = (int)Math.Ceiling((firstDay + days) / 7d);
        CalendarGrid.Rows = weekCount;
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var slot = 0; slot < weekCount * 7; slot++)
        {
            var day = slot - firstDay + 1;
            if (day < 1 || day > days)
            {
                CalendarGrid.Children.Add(new Border { Margin = new Thickness(3) });
                continue;
            }

            var date = new DateOnly(_month.Year, _month.Month, day);
            var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var hasEnergy = _energyByDate.TryGetValue(key, out var wattHours);
            var isFuture = date > today;
            var button = new Button
            {
                Tag = date,
                IsEnabled = !isFuture,
                Style = (Style)FindResource("CalendarDayButtonStyle"),
                Margin = new Thickness(3),
                ToolTip = $"{key} · {(hasEnergy ? FormatEnergy(wattHours) : "无记录")}"
            };
            button.Click += DayButton_Click;
            var content = new StackPanel();
            var dateText = new TextBlock
            {
                Text = day.ToString(CultureInfo.InvariantCulture),
                FontSize = 15,
                FontWeight = date == today ? FontWeights.Bold : FontWeights.SemiBold
            };
            // Resource references keep the cell text in sync with the light/dark switch.
            dateText.SetResourceReference(TextBlock.ForegroundProperty,
                date == today ? "AccentBrush" : isFuture ? "MutedTextBrush" : "TextBrush");
            content.Children.Add(dateText);
            var energyText = new TextBlock
            {
                Text = hasEnergy ? FormatEnergy(wattHours) : "无数据",
                FontSize = 15,
                // Same semi-bold weight as every other energy figure in the app.
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 7, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            energyText.SetResourceReference(TextBlock.ForegroundProperty, hasEnergy ? "AccentBrush" : "MutedTextBrush");
            content.Children.Add(energyText);
            button.Content = content;
            CalendarGrid.Children.Add(button);
        }
    }

    private static string DayOfMonth(string date) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("M/d", CultureInfo.InvariantCulture);
    private static string FormatEnergy(double wattHours) => wattHours >= 1000 ? $"{wattHours / 1000d:0.000} kWh" : $"{wattHours:0.0} Wh";
    private static string FormatCategory(double? wattHours) => wattHours is double value ? FormatEnergy(value) : "不可用";
    private static string CoverageText(double covered, double total) => total <= 0 || covered <= 0
        ? "传感器不可用" : $"有效采样 {Math.Clamp(covered / total * 100, 0, 100):0}%";
}
