using Seein.Core.Thermal;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace Seein.Tray;

public partial class FanControlView : System.Windows.Controls.UserControl
{
    private sealed class Card(string id, string fallbackLabel) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        public string Id { get; } = id;
        public string Label => fallbackLabel;
        public string AccessibleAction => $"{Action} · {Label}";
        public string Rpm { get; set; } = "— RPM";
        public string Target { get; set; } = "目标 —";
        public string Temperature { get; set; } = "温度 —";
        public string Owner { get; set; } = "等待状态";
        public string Detail { get; set; } = "";
        public string Action { get; set; } = "查看曲线";
    }

    private readonly Card[] _cards =
    [
        new("cpu-fan", "冷排风扇"), new("pump-fan", "水冷泵"),
        new("system-fan-4", "机箱风扇"), new("gpu-fan", "RX 9070 XT")
    ];
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromSeconds(16) };
    private readonly Stack<FanSettings> _boardUndo = new();
    private readonly Stack<(IReadOnlyList<FanCurvePoint> Points, bool DisableZero, GpuFanStatus? Baseline)> _gpuUndo = new();
    private FanSettings _applied = FanDefaults.DefaultSettings();
    private FanSettings? _draft;
    private FanStatus? _status;
    private IReadOnlyList<FanCurvePoint>? _gpuDraft;
    private bool _disableZero;
    private GpuFanStatus? _gpuBaseline;
    private bool _loading = true;
    private string? _channel;
    private string? _pending;

    public static readonly DependencyProperty CardColumnsProperty =
        DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(FanControlView), new PropertyMetadata(2));

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    public FanControlView()
    {
        InitializeComponent();
        SummaryCards.ItemsSource = _cards;
        foreach (var source in Enum.GetValues<FanTemperatureSource>())
            SourceSelector.Items.Add(new ComboBoxItem { Content = SourceName(source), Tag = source });
        _poll.Tick += (_, _) => RequestState();
        _timeout.Tick += (_, _) => ShowTransportError("等待后台服务响应超时。草稿已保留。");
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                RequestState();
                _poll.Start();
            }
            else _poll.Stop();
        };
        _loading = false;
        UpdateButtons();
    }

    public event Action<FanSettings, string?, GpuFanCommand?>? ConfigRequested;
    public event Action? RestoreRequested;
    public event Action? StateRequested;

    public void RequestState()
    {
        if (_pending is null) StateRequested?.Invoke();
    }

    public void Load(FanStateResult result)
    {
        var priorGpu = _status?.Gpu;
        _applied = FanDefaults.Normalize(result.Settings);
        _status = result.Status;
        RefreshProfile();
        RefreshLive();
        if (_channel == "gpu-fan" && (priorGpu?.CanWrite != result.Status.Gpu?.CanWrite ||
            (priorGpu?.Curve is null) != (result.Status.Gpu?.Curve is null)))
            RefreshEditor();
        UpdateButtons();
    }

    public void ShowResult(FanStatus? status, FanSettings? settings, bool success, string? error)
    {
        var command = _pending;
        EndPending();
        if (success && command == "apply")
        {
            _draft = null;
            _boardUndo.Clear();
        }
        if (success && command == "applyGpu")
        {
            _gpuDraft = null;
            _gpuBaseline = null;
            _disableZero = false;
            _gpuUndo.Clear();
        }
        if (success && command == "restore")
        {
            _draft = null;
            _gpuDraft = null;
            _gpuBaseline = null;
            _disableZero = false;
            _boardUndo.Clear();
            _gpuUndo.Clear();
        }
        if (status is not null && settings is not null) Load(new(status, settings));
        RefreshEditor();
        Feedback.Text = success ? command switch
        {
            "apply" => "策略已保存。控制状态以 PWM 与 RPM 回读为准。",
            "applyGpu" => "AMD 驱动已回读风扇设置。",
            "restore" => "已交还硬件控制。",
            _ => "服务命令已执行，请查看控制诊断。"
        } : error ?? "后台服务未能执行命令。";
    }

    public void ShowTransportError(string message)
    {
        EndPending();
        Feedback.Text = message;
    }

    private FanSettings Draft => _draft ?? _applied;
    private FanProfile Profile => Draft.Profiles!.First(profile => profile.Id == Draft.ActiveProfileId);
    private FanProfile? AppliedProfile => _applied.Profiles!.FirstOrDefault(profile => profile.Id == _applied.ActiveProfileId);
    private bool IsGpu => _channel == "gpu-fan";
    private bool SecondaryBranch => BranchSelector.SelectedIndex == 1;
    private GpuFanStatus? Gpu => _status?.Gpu;

    private FanChannelState? FindState(string id) => _status?.Channels.FirstOrDefault(channel =>
        channel.Id == id || (id == "pump-fan" && FanDefaults.FloorFor(channel.Id) == 55));

    private FanChannelSettings? CurrentChannel()
    {
        if (_channel is null || IsGpu) return null;
        var id = FindState(_channel)?.Id ?? _channel;
        return FanDefaults.FindChannel(Profile, id) ?? new(id, Curve: FanDefaults.CurveFor(id, Profile.Id),
            FloorPercent: FanDefaults.FloorFor(id), Source: Profile.Source);
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        CardColumns = e.NewSize.Width >= 860 ? 4 : e.NewSize.Width >= 420 ? 2 : 1;
    }

    private void RefreshProfile()
    {
        var profiles = Draft.Profiles!;
        _loading = true;
        if (!ProfileSelector.Items.Cast<FanProfile>().Select(profile => (profile.Id, profile.Name))
            .SequenceEqual(profiles.Select(profile => (profile.Id, profile.Name))))
        {
            ProfileSelector.DisplayMemberPath = nameof(FanProfile.Name);
            ProfileSelector.SelectedValuePath = nameof(FanProfile.Id);
            ProfileSelector.ItemsSource = profiles;
        }
        ProfileSelector.SelectedValue = Draft.ActiveProfileId;
        EnableToggle.IsChecked = Draft.Enabled;
        _loading = false;
    }

    private void RefreshLive()
    {
        if (_status is null) return;
        var errors = _status.Channels.Where(ch => ch.Error is not null).Select(ch => $"{ch.Label} · {ch.Error}").ToList();
        if (_status.MsiFanServiceRunning) errors.Insert(0, "微星风扇服务正在运行，主板通道接管暂停。");
        if (_status.Message is { } message) errors.Add(message);
        if (Gpu?.Owner == "suspended") errors.Add(Gpu.Message);
        DiagnosticText.Text = errors.Count == 0 ? "没有新的控制异常。" : string.Join(Environment.NewLine, errors.Distinct());
        MsiButton.Content = _status.MsiFanServiceRunning ? "暂停微星风扇服务" : "启动微星风扇服务";
        MonitoringRows.ItemsSource = _status.Channels.Where(ch => _cards.All(card => FindState(card.Id)?.Id != ch.Id))
            .Select(ch => $"{ch.RawName}  ·  {(ch.RpmAvailable || ch.Rpm > 0 ? $"{ch.Rpm} RPM" : "转速不可用")}  ·  只读").ToArray();

        MonitoringPanel.Visibility = MonitoringRows.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var card in _cards)
        {
            var board = FindState(card.Id);
            if (card.Id == "gpu-fan")
            {
                card.Rpm = Rpm(Gpu?.Rpm);
                card.Target = "由驱动计算占空比";
                card.Temperature = $"GPU 核心 {Degrees(Gpu?.TemperatureC)}";
                card.Owner = Owner(Gpu?.Owner ?? "driver");
                card.Detail = Gpu?.ZeroRpm switch { true => "Zero RPM 已开启", false => "Zero RPM 已关闭", _ => "Zero RPM 状态不可用" };
                card.Action = Gpu?.CanWrite == true ? "编辑曲线" : "查看显卡状态";
            }
            else
            {
                card.Rpm = Rpm(board is { RpmAvailable: true } || board?.Rpm > 0 ? board?.Rpm : null);
                card.Target = $"目标 {Percent(board?.TargetPercent)} · 回读 {Percent(board?.Pwm)}";
                card.Temperature = TemperatureText(board);
                card.Owner = board is null ? "通道离线" : Owner(board.Owner);
                card.Detail = board is null ? "等待读数" : $"{board.RawName} · 下限 {board.FloorPercent:0}%";
                card.Action = "编辑策略";
            }
            card.Refresh();
        }

        if (_channel is not null)
        {
            var board = FindState(_channel);
            DetailLive.Text = IsGpu ? $"{Rpm(Gpu?.Rpm)} · GPU 核心 {Degrees(Gpu?.TemperatureC)} · {Gpu?.Message ?? "显卡状态不可用。"}"
                : $"{Owner(board?.Owner)} · {Rpm(board?.RpmAvailable == true ? board.Rpm : null)} · {TemperatureText(board)}" +
                    (board?.Error is string error ? $" · {error}" : "");
            CurveEditor.Temperature = IsGpu ? null : SecondaryBranch ? board?.SecondaryTemperatureC : board?.TemperatureC;
            TrendChart.Samples = IsGpu ? Gpu?.Trend ?? [] : board?.Trend ?? [];
        }
    }

    private static string SourceName(FanTemperatureSource source) => source switch
    {
        FanTemperatureSource.CpuPackage => "CPU 封装",
        FanTemperatureSource.GpuCore => "GPU 核心",
        FanTemperatureSource.Motherboard => "主板",
        FanTemperatureSource.CpuAndGpu => "CPU 与 GPU 分别计算",
        _ => "传感器最高温度"
    };

    private static string Owner(string? owner) => owner switch
    {
        "seein" => "SEEIN 控制",
        "verifying" => "等待回读确认",
        "driver" => "AMD 驱动控制",
        "restore-failed" => "交还失败",
        "suspended" => "接管已暂停",
        "firmware" => "BIOS 控制",
        _ => "通道离线"
    };

    private static string Rpm(int? rpm) => rpm.HasValue ? $"{rpm:N0} RPM" : "— RPM";
    private static string Percent(double? percent) => percent.HasValue ? $"{percent:0}%" : "—";
    private static string Degrees(double? temperature) => temperature.HasValue ? $"{temperature:0} °C" : "— °C";

    private static string TemperatureText(FanChannelState? channel)
    {
        var source = channel?.Source ?? FanTemperatureSource.CpuPackage;
        return source == FanTemperatureSource.CpuAndGpu
            ? $"CPU {Degrees(channel?.TemperatureC)} · GPU {Degrees(channel?.SecondaryTemperatureC)}"
            : $"{SourceName(source)} {Degrees(channel?.TemperatureC)}";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        _channel = id;
        BranchSelector.SelectedIndex = 0;
        OverviewPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = DetailPanel.Visibility = Visibility.Visible;
        BodyScroll.ScrollToTop();
        RefreshEditor();
        RefreshLive();
        BackButton.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _channel = null;
        DetailPanel.Visibility = BackButton.Visibility = Visibility.Collapsed;
        OverviewPanel.Visibility = Visibility.Visible;
        BodyScroll.ScrollToTop();
        RestoreButton.Focus();
    }

    private void RefreshEditor()
    {
        if (_channel is null) { UpdateButtons(); return; }
        _loading = true;
        DetailTitle.Text = _cards.First(card => card.Id == _channel).Label + (IsGpu ? " · 风扇状态" : " · 曲线编辑器");
        BoardOptions.Visibility = IsGpu ? Visibility.Collapsed : Visibility.Visible;
        DisableZero.Visibility = IsGpu && Gpu?.ZeroRpm is not null ? Visibility.Visible : Visibility.Collapsed;
        DisableZero.IsEnabled = Gpu?.CanWrite == true && _pending is null;
        DisableZero.IsChecked = _disableZero;
        var channel = CurrentChannel();
        if (channel is not null)
        {
            ModeSelector.SelectedIndex = (int)channel.Mode;
            var selectedSource = channel.Source ?? Profile.Source;
            SourceSelector.SelectedItem = SourceSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selectedSource));
            ManualValue.Minimum = Math.Max(0, channel.FloorPercent);
            ManualValue.Maximum = Math.Max(ManualValue.Minimum, channel.CeilingPercent);
            ManualValue.Value = channel.ManualPercent;
            ManualPanel.Visibility = channel.Mode == FanChannelMode.Manual ? Visibility.Visible : Visibility.Collapsed;
            var dual = selectedSource == FanTemperatureSource.CpuAndGpu;
            BranchSelector.Visibility = dual && channel.Mode == FanChannelMode.Curve ? Visibility.Visible : Visibility.Collapsed;
            if (!dual) BranchSelector.SelectedIndex = 0;
            SimSecondaryPanel.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
            SimName.Text = dual ? "CPU °C" : $"{SourceName(selectedSource)} °C";
        }
        else
        {
            SimSecondaryPanel.Visibility = Visibility.Collapsed;
            SimName.Text = "曲线温度 °C";
        }

        var baseline = IsGpu ? Gpu?.Curve ?? [] : AppliedPoints();
        var points = IsGpu ? _gpuDraft ?? baseline : GetCurve(channel)?.Points ?? [];
        var editable = IsGpu ? Gpu?.CanWrite == true : channel?.Mode == FanChannelMode.Curve;
        ChartPanel.Visibility = (IsGpu || channel?.Mode == FanChannelMode.Curve) ? Visibility.Visible : Visibility.Collapsed;
        PointsPanel.Visibility = KeyboardHelp.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        KeyboardHelp.Text = IsGpu ? "Ctrl + ← / → 选点，方向键按驱动步进微调。"
            : "Ctrl + ← / → 选点，方向键微调，Insert 新增，Delete 删除。";
        CurveEditor.IsReadOnly = !editable || _pending is not null;
        CurveEditor.FixedPointCount = IsGpu;
        CurveEditor.DutyRange = IsGpu ? Gpu?.SpeedRange ?? new(0, 100, 1) : new((int)Math.Ceiling(Math.Max(0, channel!.FloorPercent)), 100, 1);
        var allPoints = points.Concat(baseline).ToArray();
        var low = allPoints.Length == 0 ? 20 : Math.Clamp((int)Math.Floor(allPoints.Min(point => point.TemperatureC)), 0, 100);
        var high = allPoints.Length == 0 ? 100 : Math.Clamp((int)Math.Ceiling(allPoints.Max(point => point.TemperatureC)), 21, 130);
        CurveEditor.TemperatureRange = IsGpu ? Gpu?.TemperatureRange ?? new(20, 100, 1)
            : new(Math.Min(low, 20), Math.Max(high, 100), 1);
        CurveEditor.ActivePoints = baseline;
        CurveEditor.Points = points;
        CurveEditor.Temperature = IsGpu ? null : SecondaryBranch ? FindState(_channel)?.SecondaryTemperatureC : FindState(_channel)?.TemperatureC;
        SyncPointFields();
        ApplyDetailButton.Content = IsGpu ? "应用显卡曲线" : "应用主板设置";
        _loading = false;
        UpdateButtons();
    }

    private FanCurveSettings? GetCurve(FanChannelSettings? channel) => SecondaryBranch
        ? channel?.GpuCurve ?? FanDefaults.CaseGpuCurve(Profile.Id)
        : channel?.Curve;

    private IReadOnlyList<FanCurvePoint> AppliedPoints()
    {
        var setting = FanDefaults.FindChannel(AppliedProfile!, FindState(_channel!)?.Id ?? _channel!);
        if (setting?.Mode != FanChannelMode.Curve) return [];
        return (SecondaryBranch ? setting.GpuCurve : setting.Curve)?.Points ?? [];
    }

    private void RecordUndo()
    {
        if (IsGpu)
        {
            _gpuBaseline ??= Gpu;
            _gpuUndo.Push((_gpuDraft ?? Gpu?.Curve ?? [], _disableZero, _gpuBaseline));
        }
        else _boardUndo.Push(Draft);
    }

    private void UpdateChannel(FanChannelSettings updated)
    {
        var profile = Profile;
        var channels = profile.Channels.Where(channel => !StringComparer.OrdinalIgnoreCase.Equals(channel.Id, updated.Id))
            .Append(updated).ToArray();
        _draft = Draft with
        {
            Profiles = Draft.Profiles!.Select(entry => entry.Id == profile.Id ? entry with { Channels = channels } : entry).ToArray()
        };
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileSelector.SelectedValue is not string profile || profile == Draft.ActiveProfileId) return;
        RecordUndo();
        _draft = Draft with { ActiveProfileId = profile };
        RefreshLive();
        UpdateButtons();
    }

    private void BoardOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _pending is not null) return;
        if (ReferenceEquals(sender, EnableToggle))
        {
            RecordUndo();
            _draft = Draft with { Enabled = EnableToggle.IsChecked == true };
            UpdateButtons();
            return;
        }
        var channel = CurrentChannel();
        if (channel is null || SourceSelector.SelectedItem is not ComboBoxItem { Tag: FanTemperatureSource source } ||
            ModeSelector.SelectedIndex < 0) return;
        var updated = channel with
        {
            Mode = (FanChannelMode)ModeSelector.SelectedIndex,
            Source = source,
            ManualPercent = ReferenceEquals(sender, ManualValue) ? ManualValue.Value : channel.ManualPercent,
            GpuCurve = source == FanTemperatureSource.CpuAndGpu ? channel.GpuCurve ?? FanDefaults.CaseGpuCurve(Profile.Id) : channel.GpuCurve
        };
        if (updated == channel) return;
        RecordUndo();
        UpdateChannel(updated);
        if (!ReferenceEquals(sender, ManualValue)) RefreshEditor();
        else UpdateButtons();
    }

    private void GpuOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Gpu?.CanWrite != true) return;
        RecordUndo();
        _disableZero = DisableZero.IsChecked == true;
        UpdateButtons();
    }

    private void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) RefreshEditor();
    }

    private void CurveEdit_Started(object? sender, EventArgs e)
    {
        if (!_loading) RecordUndo();
    }

    private void Curve_Changed(object? sender, EventArgs e)
    {
        if (_loading) return;
        if (IsGpu) _gpuDraft = CurveEditor.Points;
        else if (CurrentChannel() is { } channel && GetCurve(channel) is { } curve)
        {
            var updated = curve with { Points = CurveEditor.Points };
            UpdateChannel(SecondaryBranch ? channel with { GpuCurve = updated } : channel with { Curve = updated });
        }
        SyncPointFields();
        UpdateButtons();
    }

    private void CurveSelection_Changed(object? sender, EventArgs e)
    {
        if (!_loading) SyncPointFields();
    }

    private void SyncPointFields()
    {
        var wasLoading = _loading;
        _loading = true;
        var points = CurveEditor.Points;
        if (PointSelector.Items.Count != points.Count)
            PointSelector.ItemsSource = Enumerable.Range(1, points.Count).Select(index => $"点 {index}").ToArray();
        PointSelector.SelectedIndex = points.Count > 0 ? CurveEditor.SelectedIndex : -1;
        var point = points.ElementAtOrDefault(CurveEditor.SelectedIndex);
        PointTemperature.Text = point?.TemperatureC.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        PointDuty.Text = point?.Percent.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        PointsPanel.IsEnabled = points.Count > 0 && _pending is null;
        _loading = wasLoading;
    }

    private void PointSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && PointSelector.SelectedIndex >= 0) CurveEditor.SelectedIndex = PointSelector.SelectedIndex;
    }

    private void UpdatePoint_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(PointTemperature.Text, out var temperature) || !TryNumber(PointDuty.Text, out var duty) ||
            !CurveEditor.TryReplacePoint(temperature, duty))
            Feedback.Text = "请检查数值、驱动步进和曲线点的温度顺序。";
        else Feedback.Text = "已更新草稿。";
    }

    private void Simulate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(SimPrimary.Text, out var primary) || primary is < 0 or > 130)
        {
            SimResult.Text = "输入 0 至 130 °C。";
            return;
        }
        double? secondary = null;
        if (SimSecondaryPanel.Visibility == Visibility.Visible)
        {
            if (!TryNumber(SimSecondary.Text, out var gpuTemp) || gpuTemp is < 0 or > 130)
            {
                SimResult.Text = "输入 0 至 130 °C 的 GPU 温度。";
                return;
            }
            secondary = gpuTemp;
        }
        if (IsGpu)
        {
            var estimate = FanCurveMath.Evaluate(_gpuDraft ?? Gpu?.Curve, primary);
            SimResult.Text = estimate is double pwm
                ? $"曲线估算 {pwm:0}% · 实际行为由 AMD 驱动和 Zero RPM 决定。"
                : "缺少 AMD 驱动曲线。";
            return;
        }
        var channel = CurrentChannel();
        if (channel is null) return;
        var estimateBoard = FanPolicy.Evaluate(channel, channel.Source ?? Profile.Source, primary, secondary,
            Math.Max(FanDefaults.FloorFor(channel.Id), channel.FloorPercent), channel.CeilingPercent);
        var cpu = (channel.Source ?? Profile.Source) is FanTemperatureSource.CpuPackage or FanTemperatureSource.CpuAndGpu
            ? primary : (double?)null;
        SimResult.Text = estimateBoard is double target
            ? $"稳态目标 {(cpu >= 90 ? 100 : target):0}%"
            : "本通道使用 BIOS 曲线。";
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (IsGpu)
        {
            if (_gpuUndo.TryPop(out var old)) (_gpuDraft, _disableZero, _gpuBaseline) = old;
            if (_gpuDraft is not null && _gpuDraft.SequenceEqual(Gpu?.Curve ?? [])) _gpuDraft = null;
            if (_gpuDraft is null && !_disableZero) _gpuBaseline = null;
        }
        else if (_boardUndo.TryPop(out var old))
        {
            _draft = ReferenceEquals(old, _applied) || _boardUndo.Count == 0 ? null : old;
            RefreshProfile();
        }
        RefreshEditor();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        RecordUndo();
        if (IsGpu) { _gpuDraft = null; _disableZero = false; _gpuBaseline = null; }
        else if (CurrentChannel() is { } channel)
        {
            UpdateChannel(SecondaryBranch
                ? channel with { GpuCurve = FanDefaults.CaseGpuCurve(Profile.Id) }
                : channel with { Curve = FanDefaults.CurveFor(channel.Id, Profile.Id) });
        }
        Feedback.Text = IsGpu ? "已还原为驱动回读设置。" : "已将这条草稿曲线重置为配置档默认值。";
        RefreshEditor();
    }

    private void DiscardBoard_Click(object sender, RoutedEventArgs e)
    {
        _draft = null;
        _boardUndo.Clear();
        RefreshProfile();
        RefreshEditor();
        Feedback.Text = "已放弃主板草稿。";
    }

    private void ApplyBoard_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null || !BeginPending("apply")) return;
        ConfigRequested?.Invoke(Draft, null, null);
    }

    private void ApplyDetail_Click(object sender, RoutedEventArgs e)
    {
        if (!IsGpu) { ApplyBoard_Click(sender, e); return; }
        if (Gpu?.CanWrite != true || !BeginPending("applyGpu")) return;
        ConfigRequested?.Invoke(_applied, "applyGpu", new(_gpuDraft ?? Gpu.Curve ?? [], _disableZero ? false : null,
            _gpuBaseline?.Curve, _gpuBaseline?.ZeroRpm));
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginPending("restore")) return;
        RestoreRequested?.Invoke();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RequestState();

    private void Msi_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null || !BeginPending("msi")) return;
        ConfigRequested?.Invoke(_applied, _status.MsiFanServiceRunning ? "stopMsiServices" : "startMsiServices", null);
    }

    private bool BeginPending(string command)
    {
        if (_pending is not null) return false;
        _pending = command;
        _timeout.Start();
        Feedback.Text = "等待后台服务确认…";
        RefreshEditor();
        return true;
    }

    private void EndPending()
    {
        _pending = null;
        _timeout.Stop();
        RefreshEditor();
    }

    private void UpdateButtons()
    {
        var available = _pending is null;
        EnableToggle.IsEnabled = ProfileSelector.IsEnabled = available && _status is not null;
        ApplyBoardButton.IsEnabled = available && _status is not null && (_draft is not null || _applied.Enabled);
        BoardSummary.Text = (_status?.ControllerAvailable == true
            ? _status.Enabled ? "已请求 SEEIN 接管" : "主板处于 BIOS 自动控制"
            : "主板控制器离线") + $" · 当前配置档 {AppliedProfile?.Name ?? "—"}" +
            (_draft is null ? "" : " · 有未应用的修改");
        DiscardBoardButton.IsEnabled = available && _draft is not null;
        RestoreButton.IsEnabled = available;
        MsiButton.IsEnabled = available && _status is not null;
        BoardOptions.IsEnabled = available;
        ApplyDetailButton.IsEnabled = available && (IsGpu ? Gpu?.CanWrite == true &&
            (_gpuDraft is not null || _disableZero) : ApplyBoardButton.IsEnabled);
        UndoButton.IsEnabled = available && (IsGpu ? _gpuUndo.Count > 0 && Gpu?.CanWrite == true : _boardUndo.Count > 0);
        ResetButton.IsEnabled = available && (IsGpu ? Gpu?.CanWrite == true : CurrentChannel()?.Mode == FanChannelMode.Curve);
    }
}
