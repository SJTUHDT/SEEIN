using Seein.Core;
using Seein.SmartHome;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;

namespace Seein.Tray;

public partial class XiaomiLampView : System.Windows.Controls.UserControl, IDisposable
{
    private readonly SmartHomeStore _store = new(AppPaths.UserDefault.DataRoot);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _pageLifetime;
    private CancellationTokenSource? _authorizationLifetime;
    private XiaomiLampClient? _client;
    private LampAuthorization? _authorization;
    private bool _busy, _connected, _loadingValues, _dirty, _initialized, _disposed;
    private readonly LampShortcutController _shortcuts;
    public event Action<string, bool>? ShortcutActionCompleted;

    private bool _ownsShortcuts;
    public XiaomiLampView():this(new LampShortcutController(new SmartHomeStore(AppPaths.UserDefault.DataRoot))){_ownsShortcuts=true;}
    internal XiaomiLampView(LampShortcutController shortcuts)
    {
        InitializeComponent();
        var settings = _store.LoadSettings();
        Address.Text = settings.LampAddress;
        _shortcuts = shortcuts;
        _shortcuts.ActionCompleted += (text, failed) =>
        {
            if (_disposed) return;
            ShortcutStatus.Text = text;
            ShortcutActionCompleted?.Invoke(text, failed);
            if (!failed && IsVisible && !_busy) _ = RunAsync(token => RefreshAsync(false, token));
        };
        SetShortcut(OnKey, _shortcuts.Profile.PowerOn);
        SetShortcut(OffKey, _shortcuts.Profile.PowerOff);
        SetShortcut(PresetKey, null);
        ReloadPresets();
        ShortcutStatus.Text = _shortcuts.Status;
        try { _authorization = _store.LoadAuthorization(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { StateText.Text = "本机授权无法读取，请重新扫码连接。"; }
        _initialized = true;
        LightValue_Changed(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
        _dirty = false;
        _timer.Tick += async (_, _) => { if (!_busy && _authorization is not null) await RunAsync(token => RefreshAsync(false, token)); };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_disposed)
            {
                _pageLifetime = new();
                _timer.Start();
                if (_authorization is not null) await RunAsync(token => ConnectAsync(_authorization, token));
                UpdateAvailability();
            }
            else StopPage();
        };
        UpdateAvailability();
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _disposed || !IsVisible || _pageLifetime is null) return;
        _busy = true;
        UpdateAvailability();
        try { await operation(_pageLifetime.Token); }
        catch (OperationCanceledException) { if (IsVisible) StateText.Text = "连接已取消。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _connected = false;
            StateText.Text = ex is ArgumentException or InvalidOperationException or TimeoutException or System.Security.Cryptography.CryptographicException ? ex.Message : "设备连接失败，请检查网络或重新扫码连接。";
            LampStateText.Text = "尚未取得有效设备状态";
        }
        finally { _busy = false; UpdateAvailability(); }
    }

    private async Task ConnectAsync(LampAuthorization authorization, CancellationToken token)
    {
        _connected = false;
        _client?.Dispose();
        _client = new(Address.Text.Trim(), authorization.Token);
        StateText.Text = "正在验证挂灯并读取状态…";
        await _client.VerifyModelAsync(token);
        await RefreshAsync(true, token);
    }

    private async Task RefreshAsync(bool updateDraft, CancellationToken token)
    {
        if (_client is null && _authorization is not null) { await ConnectAsync(_authorization, token); return; }
        if (_client is null) return;
        var state = await _client.ReadAsync(token);
        _connected = true;
        StateText.Text = "已连接米家显示器挂灯2 · " + Address.Text.Trim();
        LampStateText.Text = $"{(state.IsOn ? "已开灯" : "已关灯")} · 亮度 {state.Brightness}% · 色温 {state.ColorTemperature} K";
        if (updateDraft || !_dirty)
        {
            _loadingValues = true;
            Brightness.Value = state.Brightness;
            Temperature.Value = state.ColorTemperature;
            _loadingValues = false;
            _dirty = false;
        }
    }

