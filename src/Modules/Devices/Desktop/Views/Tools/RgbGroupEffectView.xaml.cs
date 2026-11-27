using Seein.Core.Lighting;
using Seein.Core.Sensors;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Seein.Tray;

public partial class RgbGroupEffectView : System.Windows.Controls.UserControl
{
    private readonly Dictionary<string, RgbEffectSettings> _drafts = [];
    private readonly Stopwatch _previewClock = Stopwatch.StartNew();
    private readonly PlaybackPeakReader _previewPeak = new();
    private readonly Border[] _swatches;
    private RgbSettings _settings = RgbEffects.DefaultSettings();
    private RgbEffectOption? _option;
    private RgbEffectSettings? _draft;
    private bool _loadedSettings;
    private bool _updating = true;
    private bool _busy;
    private string? _groupPlayback;
    private double? _serviceMusicPeak;
    private IReadOnlyList<RgbDeviceInfo> _hardwareLights = [];
    private IReadOnlyList<RgbDeviceInfo> _devices = [];
    private IReadOnlyDictionary<RgbColor, RgbColor> _caseAppearance = new Dictionary<RgbColor, RgbColor>();
    private IReadOnlyList<RgbProfile> _groupProfiles = [];
    private TextBox[] ColorFields => [PrimaryColor, SecondaryColor, ThirdColor, FourthColor];

    public event Action<RgbCommandRequest>? CommandRequested;

