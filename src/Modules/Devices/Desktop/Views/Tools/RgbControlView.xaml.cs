using Seein.Core.Lighting;
using Seein.Core.Sensors;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using ListBox = System.Windows.Controls.ListBox;

namespace Seein.Tray;

/// <summary>
/// OpenRGB-style lighting console: device list, hardware mode selector, speed/brightness, zone list
/// with resize, draggable per-LED canvas, HSV colour picker, and profile save/load.  All device
/// operations go through the same OpenRGB SDK surface the OpenRGB client uses.
/// </summary>
public partial class RgbControlView : System.Windows.Controls.UserControl
{
    private sealed record DeviceRow(string? Address, string Name, string Detail, string? Key = null)
    {
        public string Tooltip => Name + Environment.NewLine + Detail;
    }

    private readonly ObservableCollection<DeviceRow> _devices = [];
    private readonly DispatcherTimer _poll;
    private readonly PlaybackPeakReader _previewPeak = new();
    private readonly DispatcherTimer _effectPreview = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Stopwatch _previewClock = Stopwatch.StartNew();
    private bool _previewingAudio;
    private RgbSettings _settings = RgbEffects.DefaultSettings();
    private RgbStatus? _status;
    private RgbDeviceState? _selectedDevice;
    private List<RgbColor> _frame = [];
    private bool _suppress;
    private RgbDeviceInfo? _shownDevice;
    private bool _commandPending;
    private bool _showGroup;
    private string? _pendingOperation;
    private RgbColor? _linkedColor;
    private double _hue = 207;
    private double _saturation = 100;
    private double _value = 75;

