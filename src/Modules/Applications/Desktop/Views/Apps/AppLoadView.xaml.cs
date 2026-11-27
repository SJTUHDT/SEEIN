using Seein.Core;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;

namespace Seein.Tray;

/// <summary>
/// Resource usage of the machine and of each application: CPU share, GPU share and network bytes.
/// The machine itself is simply the first row of each table; every column header sorts.
/// </summary>
public partial class AppLoadView : System.Windows.Controls.UserControl
{
    private sealed class LoadRow
    {
        public string Name { get; init; } = string.Empty;
        public ImageSource? Icon { get; init; }
        public string DurationText { get; init; } = "—";
        public string ValueText { get; init; } = "—";
        public string Value2Text { get; init; } = "—";
        public string Value3Text { get; init; } = "—";
        public string MinText { get; init; } = "—";
        public string PeakText { get; init; } = "—";
        public string ShareText { get; init; } = "—";
        public bool IsMachine { get; init; }
        public double Seconds { get; init; }
        public double Percent { get; init; }
        public double Min { get; init; }
        public double Peak { get; init; }
        public double Share { get; init; }
        public long Up { get; init; }
        public long Down { get; init; }
        public long Total => Up + Down;

        public FontWeight NameWeight => IsMachine ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
        public FontWeight ValueWeight => IsMachine ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
    }

    private const double MinPercent = 0.5;

    private readonly ObservableCollection<LoadRow> _cpuRows = [];
    private readonly ObservableCollection<LoadRow> _gpuRows = [];
    private readonly ObservableCollection<LoadRow> _netRows = [];

    private AppLoadResult? _result;
    private string _cpuSort = "value";
    private bool _cpuDescending = true;
    private string _gpuSort = "value";
    private bool _gpuDescending = true;
    private string _netSort = "total";
    private bool _netDescending = true;