    public RgbGroupEffectView()
    {
        InitializeComponent();
        _swatches = Enumerable.Range(0, 18).Select(_ => new Border { Margin = new Thickness(0, 0, 1, 0) }).ToArray();
        foreach (var swatch in _swatches) Preview.Children.Add(swatch);
        EffectList.ItemsSource = RgbEffects.Options;
        _updating = false;
        SetDraft(RgbEffects.DefaultsFor(RgbEffects.Static));
        var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        previewTimer.Tick += (_, _) => DrawPreview();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) previewTimer.Start();
            else
            {
                previewTimer.Stop();
                _previewPeak.Dispose();
            }
        };
    }

    internal void LoadState(RgbSettings settings, RgbStatus status)
    {
        _settings = settings;
        _groupPlayback = status.GroupPlayback;
        _serviceMusicPeak = status.MusicPeak;
        _groupProfiles = settings.Profiles?.Where(RgbEffects.IsGroupProfile).ToArray() ?? [];
        _devices = status.Devices.Select(device => device.Info).ToArray();
        _caseAppearance = RgbAppearanceProfile.ReadCase(_devices);
        _hardwareLights = _devices
            .Where(device => RgbEffects.GroupExternalInput(device, settings) is not null ||
                (!device.SupportsDirect && device.CanSetSolidColor)).ToArray();
        _updating = true;
        EnableToggle.IsChecked = settings.Enabled;
        _updating = false;
        if (!_loadedSettings) LoadActiveDraft();
        else if (_draft is { } draft) DrawColorSwatches(draft.Colors);
        _loadedSettings = true;
        UpdateButtons();
    }

    internal void LoadActiveDraft()
    {
        var effect = _settings.GroupEffect ?? _settings.Profiles?.FirstOrDefault(p => p.Id == _settings.ActiveProfileId)?.Effect;
        if (RgbEffects.NormalizeGroupEffect(effect) is { } normalized) SetDraft(normalized);
    }

    internal void SetBusy(bool busy) { _busy = busy; UpdateButtons(); }

    private void SetDraft(RgbEffectSettings effect)
    {
        var option = RgbEffects.Options.First(entry => entry.Id == effect.EffectId);
        if (_option?.Id == RgbEffects.Music && option.Id != RgbEffects.Music)
            _previewPeak.Dispose();
        _updating = true;
        _option = option;
        EffectList.SelectedItem = option;
        EffectTitle.Text = option.Name;
        RainbowPalette.Visibility = VisibleWhen(option.SupportsRainbowPalette);
        RainbowPalette.IsChecked = effect.UseRainbowPalette;
        UseCalibration.IsChecked = effect.UseZoneCalibration;
        MusicHint.Visibility = VisibleWhen(option.Id == RgbEffects.Music);
        SpeedRow.Visibility = VisibleWhen(option.Speed);
        BrightnessRow.Visibility = VisibleWhen(option.Brightness);
        DirectionRow.Visibility = VisibleWhen(option.Direction);
        TemperatureRow.Visibility = VisibleWhen(option.TemperatureRange);
        (PrimaryLabel.Text, SecondaryLabel.Text) = option.Id switch
        {
            RgbEffects.Temperature => ("低温颜色", "中温颜色"),
            RgbEffects.Fire => ("火焰底色", "火焰高光"),
            RgbEffects.ColorCycle => ("起始颜色", "循环颜色"),
            _ when option.ColorCount > 1 => ("颜色 1", "颜色 2"),
            _ => ("颜色", "")
        };
        ThirdLabel.Text = option.TemperatureRange ? "高温颜色" : "颜色 3";
        var fields = ColorFields;
        for (var index = 0; index < fields.Length; index++)
            fields[index].Text = index < effect.Colors.Count ? effect.Colors[index].ToHex() : "";
        Speed.Value = effect.Speed;
        Brightness.Value = effect.Brightness;
        Direction.SelectedIndex = effect.Direction == "backward" ? 1 : 0;
        LowTemperature.Text = effect.LowTemperature.ToString(CultureInfo.InvariantCulture);
        HighTemperature.Text = effect.HighTemperature.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        UpdateDraft();
        _previewClock.Restart();
    }

    private void Effect_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updating && EffectList.SelectedItem is RgbEffectOption option)
            SetDraft(_drafts.GetValueOrDefault(option.Id) ?? RgbEffects.DefaultsFor(option.Id));
    }

    private void Parameter_Changed(object sender, RoutedEventArgs e) { if (!_updating) UpdateDraft(); }

    private void UpdateDraft()
    {
        if (_option is not { } option) return;
        var rainbow = option.SupportsRainbowPalette && RainbowPalette.IsChecked == true;
        var editableColors = rainbow ? 0 : option.ColorCount;
        PrimaryRow.Visibility = VisibleWhen(editableColors >= 1);
        SecondaryRow.Visibility = VisibleWhen(editableColors >= 2);
        ThirdRow.Visibility = VisibleWhen(editableColors >= 3);
        FourthRow.Visibility = VisibleWhen(editableColors >= 4);
        var colors = new List<RgbColor>();
        string? error = null;
        foreach (var field in ColorFields.Take(option.ColorCount))
        {
            if (TryReadColor(field.Text, out var color)) colors.Add(color);
            else if (rainbow) colors.Add(RgbEffects.DefaultsFor(option.Id).Colors[colors.Count]);
            else { error = "请输入有效的 HEX 颜色，例如 #0067C0。"; break; }
        }
        var low = 30;
        var high = 95;
        if (option.TemperatureRange && (!int.TryParse(LowTemperature.Text, out low) ||
            !int.TryParse(HighTemperature.Text, out high) || low < 0 || high > 120 || low >= high))
            error = "温度范围须在 0–120 °C 之间，低温须小于高温。";

        _draft = error is null
            ? new(option.Id, colors, Speed.Value, Brightness.Value, Direction.SelectedIndex == 1 ? "backward" : "forward", low, high,
                UseRainbowPalette: rainbow, UseZoneCalibration: UseCalibration.IsChecked == true)
            : null;
        if (_draft is { } draft) _drafts[option.Id] = draft;
        ValidationText.Text = error ?? "";
        DrawColorSwatches(colors);
        UpdateButtons();
        DrawPreview();
    }

    private void DrawColorSwatches(IReadOnlyList<RgbColor> colors)
    {
        Border[] swatches = [PrimarySwatch, SecondarySwatch, ThirdSwatch, FourthSwatch];
        for (var index = 0; index < colors.Count; index++)
            swatches[index].Background = ColorBrush(CasePreview(colors[index]));
    }

    private RgbColor CasePreview(RgbColor input) => _caseAppearance.GetValueOrDefault(input, input);

    internal static bool TryReadColor(string? hex, out RgbColor color)
    {
        var text = hex?.Trim().TrimStart('#');
        if (text is { Length: 6 } && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            color = RgbColor.FromHex(text);
            return true;
        }
        color = default;
        return false;
    }

    private void DrawPreview()
    {
        if (_draft is null) return;
        var time = _previewClock.Elapsed.TotalSeconds;
        var previewTemperature = 62.5 + Math.Sin(time * 0.8) * 32.5;
        var peak = IsVisible && _draft.EffectId == RgbEffects.Music ? _previewPeak.Read() : null;
        var colors = RgbEffectRenderer.Render(_draft, _swatches.Length, time, previewTemperature, audioLevel: peak);
        for (var i = 0; i < colors.Count; i++)
        {
            var display = _draft is { EffectId: RgbEffects.Static, Brightness: 100 } ? CasePreview(colors[i]) : colors[i];
            var color = Color.FromRgb(display.R, display.G, display.B);
            if (_swatches[i].Background is not SolidColorBrush brush || brush.Color != color)
                _swatches[i].Background = new SolidColorBrush(color);
        }
    }

    private static Brush ColorBrush(RgbColor color) => new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    private static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = SaveButton.IsEnabled = !_busy && _draft is not null;
        LoadButton.IsEnabled = !_busy && _groupProfiles.Count > 0;
        EnableToggle.IsEnabled = !_busy;
        UseCalibration.Visibility = VisibleWhen(_settings.ZoneCalibrations is { Count: > 0 } &&
            _option is { ColorCount: > 0 } && !(_option.SupportsRainbowPalette && RainbowPalette.IsChecked == true));
        var effect = _option?.Id;
        MusicHint.Text = _serviceMusicPeak is double peak
            ? $"播放服务最近一次采样的峰值为 {peak:P0}。"
            : "播放声音后，灯光会跟随默认播放设备的音量起伏。";
        var modes = _hardwareLights.Select(device => RgbEffects.GroupExternalInput(device, _settings) is { } input
            ? $"{device.Name} · 整机灯效使用 {input.Name}（主板 ARGB 同步）"
            : effect is null or RgbEffects.Static or RgbEffects.Off ? null
            : RgbEffects.NativeAnimation(device, effect) is { } native
                ? $"{device.Name} · 原生 {native.Name}，使用硬件配色" : $"{device.Name} · 固定配色");
        HardwareHint.Text = string.Join(Environment.NewLine,
            modes.Prepend(_groupPlayback is null ? null : $"当前播放：{_groupPlayback}")
                .Where(message => !string.IsNullOrWhiteSpace(message)));
        HardwareHint.Visibility = VisibleWhen(!string.IsNullOrWhiteSpace(HardwareHint.Text));
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var field = ((sender as Button)?.Tag as string) switch
        {
            "secondary" => SecondaryColor,
            "third" => ThirdColor,
            "fourth" => FourthColor,
            _ => PrimaryColor
        };
        TryReadColor(field.Text, out var color);
        var owner = Window.GetWindow(this);
        var dialog = new RgbColorPickerWindow(color, _caseAppearance) { Owner = owner };
        if (dialog.ShowDialog() == true) field.Text = dialog.SelectedColor.ToHex();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(new("rescan"));
    private void Enable_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updating) CommandRequested?.Invoke(new(EnableToggle.IsChecked == true ? "enable" : "disable"));
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is { } draft) CommandRequested?.Invoke(new("groupEffect", GroupEffect: draft));
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_option is { } option) SetDraft(RgbEffects.DefaultsFor(option.Id));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is not { } draft) return;
        var input = new TextBox { Text = _option!.Name, MaxLength = 64, MinWidth = 275 };
        System.Windows.Automation.AutomationProperties.SetName(input, "配置名称");
        var dialog = CreateDialog(this, "保存灯效配置", "配置名称", input, "保存", () => !string.IsNullOrWhiteSpace(input.Text));
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() == true)
            CommandRequested?.Invoke(new("saveGroupProfile", GroupEffect: draft, ProfileName: input.Text.Trim()));
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var profile = ChooseProfile(this, "载入整机配置", _groupProfiles, _settings.ActiveProfileId);
        if (profile is not null) CommandRequested?.Invoke(new("loadGroupProfile", ProfileId: profile.Id));
    }

    internal static RgbProfile? ChooseProfile(FrameworkElement owner, string title,
        IReadOnlyList<RgbProfile> profiles, string selectedId)
    {
        if (profiles.Count == 0) return null;
        var input = new System.Windows.Controls.ComboBox
        {
            ItemsSource = profiles, DisplayMemberPath = nameof(RgbProfile.Name), MinWidth = 275,
            SelectedValuePath = nameof(RgbProfile.Id), SelectedValue = selectedId
        };
        if (input.SelectedIndex < 0) input.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(input, "已保存的灯效配置");
        var dialog = CreateDialog(owner, title, "已保存的配置", input, "载入", () => input.SelectedItem is RgbProfile);
        dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.SelectedItem as RgbProfile : null;
    }

    private static Window CreateDialog(FrameworkElement owner, string title, string label, FrameworkElement input, string action, Func<bool> accept)
    {
        var dialog = new Window
        {
            Title = title, Owner = Window.GetWindow(owner), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "WindowBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(input);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var submit = new Button { Content = action, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        submit.SetResourceReference(StyleProperty, "AccentButtonStyle");
        submit.Click += (_, _) => { if (accept()) dialog.DialogResult = true; else input.Focus(); };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true });
        actions.Children.Add(submit);
        content.Children.Add(actions);
        dialog.Content = content;
        return dialog;
    }
}