    public RgbControlView()
    {
        _suppress = true;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _poll.Tick += (_, _) => { if (!_commandPending) RequestState(); };
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) { RequestState(); _poll.Start(); }
            else _poll.Stop();
        };

        InitializeComponent();
        _effectPreview.Tick += (_, _) => UpdatePreview();
        EffectControls.IsVisibleChanged += (_, _) => UpdatePreview();
        DeviceList.ItemsSource = _devices;
        GroupEditor.CommandRequested += Send;
        foreach (var (id, name) in RgbEffects.All)
            EffectSelector.Items.Add(new ComboBoxItem { Content = name, Tag = id });

        EffectSelector.SelectionChanged += (_, _) => UpdatePreview();
        EffectSpeed.ValueChanged += (_, _) => UpdatePreview();
        EffectBrightness.ValueChanged += (_, _) => UpdatePreview();
        LedCanvas.SelectionChanged += OnSelectionChanged;
        LedCanvas.Painted += OnPainted;
        HueSlider.ValueChanged += (_, _) => UpdateColorFromSliders();
        SaturationSlider.ValueChanged += (_, _) => UpdateColorFromSliders();
        ValueSlider.ValueChanged += (_, _) => UpdateColorFromSliders();
        _suppress = false;
        UpdateColorFromSliders();
    }

    public event Action<RgbCommandRequest>? CommandRequested;

    public event Action? StateRequested;

    public void RequestState() => StateRequested?.Invoke();

    private RgbColor PaintColor => HsvToRgb(_hue, _saturation / 100, _value / 100);

    public void Load(RgbDevicesResult result)
    {
        _suppress = true;
        try
        {
            _settings = RgbEffects.Normalize(result.Settings);
            _status = result.Status;
            var previousAddress = _selectedDevice?.Info.Address;
            var previousKey = _selectedDevice?.Info.DeviceKey;

            var groupDeviceCount = result.Status.Devices.Count(device => device.Info.CanSetSolidColor ||
                RgbEffects.GroupExternalInput(device.Info, _settings) is not null);
            var rows = result.Status.Devices.Select(device =>
            {
                var zones = device.Info.Zones.Count > 0
                    ? string.Join(" / ", device.Info.Zones.Select(zone => $"{zone.Name}({zone.LedCount})"))
                    : "无分区";
                var capability = RgbEffects.IsExternalInput(device.Info.ActiveMode) ? "主板 ARGB 同步" :
                    HasLinkedGpuLeds(device.Info) ? "原生灯带控制" : device.Info.SupportsDirect ? $"{device.Info.LedCount} 灯珠" :
                    device.Info.SolidColorMode is not null ? "整组常亮" : "硬件模式";
                var detail = HasLinkedGpuLeds(device.Info) ? $"{device.Info.Type} · {capability}" :
                    $"{device.Info.Type} · {capability} · {zones}";
                return new DeviceRow(device.Info.Address, device.Info.Name, detail, device.Info.DeviceKey);
            }).Prepend(new DeviceRow(null, "整设备上色", $"统一上色 · {groupDeviceCount} 个可用设备")).ToArray();
            if (!_devices.SequenceEqual(rows))
            {
                _devices.Clear();
                foreach (var row in rows) _devices.Add(row);
            }

            var matches = result.Status.Devices.Where(device => device.Info.DeviceKey == previousKey).ToArray();
            _selectedDevice = _showGroup ? null : result.Status.Devices.FirstOrDefault(device => device.Info.Address == previousAddress)
                ?? (matches.Length == 1 ? matches[0] : null)
                ?? result.Status.Devices.FirstOrDefault();
            DeviceList.SelectedItem = _devices.First(entry => entry.Address == _selectedDevice?.Info.Address);
            _showGroup = _selectedDevice is null;

            UpdateSelectedDevice();

            ProviderText.Text = result.Status.LastError ?? result.Status.Message ??
                $"SEEIN 灯效引擎已连接 · {result.Status.Devices.Count} 个设备";

            GroupEditor.LoadState(_settings, result.Status);
        }
        finally
        {
            Dispatcher.BeginInvoke(new Action(() => _suppress = false), DispatcherPriority.Background);
        }
    }

    private void UpdateSelectedDevice()
    {
        var info = _selectedDevice?.Info;
        var linked = info is not null && HasLinkedGpuLeds(info);
        var layoutChanged = info?.Address != _shownDevice?.Address || info?.TopologyKey != _shownDevice?.TopologyKey;
        var modeChanged = layoutChanged || info?.ActiveModeIndex != _shownDevice?.ActiveModeIndex;
        _shownDevice = info;
        SelectedDeviceText.Text = _showGroup ? "整设备上色" :
            linked ? $"{info!.Name} · 灯带" :
            info is null ? "未选择设备" : info.SupportsDirect ? $"{info.Name} · {info.LedCount} 灯珠" :
            $"{info.Name} · 硬件模式";
        ModeControls.Visibility = _showGroup ? Visibility.Collapsed : Visibility.Visible;
        LoadDeviceProfileButton.Visibility = DeviceProfiles().Any() ? Visibility.Visible : Visibility.Collapsed;
        ZoneControls.Visibility = LedPanel.Visibility = EffectControls.Visibility = ApplySelectionButton.Visibility =
            _showGroup || linked ? Visibility.Collapsed : Visibility.Visible;
        GroupEditor.Visibility = _showGroup ? Visibility.Visible : Visibility.Collapsed;
        DeviceColors.Visibility = _showGroup ? Visibility.Collapsed : Visibility.Visible;
        LinkedPanel.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        var staticStrip = linked && info?.ActiveMode == "Static";
        LinkedModeLabel.Text = staticStrip ? "原生常亮 · 整条颜色预览" :
            RgbEffects.IsExternalInput(info?.ActiveMode) ? $"{info!.ActiveMode} · 跟随主板 ARGB" :
            $"当前硬件模式：{info?.ActiveMode ?? "未知"}";
        LinkedLeds.Visibility = staticStrip ? Visibility.Visible : Visibility.Collapsed;
        ApplySelectionButton.IsEnabled = info?.SupportsDirect == true;
        ApplySolidButton.Content = linked ? "应用到整条灯带" : "应用到此设备";
        RefreshSolidButton();
        if (modeChanged)
        {
            ModeSelector.Items.Clear();
            foreach (var mode in info?.Modes ?? [])
                ModeSelector.Items.Add(new ComboBoxItem
                {
                    Content = RgbEffects.IsExternalInput(mode.Name) ? $"{mode.Name}（主板同步）" : mode.Name,
                    Tag = mode.Index
                });
            if (ModeSelector.Items.Count > 0)
                ModeSelector.SelectedIndex = info!.ActiveModeIndex >= 0 && info.ActiveModeIndex < ModeSelector.Items.Count ? info.ActiveModeIndex : 0;
        }
        if (layoutChanged)
        {
            ZoneSelector.Items.Clear();
            foreach (var zone in info?.Zones ?? [])
                ZoneSelector.Items.Add(new ComboBoxItem
                {
                    Content = $"{(string.IsNullOrEmpty(zone.DisplayName) ? zone.Name : zone.DisplayName)}（{zone.LedCount} 灯珠）",
                    Tag = zone.Index
                });
            ZoneSelector.SelectedIndex = ZoneSelector.Items.Count > 0 ? 0 : -1;
        }

        _frame = _selectedDevice?.Colors.ToList() ?? [];
        var linkedColor = linked && _frame.Count > 0 ? _frame[0] : (RgbColor?)null;
        if (layoutChanged || linkedColor != _linkedColor)
        {
            _linkedColor = linkedColor;
            LinkedLeds.ItemsSource = linked ? Enumerable.Repeat(
                linkedColor is { } color ? new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)) : null, 10) : null;
        }
        while (_frame.Count < (info?.LedCount ?? 0)) _frame.Add(new RgbColor(0x20, 0x20, 0x20));
        if (layoutChanged) LedCanvas.SetDevice(info?.Zones ?? [], _frame);
        else LedCanvas.SetColors(_frame);
        if (modeChanged || info is null) UpdateModeControls();
        if (layoutChanged || info is null) UpdateZoneControls();
    }

    private static bool HasLinkedGpuLeds(RgbDeviceInfo info) => info is { SupportsDirect: false, LedCount: 1 } &&
        info.Vendor.Equals("Sapphire", StringComparison.OrdinalIgnoreCase) &&
        info.Name.Contains("9070 XT", StringComparison.OrdinalIgnoreCase) && info.SolidColorMode is not null;

    private void OnSelectionChanged(IReadOnlyList<int> selection) =>
        SelectionText.Text = selection.Count == 0 ? "未选中灯珠" : $"已选中 {selection.Count} 颗灯珠";

    private void OnPainted(IReadOnlyList<int> selection)
    {
        if (_selectedDevice?.Info.SupportsDirect != true || selection.Count == 0) return;
        foreach (var index in selection)
            if (index < _frame.Count) _frame[index] = PaintColor;
        LedCanvas.SetColors(_frame);
        if (selection.Count == 1)
            Send(new RgbCommandRequest("led", DeviceAddress: _selectedDevice.Info.Address,
                LedIndex: selection[0], Colors: [PaintColor]));
        else
            Send(new RgbCommandRequest("colors", DeviceAddress: _selectedDevice.Info.Address, Colors: _frame.ToArray(),
                PaintedLedIndices: selection));
    }

    private void ApplyToSelection_Click(object sender, RoutedEventArgs e)
    {
        var selection = LedCanvas.Selection;
        if (selection.Count == 0)
        {
            StatusText.Text = "先在画布上点选/拖选灯珠";
            return;
        }
        OnPainted(selection);
    }

    private void ApplySolid_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is { } device)
            Send(new RgbCommandRequest("solid", DeviceAddress: device.Info.Address, Colors: [PaintColor]));
    }

    private void RefreshSolidButton() => ApplySolidButton.IsEnabled = !_commandPending &&
        _selectedDevice?.Info.CanSetSolidColor == true;

    private void ApplyMode_Click(object sender, RoutedEventArgs e) => SendMode("mode");

    private void BootMode_Click(object sender, RoutedEventArgs e) => SendMode("bootMode");

    private IEnumerable<RgbProfile> DeviceProfiles()
    {
        if (_selectedDevice is not { } device) return [];
        return (_settings.Profiles ?? []).Where(profile => !RgbEffects.IsGroupProfile(profile) &&
            (profile.DeviceKey is { } key ? key == device.Info.DeviceKey :
             profile.DeviceName is { } name ? string.Equals(name, device.Info.Name, StringComparison.OrdinalIgnoreCase) :
             profile.Targets?.Any(target => target.DeviceKey == device.Info.DeviceKey) == true));
    }

    private void LoadDeviceProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_commandPending) return;
        var profile = RgbGroupEffectView.ChooseProfile(this, "载入设备配置", DeviceProfiles().ToArray(), _settings.ActiveProfileId);
        if (profile is not null) Send(new("loadProfile", ProfileId: profile.Id));
    }

    private void SendMode(string command)
    {
        if (_selectedDevice is null || ModeSelector.SelectedItem is not ComboBoxItem item || item.Tag is not int modeValue)
            return;
        var mode = _selectedDevice.Info.Modes.ElementAtOrDefault(modeValue);
        if (mode is null) return;
        Send(new RgbCommandRequest(command, DeviceAddress: _selectedDevice.Info.Address,
            ModeValue: modeValue, Colors: mode.HasModeColors || (mode.PerLedColor && !mode.IsDirect) ? [PaintColor] : null,
            Speed: mode.HasSpeed ? MapPercent(SpeedSlider.Value, mode.SpeedMin, mode.SpeedMax) : null,
            Brightness: mode.HasBrightness ? MapPercent(BrightnessSlider.Value, mode.BrightnessMin, mode.BrightnessMax) : null));
    }

    private void ApplyZone_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null || ZoneSelector.SelectedItem is not ComboBoxItem item || item.Tag is not int zoneIndex)
            return;
        Send(new RgbCommandRequest("zone", DeviceAddress: _selectedDevice.Info.Address,
            ZoneIndex: zoneIndex, Colors: [PaintColor]));
    }

    private void ResizeZone_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null || ZoneSelector.SelectedItem is not ComboBoxItem item || item.Tag is not int zoneIndex)
            return;
        if (!int.TryParse(ZoneSizeBox.Text, out var size) || size < 0)
        {
            StatusText.Text = "尺寸需要是非负整数";
            return;
        }
        Send(new RgbCommandRequest("resize", DeviceAddress: _selectedDevice.Info.Address,
            ZoneIndex: zoneIndex, ResizeSize: size));
    }

    private void Device_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _status is null || DeviceList.SelectedItem is not DeviceRow row) return;
        _showGroup = row.Address is null;
        _selectedDevice = _status.Devices.FirstOrDefault(device =>
            device.Info.Address == row.Address);
        _suppress = true;
        try
        {
            UpdateSelectedDevice();
        }
        finally { Dispatcher.BeginInvoke(new Action(() => _suppress = false), DispatcherPriority.Background); }
    }

    private void Mode_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) UpdateModeControls();
    }

    private void UpdateModeControls()
    {
        var mode = (ModeSelector.SelectedItem as ComboBoxItem)?.Tag is int index
            ? _selectedDevice?.Info.Modes.ElementAtOrDefault(index) : null;
        ApplyModeButton.IsEnabled = mode is not null;
        BootModeButton.IsEnabled = mode is not null;
        SpeedSlider.IsEnabled = mode?.HasSpeed == true;
        BrightnessSlider.IsEnabled = mode?.HasBrightness == true;
        if (mode is null) return;
        SpeedSlider.Value = ToPercent(mode.Speed, mode.SpeedMin, mode.SpeedMax);
        BrightnessSlider.Value = ToPercent(mode.Brightness, mode.BrightnessMin, mode.BrightnessMax);
    }

    private static double ToPercent(uint value, uint min, uint max) =>
        max > min ? ((double)value - min) / (max - min) * 100 : 0;

    private void Zone_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) UpdateZoneControls();
    }

    private void UpdateZoneControls()
    {
        var zone = (ZoneSelector.SelectedItem as ComboBoxItem)?.Tag is int index
            ? _selectedDevice?.Info.Zones.ElementAtOrDefault(index) : null;
        ZoneColorButton.IsEnabled = zone is { LedCount: > 0 } && _selectedDevice?.Info.SupportsDirect == true;
        ResizeButton.IsEnabled = zone is not null && (zone.Flags & 3) != 0 && zone.LedsMax > zone.LedsMin;
        ZoneSizeBox.IsEnabled = ResizeButton.IsEnabled;
        ZoneSizeBox.Text = zone?.LedCount.ToString() ?? "";
        UpdateEffectEditor();
    }

    private void EffectTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress) UpdateEffectEditor();
    }

    private void UpdateEffectEditor()
    {
        if (_showGroup) return;
        var key = _selectedDevice?.Info.DeviceKey;
        var zone = EffectTarget.SelectedIndex == 1 ? (ZoneSelector.SelectedItem as ComboBoxItem)?.Tag as int? : null;
        var effect = _settings.Targets?.FirstOrDefault(target => target.DeviceKey == key && target.ZoneIndex == zone)?.Effect
            ?? _settings.Targets?.FirstOrDefault(target => target.DeviceKey == key && target.ZoneIndex is null)?.Effect
            ?? _settings.GroupEffect ?? ActiveProfile().Effect;
        effect = RgbEffects.NormalizeGroupEffect(effect) ?? RgbEffects.DefaultsFor(RgbEffects.Static);
        SelectTag(EffectSelector, effect.EffectId);
        EffectSpeed.Value = effect.Speed;
        EffectBrightness.Value = effect.Brightness;
        SecondColorBox.Text = effect.Colors.Count > 1 ? effect.Colors[1].ToHex() : "#FF3366";
        ThirdColorBox.Text = effect.Colors.Count > 2 ? effect.Colors[2].ToHex() : "#36F5B4";
        FourthColorBox.Text = effect.Colors.Count > 3 ? effect.Colors[3].ToHex() : "#BB72FF";
        DeviceRainbowPalette.IsChecked = effect.UseRainbowPalette;
        EffectDirection.SelectedIndex = effect.Direction == "backward" ? 1 : 0;
        LowTemperature.Text = effect.LowTemperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HighTemperature.Text = effect.HighTemperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (effect.Colors.Count > 0)
        {
            HsvFromColor(effect.Colors[0]);
            UpdateColorFromSliders();
        }
        UpdatePreview();
    }

    private void ApplyEffect_Click(object sender, RoutedEventArgs e)
    {
        var address = EffectTarget.SelectedIndex == 2 ? null : _selectedDevice?.Info.Address;
        var zone = EffectTarget.SelectedIndex == 1 ? (ZoneSelector.SelectedItem as ComboBoxItem)?.Tag as int? : null;
        if ((EffectTarget.SelectedIndex != 2 && address is null) || (EffectTarget.SelectedIndex == 1 && zone is null))
        {
            StatusText.Text = "先选择灯效设备或分区";
            return;
        }
        var effect = ReadDeviceEffect();
        if (effect is null)
        {
            StatusText.Text = "请检查 HEX 颜色和温度范围。温度应在 0–120 °C 之间，且低温须小于高温。";
            return;
        }
        Send(new RgbCommandRequest("effect", DeviceAddress: address, ZoneIndex: zone, Colors: effect.Colors,
            EffectId: effect.EffectId, Speed: effect.Speed, Brightness: effect.Brightness,
            Direction: effect.Direction, UseRainbowPalette: effect.UseRainbowPalette, GroupEffect: effect));
    }

    private void Send(RgbCommandRequest request)
    {
        if (_commandPending) return;
        _commandPending = true;
        _pendingOperation = request.Command;
        GroupEditor.SetBusy(true);
        RefreshSolidButton();
        StatusText.Text = request.Command == "rescan" ? "正在重新扫描设备…" : "正在处理…";
        CommandRequested?.Invoke(request);
    }

    public void ResetPendingCommand()
    {
        _commandPending = false;
        _pendingOperation = null;
        GroupEditor.SetBusy(false);
        RefreshSolidButton();
    }

    public void ShowResult(RgbCommandResult result)
    {
        var command = _pendingOperation;
        ResetPendingCommand();
        if (result.Success && command == "loadGroupProfile") GroupEditor.LoadActiveDraft();
        if (result.Success && command == "loadProfile" && !_showGroup) UpdateEffectEditor();
        StatusText.Text = result.Outcome switch
        {
            RgbWriteOutcome.Sent => "命令已发送",
            RgbWriteOutcome.Confirmed => "引擎已确认",
            RgbWriteOutcome.ReadBack => "已更新并回读设备状态",
            RgbWriteOutcome.Saved when command is "groupEffect" or "loadGroupProfile" && result.Status?.GroupPlayback is not null
                => "灯效配置已保存，主板已回读。",
            RgbWriteOutcome.Saved when command == "groupEffect" => "灯效已应用，设备就绪后会播放。",
            RgbWriteOutcome.Saved when command is "loadProfile" or "loadGroupProfile" => "配置已载入。",
            RgbWriteOutcome.Saved => "配置已保存",
            _ => result.Error ?? "灯效命令失败"
        };
    }

    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        var color = RgbColor.FromHex(HexBox.Text, PaintColor);
        if (!string.Equals(color.ToHex(), HexBox.Text, StringComparison.OrdinalIgnoreCase)) return;
        HsvFromColor(color);
        UpdateColorFromSliders(skipHex: true);
    }

    private void UpdateColorFromSliders(bool skipHex = false)
    {
        _hue = HueSlider.Value;
        _saturation = SaturationSlider.Value;
        _value = ValueSlider.Value;
        var color = PaintColor;
        if (!skipHex)
        {
            var wasSuppressed = _suppress;
            _suppress = true;
            HexBox.Text = color.ToHex();
            _suppress = wasSuppressed;
        }
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        ColorSwatch.Background = brush;
        UpdatePreview();
    }

    private void HsvFromColor(RgbColor color)
    {
        var (hue, saturation, value) = RgbToHsv(color);
        var wasSuppressed = _suppress;
        _suppress = true;
        HueSlider.Value = hue;
        SaturationSlider.Value = saturation * 100;
        ValueSlider.Value = value * 100;
        _suppress = wasSuppressed;
    }

    internal static (double Hue, double Saturation, double Value) RgbToHsv(RgbColor color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = 0d;
        if (delta > 0.0001)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    internal static RgbColor HsvToRgb(double hue, double saturation, double value)
    {
        var h = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return new RgbColor(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static int MapPercent(double percent, uint min, uint max)
    {
        if (max <= min) return (int)max;
        return (int)Math.Round(min + Math.Clamp(percent, 0, 100) / 100d * (max - min));
    }

    private static void SelectTag(ComboBox combo, string? tag)
    {
        for (var index = 0; index < combo.Items.Count; index++)
            if (combo.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedIndex = index;
                return;
            }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private RgbProfile ActiveProfile() =>
        _settings.Profiles?.FirstOrDefault(profile => profile.Id == _settings.ActiveProfileId)
        ?? _settings.Profiles?.FirstOrDefault()
        ?? RgbEffects.DefaultSettings().Profiles![0];

    private string SelectedEffect => (EffectSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? RgbEffects.Static;

    private void EffectColor_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void EffectParameter_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private RgbEffectSettings? ReadDeviceEffect()
    {
        var option = RgbEffects.Options.First(entry => entry.Id == SelectedEffect);
        var rainbow = option.SupportsRainbowPalette && DeviceRainbowPalette.IsChecked == true;
        var colors = new List<RgbColor>();
        if (option.ColorCount > 0) colors.Add(PaintColor);
        foreach (var field in new[] { SecondColorBox, ThirdColorBox, FourthColorBox }.Take(Math.Max(0, option.ColorCount - 1)))
        {
            var hex = field.Text.Trim().TrimStart('#');
            if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _)) colors.Add(RgbColor.FromHex(hex));
            else if (rainbow) colors.Add(RgbEffects.DefaultsFor(option.Id).Colors[colors.Count]);
            else return null;
        }
        int low = 30, high = 95;
        if (option.TemperatureRange && (!int.TryParse(LowTemperature.Text, out low) || !int.TryParse(HighTemperature.Text, out high)
            || low < 0 || high > 120 || low >= high)) return null;
        return new(option.Id, colors, EffectSpeed.Value, EffectBrightness.Value,
            option.Direction && EffectDirection.SelectedIndex == 1 ? "backward" : "forward", low, high, rainbow);
    }

    private void UpdatePreview()
    {
        if (PreviewStrip is null) return;
        var option = RgbEffects.Options.First(entry => entry.Id == SelectedEffect);
        DeviceRainbowPalette.Visibility = option.SupportsRainbowPalette ? Visibility.Visible : Visibility.Collapsed;
        EffectDirection.Visibility = option.Direction ? Visibility.Visible : Visibility.Collapsed;
        var count = option.SupportsRainbowPalette && DeviceRainbowPalette.IsChecked == true ? 0 : option.ColorCount;
        SecondColorBox.Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        ThirdColorBox.Visibility = count >= 3 ? Visibility.Visible : Visibility.Collapsed;
        FourthColorBox.Visibility = count >= 4 ? Visibility.Visible : Visibility.Collapsed;
        TemperatureRange.Visibility = option.TemperatureRange ? Visibility.Visible : Visibility.Collapsed;
        EffectSpeed.IsEnabled = option.Speed;
        EffectBrightness.IsEnabled = option.Brightness;
        var showMusic = EffectControls.IsVisible && option.Id == RgbEffects.Music;
        if (EffectControls.IsVisible && (option.Speed || showMusic || option.Id == RgbEffects.BoardDefault)) _effectPreview.Start();
        else _effectPreview.Stop();
        if (_previewingAudio && !showMusic) _previewPeak.Dispose();
        _previewingAudio = showMusic;
        if (ReadDeviceEffect() is not { } effect)
        {
            PreviewStrip.Children.Clear();
            return;
        }
        var peak = showMusic ? _previewPeak.Read() : null;
        var frame = RgbEffectRenderer.Render(effect, 16, _previewClock.Elapsed.TotalSeconds, 45, audioLevel: peak);
        while (PreviewStrip.Children.Count < frame.Count) PreviewStrip.Children.Add(new Border { Margin = new Thickness(0.5, 0, 0.5, 0) });
        for (var i = 0; i < frame.Count; i++)
        {
            var color = Color.FromRgb(frame[i].R, frame[i].G, frame[i].B);
            var swatch = (Border)PreviewStrip.Children[i];
            if (swatch.Background is SolidColorBrush old && old.Color == color) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            swatch.Background = brush;
        }
    }
}