    private async void SaveAddress_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var address = LocalDeviceAddress.Parse(Address.Text.Trim()).ToString();
        _store.SaveSettings(_store.LoadSettings() with { LampAddress = address });
        if (_authorization is null) { StateText.Text = "设备地址已保存，请扫码连接米家。"; return; }
        _authorization = new(_authorization.Name, _authorization.Model, address, _authorization.Token);
        _store.SaveAuthorization(_authorization);
        await ConnectAsync(_authorization, token);
    });

    private async void Authorize_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        LocalDeviceAddress.Parse(Address.Text.Trim());
        const string region = "cn";
        _store.SaveSettings(_store.LoadSettings() with { LampAddress = Address.Text.Trim(), Region = region });
        _authorizationLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var deadline = _authorizationLifetime;
        using var login = new XiaomiCloudLogin();
        try
        {
            CancelAuthorizationButton.Visibility = Visibility.Visible;
            SelectionPanel.Visibility = Visibility.Collapsed;
            StateText.Text = "正在获取小米登录二维码…";
            var qr = await login.BeginAsync(deadline.Token);
            using var image = new MemoryStream(qr.Image);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = image; bitmap.EndInit(); bitmap.Freeze();
            QrImage.Source = bitmap;
            AuthorizationPanel.Visibility = Visibility.Visible;
            StateText.Text = "请用米家 App 扫码，并在手机上确认登录。";
            var lamps = await login.CompleteAsync(qr, region, deadline.Token);
            if (lamps.Count == 0) throw new InvalidOperationException("此账号和地区下未找到显示器挂灯2，请确认设备已绑定米家。" );
            if (lamps.Count == 1) await SaveAndConnectAsync(lamps[0], token);
            else
            {
                LampSelection.ItemsSource = lamps;
                LampSelection.SelectedIndex = 0;
                SelectionPanel.Visibility = Visibility.Visible;
                StateText.Text = "请选择要在这台电脑上控制的挂灯。";
            }
        }
        finally
        {
            _authorizationLifetime = null;
            QrImage.Source = null;
            AuthorizationPanel.Visibility = CancelAuthorizationButton.Visibility = Visibility.Collapsed;
        }
    });

    private async Task SaveAndConnectAsync(LampAuthorization authorization, CancellationToken token)
    {
        var address = authorization.Address;
        try { LocalDeviceAddress.Parse(address); } catch (ArgumentException) { address = Address.Text.Trim(); }
        _authorization = new(authorization.Name, authorization.Model, address, authorization.Token);
        _store.SaveAuthorization(_authorization);
        _store.SaveSettings(_store.LoadSettings() with { LampAddress = address });
        Address.Text = address;
        LampSelection.ItemsSource = null;
        SelectionPanel.Visibility = Visibility.Collapsed;
        await ConnectAsync(_authorization, token);
    }

    private async void SelectLamp_Click(object sender, RoutedEventArgs e)
    {
        if (LampSelection.SelectedItem is LampAuthorization lamp) await RunAsync(token => SaveAndConnectAsync(lamp, token));
    }
    private void CancelAuthorization_Click(object sender, RoutedEventArgs e) => _authorizationLifetime?.Cancel();
    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            _store.ForgetAuthorization();
            _authorization = null;
            _client?.Dispose(); _client = null; _connected = false;
            LampSelection.ItemsSource = null;
            SelectionPanel.Visibility = Visibility.Collapsed;
            StateText.Text = "已解除这台电脑的授权，可重新扫码连接。";
            LampStateText.Text = "尚未连接";
            UpdateAvailability();
        }
        catch (IOException) { StateText.Text = "本机授权暂时无法移除，请稍后重试。"; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(token => RefreshAsync(true, token));
    private async void On_Click(object sender, RoutedEventArgs e) => await SetPowerAsync(true);
    private async void Off_Click(object sender, RoutedEventArgs e) => await SetPowerAsync(false);
    private Task SetPowerAsync(bool on) => RunAsync(async token => { if (_client is null) return; await _client.SetPowerAsync(on, token); await RefreshAsync(false, token); });
    private async void ApplyLight_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (_client is null) return;
        await _client.SetLightAsync((int)Brightness.Value, (int)Temperature.Value, token);
        _dirty = false;
        await RefreshAsync(true, token);
    });

    private void LightValue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || BrightnessText is null || TemperatureText is null) return;
        BrightnessText.Text = $"亮度 · {Brightness.Value:0}%";
        TemperatureText.Text = $"色温 · {Temperature.Value:0} K";
        if (!_loadingValues) _dirty = true;
    }

    private void UpdateAvailability()
    {
        AuthorizeButton.IsEnabled = SaveAddressButton.IsEnabled = ForgetButton.IsEnabled = Address.IsEnabled = !_busy;
        LightControls.IsEnabled = !_busy;
        ApplyLightButton.IsEnabled = OnButton.IsEnabled = OffButton.IsEnabled = !_busy && _connected;
        UpdatePresetButton.IsEnabled = DeletePresetButton.IsEnabled = PresetSelection.SelectedItem is LampPreset;
        ApplyPresetButton.IsEnabled = !_busy && _connected && PresetSelection.SelectedItem is LampPreset;
        PresetButtons.IsEnabled = !_busy && _connected;
        RefreshButton.IsEnabled = !_busy && _authorization is not null;
    }

    private void StopPage()
    {
        _timer.Stop();
        _authorizationLifetime?.Cancel();
        _pageLifetime?.Cancel(); _pageLifetime?.Dispose(); _pageLifetime = null;
        _client?.Dispose(); _client = null; _connected = false;
    }
    public void InitializeShortcuts(nint window) { _shortcuts.Initialize(window); ShortcutStatus.Text = _shortcuts.Status; }

    public bool HandleShortcut(nint id)
    {
        if (!_shortcuts.Matches(id)) return false;
        var recorder = new[] { PresetKey, OnKey, OffKey }.FirstOrDefault(box => IsVisible && box.IsKeyboardFocusWithin);
        if (recorder is not null)
        {
            SetShortcut(recorder, _shortcuts.ShortcutFor(id));
            ShortcutStatus.Text = "快捷键已录入，点击对应保存按钮后生效。";
        }
        else _shortcuts.Execute(id);
        return true;
    }

    private static void SetShortcut(TextBox box, LampShortcut? value)
    {
        var key = value ?? new(); box.Tag = key;
        box.Text = FormatShortcut(key);
    }

    private static string FormatShortcut(LampShortcut? value)
    {
        var key = value ?? new();
        if (key.IsEmpty) return "未设置";
        var parts = new List<string>();
        if ((key.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((key.Modifiers & 1) != 0) parts.Add("Alt");
        if ((key.Modifiers & 4) != 0) parts.Add("Shift");
        parts.Add(key.Key switch { 0x21 => "Page Up", 0x22 => "Page Down", _ => KeyInterop.KeyFromVirtualKey((int)key.Key).ToString() });
        return string.Join(" + ", parts);
    }

    private void Shortcut_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var value = key is Key.Back or Key.Delete ? new LampShortcut() : new((uint)Keyboard.Modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!value.IsValid) { ShortcutStatus.Text = LampShortcut.KeyHint; return; }
        SetShortcut((TextBox)sender, value);
        ShortcutStatus.Text = "快捷键已录入，点击对应保存按钮后生效。";
    }

    private void ReloadPresets(Guid? selected = null)
    {
        PresetSelection.ItemsSource = _shortcuts.Profile.Items;
        PresetSelection.SelectedItem = _shortcuts.Profile.Items.FirstOrDefault(p => p.Id == selected);
        PresetButtons.ItemsSource = _shortcuts.Profile.Items.Select(p => new PresetTile(p)).ToList();
        NoPresetsText.Visibility = _shortcuts.Profile.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (PresetSelection.SelectedItem is LampPreset preset)
        {
            PresetName.Text = preset.Name;
            SetShortcut(PresetKey, preset.Shortcut);
            Brightness.Value = preset.Brightness;
            Temperature.Value = preset.ColorTemperature;
            _dirty = true;
        }
        else { PresetName.Text = ""; SetShortcut(PresetKey, null); }
        UpdateAvailability();
    }

    private bool SaveProfile(LampControlProfile profile, Guid? selected = null)
    {
        var saved = _shortcuts.Save(profile);
        ShortcutStatus.Text = _shortcuts.Status;
        if (saved) ReloadPresets(selected);
        return saved;
    }

    private void SavePowerKeys_Click(object sender, RoutedEventArgs e) => SaveProfile(_shortcuts.Profile with { PowerOn = OnKey.Tag as LampShortcut, PowerOff = OffKey.Tag as LampShortcut }, (PresetSelection.SelectedItem as LampPreset)?.Id);
    private void AddPreset_Click(object sender, RoutedEventArgs e) => SavePreset(false);
    private void UpdatePreset_Click(object sender, RoutedEventArgs e) => SavePreset(true);
    private void SavePreset(bool update)
    {
        var old = PresetSelection.SelectedItem as LampPreset;
        if (update && old is null) return;
        var preset = new LampPreset(update ? old!.Id : Guid.NewGuid(), PresetName.Text.Trim(), (int)Brightness.Value, (int)Temperature.Value, PresetKey.Tag as LampShortcut);
        var items = _shortcuts.Profile.Items.ToList();
        if (update) items[items.FindIndex(p => p.Id == old!.Id)] = preset;
        else items.Add(preset);
        SaveProfile(_shortcuts.Profile with { Presets = items }, preset.Id);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetSelection.SelectedItem is LampPreset preset)
            SaveProfile(_shortcuts.Profile with { Presets = _shortcuts.Profile.Items.Where(p => p.Id != preset.Id).ToList() });
    }

    private async void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetSelection.SelectedItem is not LampPreset preset) return;
        await ApplyPresetAsync(preset);
    }

    private async void QuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: LampPreset preset } || _busy) return;
        PresetSelection.SelectedItem = preset;
        await ApplyPresetAsync(preset);
    }

    private async Task ApplyPresetAsync(LampPreset preset)
    {
        await RunAsync(async token =>
        {
            if (_client is null) return;
            await _client.SetLightAsync(preset.Brightness, preset.ColorTemperature, token);
            await _client.SetPowerAsync(true, token);
            _dirty = false;
            await RefreshAsync(true, token);
        });
    }

    private void PresetsLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PresetButtonsPanel is null || PresetButtonsColumn is null) return;
        var narrow = e.NewSize.Width < 800;
        PresetEditorColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(3, GridUnitType.Star);
        PresetButtonsColumn.Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Grid.SetColumn(PresetButtonsPanel, narrow ? 0 : 1);
        Grid.SetRow(PresetButtonsPanel, narrow ? 1 : 0);
        PresetButtonsPanel.Margin = narrow ? new Thickness(0, 18, 0, 0) : new Thickness(24, 0, 0, 0);
    }

    private sealed record PresetTile(LampPreset Preset)
    {
        public string Name => Preset.Name;
        public string ShortcutText => Preset.Shortcut is null or { IsEmpty: true } ? $"{Preset.Brightness}% · {Preset.ColorTemperature} K" : FormatShortcut(Preset.Shortcut);
        public string Details => $"{Preset}\n快捷键：{FormatShortcut(Preset.Shortcut)}";
        public string AccessibleName => "应用预设 " + Preset.Name;
    }

    public void Dispose() { _disposed = true; StopPage(); if(_ownsShortcuts)_shortcuts.Dispose(); }
}
