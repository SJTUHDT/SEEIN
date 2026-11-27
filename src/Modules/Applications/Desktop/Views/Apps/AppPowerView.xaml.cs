using Seein.Core;
using Seein.Core.Apps;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;

namespace Seein.Tray;

public partial class AppPowerView : System.Windows.Controls.UserControl
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x00, 0x67, 0xC0), Color.FromRgb(0x2B, 0x88, 0xD8), Color.FromRgb(0x4A, 0x9E, 0xDD),
        Color.FromRgb(0x87, 0x64, 0xB8), Color.FromRgb(0xC2, 0x39, 0xB3), Color.FromRgb(0x00, 0x8C, 0x6E)
    ];

    private const double MinVisibleWh = 1d;
    private readonly ObservableCollection<PowerListRow> _rows = [];
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);
    private bool _week;
    private readonly bool _initialized;

    public AppPowerView()
    {
        InitializeComponent();
        PowerList.ItemsSource = _rows;
        _initialized = true;
        UpdateHeader();
    }

    public event Action? DataRequested;

    public string Kind => _week ? "week" : "day";
    public string LocalDate => _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void RequestData() => DataRequested?.Invoke();

    public void Load(AppStatsResult result)
    {
        if (result.Error is not null)
        {
            _rows.Clear();
            AppsWhText.Text = "读取失败";
            return;
        }

        var appsWh = result.Summary.TotalWh;
        var systemWh = result.Summary.SystemWh;
        var baselineWh = result.Summary.BaselineWh;
        var total = appsWh + systemWh + baselineWh;

        AppsWhText.Text = AppFormat.Energy(appsWh);
        SystemWhText.Text = AppFormat.Energy(systemWh);
        BaselineWhText.Text = AppFormat.Energy(baselineWh);
        ShareText.Text = total > 0 ? AppFormat.Percent(appsWh / total * 100d) : "—";

        RenderList(result, appsWh + systemWh);
        if (_week)
        {
            Donut.Visibility = Visibility.Collapsed;
            WeekChart.Visibility = Visibility.Visible;
            RenderWeek(result);
            LegendPanel.Children.Clear();
        }
        else
        {
            Donut.Visibility = Visibility.Visible;
            WeekChart.Visibility = Visibility.Collapsed;
            RenderDonut(result, appsWh, systemWh, baselineWh);
        }
    }

    private void RenderDonut(AppStatsResult result, double appsWh, double systemWh, double baselineWh)
    {
        // Baseline is not an application, so the donut only shows applications plus the system bucket;
        // everything under 1 Wh is folded into a single "其他应用" slice.
        var slices = new List<DonutSlice>();
        var ranked = result.Power.Where(row => row.Wh >= MinVisibleWh).ToArray();
        var top = ranked.Take(6).ToArray();
        for (var index = 0; index < top.Length; index++)
            slices.Add(new DonutSlice(top[index].Name, top[index].Wh, ColorOf(top[index].Color, index)));

        var other = result.Power.Where(row => row.Wh < MinVisibleWh).Sum(row => row.Wh)
                  + ranked.Skip(6).Sum(row => row.Wh);
        if (other > 0) slices.Add(new DonutSlice("其他应用", other, Color.FromRgb(0x8A, 0x8A, 0x8A)));
        if (systemWh > 0) slices.Add(new DonutSlice("系统进程", systemWh, Color.FromRgb(0x5C, 0x5C, 0x5C)));

        var total = slices.Sum(slice => slice.Value);
        Donut.SetData(slices, "应用与系统耗电", AppFormat.Energy(total));
        BuildLegend(slices, total);
        _ = appsWh;
        _ = baselineWh;
    }

    private void BuildLegend(IReadOnlyList<DonutSlice> slices, double total)
    {
        LegendPanel.Children.Clear();
        foreach (var slice in slices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(slice.Color)
            });
            row.Children.Add(new TextBlock
            {
                Text = slice.Label, FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(new TextBlock
            {
                Text = total > 0 ? AppFormat.Percent(slice.Value / total * 100d) : "—",
                FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedTextBrush")
            });
            LegendPanel.Children.Add(row);
        }
    }

    private void RenderWeek(AppStatsResult result)
    {
        var series = result.WeekSeries.TakeLast(7).ToArray();
        var values = series.Select(point => point.Wh).ToArray();
        var labels = series.Select(point => point.LocalDate.Length >= 10 ? point.LocalDate[5..].Replace('-', '/') : point.LocalDate).ToArray();
        WeekChart.SetData(values, labels, AppFormat.Energy);
    }

    private void RenderList(AppStatsResult result, double totalWh)
    {
        _rows.Clear();
        foreach (var row in result.Power.Where(row => row.Wh >= MinVisibleWh))
        {
            _rows.Add(new PowerListRow
            {
                Name = row.Name,
                Icon = AppIconCache.Get(row.ExePath),
                DurationText = AppFormat.Duration(row.Seconds),
                PowerText = AppFormat.Energy(row.Wh),
                ShareText = AppFormat.Percent(totalWh > 0 ? row.Wh / totalWh * 100d : 0),
                ToolTip = $"{row.Name} · {row.Category ?? CategoryCatalog.Fallback} · CPU {AppFormat.Energy(row.CpuWh)} · GPU {AppFormat.Energy(row.GpuWh)}",
                CategoryBrush = new SolidColorBrush(ColorOf(row.Color, 0))
            });
        }
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

    private static Color ColorOf(uint argb, int fallbackIndex)
    {
        if (argb == 0) return Palette[fallbackIndex % Palette.Length];
        return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    public sealed class PowerListRow
    {
        public Brush? CategoryBrush { get; init; }
        public string Name { get; init; } = string.Empty;
        public ImageSource? Icon { get; init; }
        public string DurationText { get; init; } = string.Empty;
        public string PowerText { get; init; } = string.Empty;
        public string ShareText { get; init; } = string.Empty;
        public string ToolTip { get; init; } = string.Empty;
    }
}
