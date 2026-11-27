using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Seein.Core;
using Seein.Devices.CapturePlayback;
using Seein.Tray.CapturePlayback;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;

namespace Seein.Tray;

public partial class CapturePlaybackView : UserControl, IDisposable
{
    private readonly CaptureProfileStore _store = new(Path.Combine(AppPaths.UserDefault.DataRoot, "devices", "capture-playback.json"));
    private readonly CaptureSession _session = new();
    private readonly DispatcherTimer _timer;
    private readonly CaptureVideoHost _video;
    private CaptureProfile _profile;
    private CaptureVideoDevice[] _devices = [];
    private Window? _playerWindow;
    private bool _updating = true, _refreshing, _disposed, _changingPlayback, _fullscreen, _returning;
    private long _lastReceived, _lastPresented, _lastSample = Stopwatch.GetTimestamp();
    private WindowState _previousState;
    private WindowStyle _previousStyle;
    private ResizeMode _previousResize;
    private readonly bool _discoverOnShow;

    public CapturePlaybackView() : this(true) { }
    internal CapturePlaybackView(bool discoverOnShow)
    {
        _discoverOnShow = discoverOnShow;
        _profile = _store.Load() with { Volume = 100, Muted = false };
        InitializeComponent();
        _video = new CaptureVideoHost(_session.Renderer);
        _video.PlaybackKeyPressed += key => { if (key == 0x7A) ToggleFullscreen(); else ExitFullscreen(); };
        VideoContent.Content = _video;
        _updating = false;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        IsVisibleChanged += (_, _) => { UpdateVisibility(); if (_discoverOnShow && IsVisible && _devices.Length == 0 && !_refreshing) _ = RefreshDevicesAsync(); };
        PreviewKeyDown += PlayerKeyDown;
        UpdateStatus();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshing || _disposed || _session.IsActive) return;
        _refreshing = true; RefreshButton.IsEnabled = false;
        DeviceHint.Text = "正在读取设备与支持的视频模式…";
        try
        {
            var result = await Task.Run(() => (Video: CaptureSession.EnumerateVideo(),
                Audio: CaptureAudio.Enumerate(DataFlow.Capture), Output: CaptureAudio.Enumerate(DataFlow.Render)));
            if (_disposed) return;
            _updating = true;
            _devices = result.Video;
            VideoSelector.ItemsSource = _devices;
            VideoSelector.SelectedItem = _devices.FirstOrDefault(d => d.Id == _profile.VideoId)
                ?? (_profile.VideoId.Length == 0 ? _devices.FirstOrDefault(d => d.Name.Contains("UGREEN 45647", StringComparison.OrdinalIgnoreCase)) : null);
            var audio = result.Audio.ToList(); var output = result.Output.ToList();
            if (_profile.AudioId.Length > 0 && !audio.Any(e => e.Id == _profile.AudioId)) audio.Add(new(_profile.AudioId, "原音频输入暂时离线"));
            if (_profile.OutputId.Length > 0 && !output.Any(e => e.Id == _profile.OutputId)) output.Add(new(_profile.OutputId, "原声音输出暂时离线"));
            AudioSelector.ItemsSource = new[] { new CaptureEndpoint("", "不播放音频") }.Concat(audio).ToArray();
            OutputSelector.ItemsSource = new[] { new CaptureEndpoint("", "系统默认输出") }.Concat(output).ToArray();
            OutputSelector.SelectedItem = ((CaptureEndpoint[])OutputSelector.ItemsSource).FirstOrDefault(e => e.Id == _profile.OutputId)
                ?? ((CaptureEndpoint[])OutputSelector.ItemsSource)[0];
            PopulateModes();
            var video = VideoSelector.SelectedItem as CaptureVideoDevice;
            AudioSelector.SelectedItem = audio.FirstOrDefault(e => e.Id == _profile.AudioId)
                ?? (!_profile.AudioSelectionSaved && video != null ? CapturePolicy.MatchAudio(video.Name, result.Audio) : null)
                ?? ((CaptureEndpoint[])AudioSelector.ItemsSource)[0];
            DeviceHint.Text = video != null ? "已读取设备。选择自动模式时优先使用 1440p120，实际模式会在播放后显示。"
                : "请选择视频输入；未自动选中其他摄像头。若未发现采集卡，请检查 USB 连接后刷新。";
            _updating = false;
            SaveSettings();
        }
        catch (Exception ex) { if (!_disposed) DeviceHint.Text = "读取设备失败：" + ex.Message; }
        finally { _updating = false; _refreshing = false; if (!_disposed) RefreshButton.IsEnabled = !_session.IsActive; }
    }

    private sealed record ModeOption(string Key, string Name);
    private void PopulateModes()
    {
        var modes = (VideoSelector.SelectedItem as CaptureVideoDevice)?.Modes ?? [];
        var choices = new[] { new ModeOption("", "自动 · 优先 1440p120") }.Concat(modes.Select(m => new ModeOption(m.Key, m.Label))).ToList();
        if (_profile.ModeKey.Length > 0 && !choices.Any(m => m.Key == _profile.ModeKey))
            choices.Add(new(_profile.ModeKey, "原视频模式不可用 · 请重新选择"));
        var options = choices.ToArray();
        ModeSelector.ItemsSource = options;
        ModeSelector.SelectedItem = options.FirstOrDefault(m => m.Key == _profile.ModeKey) ?? options[0];
    }
    private void VideoChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _disposed) return;
        _updating = true;
        _profile = _profile with { ModeKey = "", AudioId = "", AudioSelectionSaved = false };
        PopulateModes();
        var endpoints = (AudioSelector.ItemsSource as CaptureEndpoint[]) ?? [];
        AudioSelector.SelectedItem = CapturePolicy.MatchAudio((VideoSelector.SelectedItem as CaptureVideoDevice)?.Name ?? "", endpoints)
            ?? endpoints.FirstOrDefault();
        _updating = false;
        SaveSettings();
    }
    private void SettingsChanged(object sender, SelectionChangedEventArgs e) { if (!_updating) SaveSettings(); }
    private void SaveSettings()
    {
        if (_updating || _disposed) return;
        _profile = _profile with
        {
            VideoId = (VideoSelector.SelectedItem as CaptureVideoDevice)?.Id ?? _profile.VideoId,
            AudioId = (AudioSelector.SelectedItem as CaptureEndpoint)?.Id ?? _profile.AudioId,
            AudioSelectionSaved = AudioSelector.SelectedItem != null || _profile.AudioSelectionSaved,
            OutputId = (OutputSelector.SelectedItem as CaptureEndpoint)?.Id ?? _profile.OutputId,
            ModeKey = (ModeSelector.SelectedItem as ModeOption)?.Key ?? _profile.ModeKey,
            Volume = 100, Muted = false
        };
        try { _store.Save(_profile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DeviceHint.Text = "设置保存失败：" + ex.Message; }
    }

    private async void PlayClicked(object sender, RoutedEventArgs e)
    {
        if (_changingPlayback || _disposed) return;
        _changingPlayback = true; PlayButton.IsEnabled = false;
        try
        {
            if (_session.IsActive) await _session.StopAsync();
            else
            {
                SaveSettings();
                if (_profile.VideoId.Length == 0) { DeviceHint.Text = "请先选择采集卡的视频输入。"; return; }
                _session.Start(_profile);
                SettingsExpander.IsExpanded = false;
            }
            UpdateStatus();
        }
        finally { _changingPlayback = false; if (!_disposed) PlayButton.IsEnabled = true; }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private void UpdateStatus()
    {
        if (_disposed) return;
        var playing = _session.IsActive;
        PlayButton.Content = playing ? "停止播放" : "开始播放";
        VideoSelector.IsEnabled = AudioSelector.IsEnabled = OutputSelector.IsEnabled = ModeSelector.IsEnabled = !playing && !_refreshing;
        RefreshButton.IsEnabled = !playing && !_refreshing;
        var hasFrames = playing && _session.ActiveMode != null;
        VideoContent.Visibility = hasFrames ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = hasFrames ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Text = playing ? "正在连接采集卡…" : "Switch 2 · 等待开始播放";
        PlaybackStatus.Text = _session.Status;
        var now = Stopwatch.GetTimestamp(); var seconds = Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds;
        var received = _session.Received; var presented = _session.Renderer.Presented;
        if (_session.ActiveMode is { } mode)
            PlaybackMetrics.Text = $"{mode.Label}  |  接收 {Math.Max(0, received - _lastReceived) / Math.Max(.01, seconds):0.0} fps · 呈现 {Math.Max(0, presented - _lastPresented) / Math.Max(.01, seconds):0.0} fps  |  最新帧处理 {_session.Renderer.LatencyMs:0.0} ms  |  排队丢帧 {_session.Renderer.Dropped} · 异常帧 {_session.Renderer.Errors}";
        else PlaybackMetrics.Text = "接收帧率与呈现帧率单独测量；处理耗时不包含采集卡自身延迟。";
        AudioStatus.Text = _session.AudioStatus + (_session.Renderer.Failure is { } error ? "  |  画面处理：" + error : "");
        _lastSample = now; _lastReceived = received; _lastPresented = presented;
        UpdateVisibility();
    }
    private void UpdateVisibility() => _session.Renderer.SetVisible(!_disposed && VideoFrame.IsVisible);

    private void DetachClicked(object sender, RoutedEventArgs e) { if (_playerWindow == null) OpenPlayer(); else ReturnToPage(); }
    private void ShowPlayerClicked(object sender, RoutedEventArgs e)
    {
        if (_playerWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = _fullscreen ? WindowState.Maximized : WindowState.Normal;
        window.Show(); window.Activate();
    }
    private void RecallPlayerClicked(object sender, RoutedEventArgs e) => ReturnToPage();
    internal void OpenPlayer(bool showWindow = true)
    {
        if (_playerWindow != null || _disposed) return;
        VideoArea.Children.Remove(VideoFrame);
        // The inline card stroke must not become a bright one-pixel outline on video.
        VideoFrame.BorderThickness = new Thickness(0);
        VideoFrame.BorderBrush = System.Windows.Media.Brushes.Black;
        DetachedHint.Visibility = Visibility.Visible;
        _playerWindow = new Window
        {
            Title = "SEEIN · 采集播放", Width = _profile.WindowWidth, Height = _profile.WindowHeight,
            MinWidth = 680, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = VideoFrame, Background = System.Windows.Media.Brushes.Black,
            BorderBrush = System.Windows.Media.Brushes.Black, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), SnapsToDevicePixels = true, UseLayoutRounding = true
        };
        _playerWindow.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        if (_profile.WindowLeft is { } left && _profile.WindowTop is { } top &&
            new Rect(left, top, _profile.WindowWidth, _profile.WindowHeight).IntersectsWith(new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)))
        {
            _playerWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _playerWindow.Left = left; _playerWindow.Top = top;
        }
        _playerWindow.PreviewKeyDown += PlayerKeyDown;
        _playerWindow.StateChanged += (_, _) => UpdateVisibility();
        _playerWindow.IsVisibleChanged += (_, _) => UpdateVisibility();
        _playerWindow.Closed += (_, _) => { if (!_disposed && !_returning) ReturnToPage(true); };
        DetachButton.Visibility = InlineFullscreenButton.Visibility = Visibility.Collapsed;
        if (showWindow) _playerWindow.Show();
        UpdateVisibility();
    }
    internal void ReturnToPage(bool alreadyClosed = false)
    {
        var window = _playerWindow;
        if (window == null) return;
        _returning = true;
        if (!_fullscreen && window.WindowState == WindowState.Normal)
            _profile = _profile with { WindowWidth = window.Width, WindowHeight = window.Height, WindowLeft = window.Left, WindowTop = window.Top };
        window.Content = null;
        _playerWindow = null; _fullscreen = false;
        if (!alreadyClosed) window.Close();
        if (!_disposed)
        {
            VideoArea.Children.Add(VideoFrame);
            VideoFrame.BorderThickness = new Thickness(1);
            VideoFrame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush");
            DetachedHint.Visibility = Visibility.Collapsed;
            DetachButton.Visibility = InlineFullscreenButton.Visibility = Visibility.Visible;
            DetachedFullscreenButton.Content = "窗口全屏";
            SaveSettings(); UpdateVisibility();
        }
        _returning = false;
    }
    private void FullscreenClicked(object sender, RoutedEventArgs e) => ToggleFullscreen();
    internal void ToggleFullscreen()
    {
        if (_playerWindow == null) OpenPlayer();
        if (_playerWindow == null) return;
        if (_fullscreen) ExitFullscreen();
        else
        {
            _previousState = _playerWindow.WindowState; _previousStyle = _playerWindow.WindowStyle; _previousResize = _playerWindow.ResizeMode;
            _playerWindow.WindowState = WindowState.Normal; _playerWindow.WindowStyle = WindowStyle.None;
            _playerWindow.ResizeMode = ResizeMode.NoResize; _playerWindow.WindowState = WindowState.Maximized;
            _fullscreen = true;
            DetachedFullscreenButton.Content = "退出全屏";
        }
    }
    private void ExitFullscreen()
    {
        if (_playerWindow == null || !_fullscreen) return;
        _playerWindow.WindowState = WindowState.Normal; _playerWindow.WindowStyle = _previousStyle;
        _playerWindow.ResizeMode = _previousResize; _playerWindow.WindowState = _previousState; _fullscreen = false;
        DetachedFullscreenButton.Content = "窗口全屏";
    }
    private void PlayerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;
        if (Keyboard.FocusedElement is ComboBox or System.Windows.Controls.TextBox) return;
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullscreen) { ExitFullscreen(); e.Handled = true; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        SaveSettings(); _disposed = true; _timer.Stop();
        if (_playerWindow != null) { _returning = true; _playerWindow.Content = null; _playerWindow.Close(); _playerWindow = null; }
        _video.Dispose();
        // Device shutdown is awaited by the module before disposing this control.
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    public Task StopPlaybackAsync() => _session.StopAsync();
}
