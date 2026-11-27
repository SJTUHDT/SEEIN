using Seein.Core.Displays;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Seein.Tray;

public partial class DisplayControlView : UserControl, IDisposable
{
    private readonly DisplayControlClient _client = new(Seein.Core.ProcessNames.Executable("display"));
    private readonly DisplayShortcutController _shortcut;
    private bool _ownsShortcut;
    private readonly DisplayInputAliases _inputAliases = new(Path.Combine(Seein.Core.AppPaths.UserDefault.DataRoot, "display-input-aliases.json"));
    private uint _shortcutKey, _shortcutModifiers;
    private bool _restoringDp1;
    public event Action<string, bool>? ShortcutActionCompleted;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Dictionary<DisplayFeature, uint> _pending = [];
    private readonly Dictionary<DisplayFeature, Action<DisplaySetting>> _updates = [];
    private CancellationTokenSource _page = new();
    private int _generation;
    private bool _loading, _sending, _disposed;
    private string? _settingsTarget, _settingsShape;
    private string? Target => (MonitorSelector.SelectedItem as DisplayTarget)?.Id;

    public DisplayControlView():this(new DisplayShortcutController()){_ownsShortcut=true;}
    internal DisplayControlView(DisplayShortcutController shortcut)
    {
        _shortcut=shortcut;
        InitializeComponent();
        _shortcutKey = _shortcut.Settings.Key;
        _shortcutModifiers = _shortcut.Settings.Modifiers;
        UpdateShortcutText();
        ShortcutFeedback.Text = _shortcut.Status;
        _debounce.Tick += async (_, _) => await FlushAsync();
        IsVisibleChanged += async (_, _) =>
        {
            InvalidateRequests();
            if (IsVisible) await RefreshAsync();
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    private void InvalidateRequests()
    {
        _generation++;
        _pending.Clear();
        _debounce.Stop();
        _page.Cancel();
        _page.Dispose();
        _page = new();
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(async () =>
    {
        if (_disposed) return;
        InvalidateRequests();
        ClearSettings();
        if (IsVisible) await RefreshAsync();
    });

    private async Task<DisplayResponse?> SendAsync(DisplayRequest request, int generation)
    {
        try
        {
            var response = await _client.SendAsync(request, _page.Token);
            return !_disposed && generation == _generation && IsVisible ? response : null;
        }
        catch (OperationCanceledException) { return null; }
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !IsVisible) return;
        var selected = Target ?? _shortcut.Settings.TargetId;
        InvalidateRequests();
        var generation = _generation;
        DisplayStatus.Text = "正在发现显示器…";
        var result = await SendAsync(new("list"), generation);
        if (result is null) return;
        _loading = true;
        MonitorSelector.ItemsSource = result.Targets ?? [];
        MonitorSelector.SelectedItem = result.Targets?.FirstOrDefault(m => m.Id == selected) ?? result.Targets?.FirstOrDefault();
        _loading = false;
        if (!result.Success || Target is null)
        {
            SettingsPanel.IsEnabled = false;
            DisplayStatus.Text = result.Error ?? "未发现可控制的实体显示器。请检查连接和DDC/CI开关。";
            return;
        }
        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        var target = Target;
        if (target is null) return;
        var generation = _generation;
        DisplayStatus.Text = "正在读取显示器设置…";
        SettingsPanel.IsEnabled = false;
        var result = await SendAsync(new("read", target), generation);
        if (result is null) return;
        DisplayStatus.Text = result.Success ? "已读取当前显示器支持的设置" : result.Error;
        if (!result.Success) return;
        ShowSettings(target, result.Settings ?? []);
        SettingsPanel.IsEnabled = true;
    }
    private void ShowSettings(string target, IReadOnlyList<DisplaySetting> settings)
    {
        var shape = System.Text.Json.JsonSerializer.Serialize(settings.Select(s => s with { Current = 0 }));
        if (_settingsTarget == target && _settingsShape == shape)
        {
            foreach (var setting in settings) if (_updates.TryGetValue(setting.Feature, out var update)) update(setting);
        }
        else
        {
            ClearSettings(); _updates.Clear();
            foreach (var setting in settings) AddSetting(setting);
            _settingsTarget = target; _settingsShape = shape;
        }
    }

    private void ClearSettings()
    {
        _settingsTarget = _settingsShape = null;
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(ShortcutCard);
    }

    private void AddSetting(DisplaySetting setting)
    {
        if (!setting.Supported) return;
        var card = new Border { CornerRadius = new(8), Padding = new(16), Margin = new(6, 0, 6, 12), BorderThickness = new(1), MinHeight = 94 };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush");
        var row = new Grid();
        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        row.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = setting.Label, Margin = new(0, 0, 0, 8) });
        FrameworkElement editor;
        if (DisplayFeatures.IsChoice(setting.Feature))
        {
            var inputTarget = Target;
            var choices = (setting.Options ?? new Dictionary<uint, string>()).Select(p => new Choice(p.Key,
                setting.Feature == DisplayFeature.Input && inputTarget is not null ? _inputAliases.GetLabel(inputTarget, p.Key) : p.Value)).ToList();
            if (choices.All(c => c.Value != setting.Current))
                choices.Add(new(setting.Current, $"当前：{DisplayFeatures.OptionLabel(setting.Feature, setting.Current)}（仅回读）"));
            var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), SelectedValue = setting.Current, MinWidth = 180 };
            System.Windows.Automation.AutomationProperties.SetName(combo, setting.Label);
            if (DisplayFeatures.RequiresExplicitAction(setting.Feature))
            {
                var panel = new DockPanel();
                var apply = new Button { Content = setting.Feature == DisplayFeature.Input ? "切换" : "应用", Margin = new(12, 0, 0, 0) };
                DockPanel.SetDock(apply, Dock.Right);
                apply.Click += async (_, _) =>
                {
                    if (combo.SelectedValue is uint value)
                    {
                        // Explicit commands are sent once, never replayed on reconnect.
                        apply.IsEnabled = false;
                        try { await WriteAsync(setting.Feature, value, _generation, Target); }
                        finally { apply.IsEnabled = true; }
                    }
                };
                panel.Children.Add(apply);
                if (setting.Feature == DisplayFeature.Input && inputTarget is not null)
                {
                    var rename = new Button { Content = "昵称", ToolTip = "重命名所选输入源的本地昵称", Margin = new(8, 0, 0, 0) };
                    System.Windows.Automation.AutomationProperties.SetName(rename, "重命名输入源昵称");
                    DockPanel.SetDock(rename, Dock.Right);
                    rename.Click += (_, _) =>
                    {
                        if (combo.SelectedValue is not uint input) return;
                        var monitorName = (MonitorSelector.SelectedItem as DisplayTarget)?.Name ?? "显示器";
                        var dialog = new DisplayInputAliasWindow(monitorName, DisplayFeatures.OptionLabel(DisplayFeature.Input, input),
                            _inputAliases.GetAlias(inputTarget, input), alias => _inputAliases.SetAlias(inputTarget, input, alias))
                            { Owner = Window.GetWindow(this) };
                        if (dialog.ShowDialog() != true) return;
                        choices = choices.Select(c => new Choice(c.Value, _inputAliases.GetLabel(inputTarget, c.Value))).ToList();
                        // Only labels change. Keep the selected VCP value and never send a hardware command here.
                        var wasLoading = _loading;
                        _loading = true;
                        try { combo.ItemsSource = choices; combo.SelectedValue = input; }
                        finally { _loading = wasLoading; }
                        DisplayStatus.Text = "本地昵称已保存";
                    };
                    panel.Children.Add(rename);
                }
                panel.Children.Add(combo);
                editor = panel;
            }
            else
            {
                combo.SelectionChanged += (_, _) => { if (!_loading && combo.SelectedValue is uint value) Queue(setting.Feature, value); };
                editor = combo;
            }
            _updates[setting.Feature] = s => { _loading = true; combo.SelectedValue = s.Current; _loading = false; };
        }
        else
        {
            var panel = new DockPanel();
            var number = new TextBlock { Text = $"{setting.Current} / {setting.Maximum}", MinWidth = 82, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(number, Dock.Right);
            var slider = new Slider { Minimum = 0, Maximum = setting.Maximum, Value = setting.Current, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
            System.Windows.Automation.AutomationProperties.SetName(slider, setting.Label);
            slider.ValueChanged += (_, _) =>
            {
                number.Text = $"{slider.Value:0} / {setting.Maximum}";
                if (!_loading) Queue(setting.Feature, (uint)Math.Round(slider.Value));
            };
            _updates[setting.Feature] = s => { _loading = true; slider.Value = s.Current; _loading = false; };
            panel.Children.Add(number);
            panel.Children.Add(slider);
            editor = panel;
        }
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(editor, 1);
        row.Children.Add(editor);
        card.Child = row;
        SettingsPanel.Children.Add(card);
    }

    private void Queue(DisplayFeature feature, uint value)
    {
        _pending[feature] = value;
        _debounce.Start();
    }

    private async Task FlushAsync()
    {
        if (_sending || _pending.Count == 0) { if (_pending.Count == 0) _debounce.Stop(); return; }
        _sending = true;
        var generation = _generation;
        var target = Target;
        try
        {
            var next = _pending.First();
            _pending.Remove(next.Key);
            await WriteAsync(next.Key, next.Value, generation, target);
        }
        finally { _sending = false; }
    }

    private async Task WriteAsync(DisplayFeature feature, uint value, int generation, string? target)
    {
        if (target is null || generation != _generation) return;
        DisplayStatus.Text = $"正在设置{DisplayFeatures.Labels[feature]}…";
        var result = await SendAsync(new("set", target, feature, value), generation);
        if (result is null) return;
        DisplayStatus.Text = result.Success ? $"{DisplayFeatures.Labels[feature]}已应用并回读确认" : result.Error;
        if (!_pending.ContainsKey(feature) && result.Success && result.Settings?.FirstOrDefault() is { } actual && _updates.TryGetValue(feature, out var update)) update(actual);
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    public void InitializeShortcut(nint window)
    {
        _shortcut.Initialize(window);
        ShortcutFeedback.Text = _shortcut.Status;
    }

    public bool HandleShortcut(nint id)
    {
        if (!_shortcut.Matches(id)) return false;
        if (ShortcutKeyBox.IsKeyboardFocusWithin) return true;
        if (!_restoringDp1) _ = RestoreDp1Async();
        return true;
    }

    private async Task RestoreDp1Async()
    {
        _restoringDp1 = true;
        InvalidateRequests(); // Cancel earlier page writes before the explicit recovery command.
        ShortcutFeedback.Text = "正在向已保存的显示器发送 DP1 指令…";
        try
        {
            var result = await _shortcut.RestoreDp1Async();
            if (_disposed) return;
            var text = result.Success ? "已切回 DP1，并回读确认" : result.Error ?? "DP1切换失败";
            ShortcutFeedback.Text = text;
            ShortcutActionCompleted?.Invoke(text, !result.Success);
            if (IsVisible) await RefreshAsync();
        }
        finally { _restoringDp1 = false; }
    }

    private void ShortcutKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var modifiers = Keyboard.Modifiers;
        var candidate = new DisplayShortcutSettings("capture", (uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!candidate.IsValid) { ShortcutFeedback.Text = "请使用含 Ctrl 或 Alt 的字母、数字或功能键组合，避开 Win 和 F12。"; return; }
        _shortcutKey = candidate.Key;
        _shortcutModifiers = candidate.Modifiers;
        UpdateShortcutText();
        ShortcutFeedback.Text = "点击「保存启用」后生效";
    }

    private void UpdateShortcutText()
    {
        var parts = new List<string>();
        if ((_shortcutModifiers & 2) != 0) parts.Add("Ctrl");
        if ((_shortcutModifiers & 1) != 0) parts.Add("Alt");
        if ((_shortcutModifiers & 4) != 0) parts.Add("Shift");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)_shortcutKey).ToString());
        ShortcutKeyBox.Text = string.Join(" + ", parts);
    }

    private void SaveShortcutClicked(object sender, RoutedEventArgs e)
    {
        _shortcut.Save(new(Target, _shortcutModifiers, _shortcutKey, true));
        ShortcutFeedback.Text = _shortcut.Status;
    }

    private void DisableShortcutClicked(object sender, RoutedEventArgs e)
    {
        _shortcut.Save(_shortcut.Settings with { Enabled = false, TargetId = _shortcut.Settings.TargetId ?? Target });
        ShortcutFeedback.Text = _shortcut.Status;
    }
    private async void MonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        InvalidateRequests();
        await ReadAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        InvalidateRequests();
        _page.Dispose();
        _client.Dispose();
        if(_ownsShortcut)_shortcut.Dispose();
    }

    private sealed record Choice(uint Value, string Label);
}