    public AppLoadView()
    {
        InitializeComponent();
        CpuList.ItemsSource = _cpuRows;
        GpuList.ItemsSource = _gpuRows;
        NetList.ItemsSource = _netRows;

        // Live view: the counters keep moving, so the page re-queries while it is on screen.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => RequestData();
        Loaded += (_, _) => { if (IsVisible) _refreshTimer.Start(); };
        Unloaded += (_, _) => _refreshTimer.Stop();
        UpdateHeader();
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                RequestData();
                _refreshTimer.Start();
            }
            else _refreshTimer.Stop();
        };
    }

    private readonly DispatcherTimer _refreshTimer;

    public event Action? DataRequested;

    private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Day the tables show; the host sends it with every request.</summary>
    public string LocalDate => _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void RequestData() => DataRequested?.Invoke();

    public void Load(AppLoadResult result)
    {
        _result = result;
        if (DateOnly.TryParseExact(result.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var loaded))
            _date = loaded;
        UpdateHeader();
        RenderCpu();
        RenderGpu();
        RenderNet();
    }

    private void RenderCpu()
    {
        _cpuRows.Clear();
        if (_result is null) return;
        var machine = _result.Machine;
        _cpuRows.Add(new LoadRow
        {
            Name = "整机",
            IsMachine = true,
            DurationText = AppFormat.Duration(machine.Seconds),
            MinText = FormatPercent(machine.CpuMin),
            ValueText = FormatPercent(machine.CpuPercent),
            PeakText = FormatPercent(machine.CpuPeak),
            ShareText = "100%",
            Seconds = machine.Seconds,
            Percent = machine.CpuPercent,
            Min = machine.CpuMin,
            Peak = machine.CpuPeak,
            Share = 100
        });
        var apps = _result.Apps.Where(row => row.CpuPercent >= MinPercent).Select(row => new LoadRow
        {
            Name = row.Name,
            Icon = AppIconCache.Get(row.IconPath, row.ExePath),
            DurationText = AppFormat.Duration(row.Seconds),
            MinText = FormatPercent(row.CpuMin),
            ValueText = FormatPercent(row.CpuPercent),
            PeakText = FormatPercent(row.CpuPeak),
            ShareText = FormatPercent(row.CpuShare),
            Seconds = row.Seconds,
            Percent = row.CpuPercent,
            Min = row.CpuMin,
            Peak = row.CpuPeak,
            Share = row.CpuShare
        });
        foreach (var row in Sorted(apps, _cpuSort, _cpuDescending)) _cpuRows.Add(row);
        UpdateHeaders([CpuNameHeader, CpuTimeHeader, CpuMinHeader, CpuValueHeader, CpuPeakHeader, CpuShareHeader],
            ["名称", "使用时长", "最低", "平均", "最高", "占比"], ["name", "time", "min", "value", "peak", "share"],
            _cpuSort, _cpuDescending);
    }

    private void RenderGpu()
    {
        _gpuRows.Clear();
        if (_result is null) return;
        var machine = _result.Machine;
        _gpuRows.Add(new LoadRow
        {
            Name = "整机",
            IsMachine = true,
            DurationText = AppFormat.Duration(machine.Seconds),
            MinText = FormatPercent(machine.GpuMin),
            ValueText = FormatPercent(machine.GpuPercent),
            PeakText = FormatPercent(machine.GpuPeak),
            ShareText = "100%",
            Seconds = machine.Seconds,
            Percent = machine.GpuPercent,
            Min = machine.GpuMin,
            Peak = machine.GpuPeak,
            Share = 100
        });
        var apps = _result.Apps.Where(row => row.GpuPercent >= MinPercent).Select(row => new LoadRow
        {
            Name = row.Name,
            Icon = AppIconCache.Get(row.IconPath, row.ExePath),
            DurationText = AppFormat.Duration(row.Seconds),
            MinText = FormatPercent(row.GpuMin),
            ValueText = FormatPercent(row.GpuPercent),
            PeakText = FormatPercent(row.GpuPeak),
            ShareText = FormatPercent(row.GpuShare),
            Seconds = row.Seconds,
            Percent = row.GpuPercent,
            Min = row.GpuMin,
            Peak = row.GpuPeak,
            Share = row.GpuShare
        });
        foreach (var row in Sorted(apps, _gpuSort, _gpuDescending)) _gpuRows.Add(row);
        UpdateHeaders([GpuNameHeader, GpuTimeHeader, GpuMinHeader, GpuValueHeader, GpuPeakHeader, GpuShareHeader],
            ["名称", "使用时长", "最低", "平均", "最高", "占比"], ["name", "time", "min", "value", "peak", "share"],
            _gpuSort, _gpuDescending);
    }

    private void RenderNet()
    {
        _netRows.Clear();
        if (_result is null) return;
        var machine = _result.Machine;
        _netRows.Add(new LoadRow
        {
            Name = "整机",
            IsMachine = true,
            DurationText = AppFormat.Duration(machine.Seconds),
            ValueText = FormatBytes(machine.NetUpBytes),
            Value2Text = FormatBytes(machine.NetDownBytes),
            Value3Text = FormatBytes(machine.NetUpBytes + machine.NetDownBytes),
            Seconds = machine.Seconds,
            Up = machine.NetUpBytes,
            Down = machine.NetDownBytes
        });
        var apps = _result.Apps
            .Where(row => row.NetUpBytes + row.NetDownBytes > 0)
            .Select(row => new LoadRow
            {
                Name = row.Name,
                Icon = AppIconCache.Get(row.IconPath, row.ExePath),
                DurationText = AppFormat.Duration(row.Seconds),
                ValueText = FormatBytes(row.NetUpBytes),
                Value2Text = FormatBytes(row.NetDownBytes),
                Value3Text = FormatBytes(row.NetUpBytes + row.NetDownBytes),
                Seconds = row.Seconds,
                Up = row.NetUpBytes,
                Down = row.NetDownBytes
            });
        foreach (var row in Sorted(apps, _netSort, _netDescending)) _netRows.Add(row);
        UpdateHeaders([NetNameHeader, NetUpHeader, NetDownHeader, NetTotalHeader],
            ["名称", "上传", "下载", "合计"], ["name", "up", "down", "total"], _netSort, _netDescending);
    }

    private static IEnumerable<LoadRow> Sorted(IEnumerable<LoadRow> rows, string column, bool descending)
    {
        Func<LoadRow, object> key = column switch
        {
            "name" => row => row.Name,
            "time" => row => row.Seconds,
            "up" => row => row.Up,
            "down" => row => row.Down,
            "total" => row => row.Total,
            "min" => row => row.Min,
            "peak" => row => row.Peak,
            "share" => row => row.Share,
            _ => row => row.Percent
        };
        return column == "name"
            ? (descending ? rows.OrderByDescending(key).ThenByDescending(row => row.Percent)
                          : rows.OrderBy(key).ThenByDescending(row => row.Percent))
            : (descending ? rows.OrderByDescending(key).ThenBy(row => row.Name)
                          : rows.OrderBy(key).ThenBy(row => row.Name));
    }

    private static void UpdateHeaders(IReadOnlyList<Button> buttons, IReadOnlyList<string> labels,
        IReadOnlyList<string> keys, string column, bool descending)
    {
        for (var index = 0; index < buttons.Count; index++)
        {
            var active = keys[index] == column;
            buttons[index].Content = active ? $"{labels[index]} {(descending ? "↓" : "↑")}" : labels[index];
        }
    }

    private void CpuSort_Click(object sender, RoutedEventArgs e) => Toggle(sender, ref _cpuSort, ref _cpuDescending, RenderCpu);
    private void GpuSort_Click(object sender, RoutedEventArgs e) => Toggle(sender, ref _gpuSort, ref _gpuDescending, RenderGpu);
    private void NetSort_Click(object sender, RoutedEventArgs e) => Toggle(sender, ref _netSort, ref _netDescending, RenderNet);

    private static void Toggle(object sender, ref string column, ref bool descending, Action render)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (column == tag) descending = !descending;
        else
        {
            column = tag;
            descending = tag != "name";
        }
        render();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RequestData();

    private void UpdateHeader()
    {
        DateText.Text = AppFormat.DateLabel(_date);
        NextButton.IsEnabled = _date < DateOnly.FromDateTime(DateTime.Now);
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _date = _date.AddDays(-1);
        UpdateHeader();
        RequestData();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        var next = _date.AddDays(1);
        if (next > DateOnly.FromDateTime(DateTime.Now)) return;
        _date = next;
        UpdateHeader();
        RequestData();
    }

    private static string FormatPercent(double value) => value > 0 ? $"{value:0.0}%" : "—";

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }
}
