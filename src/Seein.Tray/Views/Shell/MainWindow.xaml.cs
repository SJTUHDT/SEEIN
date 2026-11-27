using Seein.Core;
using Seein.Core.Ipc;
using Seein.Core.Sensors;
using Seein.Core.Tools;
using Seein.Contracts;
using Seein.Modularity;
using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using WpfApplication = System.Windows.Application;
namespace Seein.Tray;
public partial class MainWindow : Window, IDisposable, IDesktopHost
{
    private const int WmNcHitTest = 0x0084, WmNcLeftButtonDown = 0x00A1, WmNcLeftButtonUp = 0x00A2, HtMaxButton = 9;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly PanelPipeClient _pipe = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly SystemVolumeReader _volume = new();
    private readonly LatestRequestTracker _overviewEnergyRequest = new(), _overviewDayRequest = new();
    private readonly List<IDesktopModule> _modules = [];
    private readonly List<ModulePage> _pages = [];
    private readonly Dictionary<string, FrameworkElement> _views = [];
    private readonly Dictionary<string, ShortcutProvider> _shortcuts = [];
    private readonly List<Func<nint, bool>> _hotkeys = [];
    private ShellShortcutController? _shellShortcuts;
    private string[] _serviceModuleIds = [];
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly Dictionary<string, string> _lastRoutes = [];
    private string? _sectionTabsStamp;
    private bool _reallyClose, _loadingTheme = true, _sidebarCollapsed, _sidebarPreferredCollapsed, _sidebarCompactExpanded, _updatingTabs, _disposed;
    private string _page = "overview";
    private SensorSnapshot? _overviewSensors;
    private PowerSnapshot? _overviewPower;
    public Window Window => this;
    public PanelPipeClient Pipe => _pipe;
    public CancellationTokenSource Shutdown => _shutdown;
    public TextBlock Status => StatusText;
    public string ActiveRoute => _page;
    public nint Handle => new WindowInteropHelper(this).Handle;
    public event Action<PipeEnvelope>? Message;
    public event Action? Connected;
    public Func<string, string, System.Text.Json.Nodes.JsonObject?, Task<ToolResponse>>? CaptureCommand { get; set; }
    public IEnumerable<ShortcutItem> Shortcuts => _shortcuts.Values.SelectMany(p => p.Items());
    public void AddShortcutProvider(string id, ShortcutProvider provider) => _shortcuts.Add(id, provider);
    public (bool Success, string Message) SaveShortcut(ShortcutBinding binding)
    {
        ShortcutValidation.Validate(Shortcuts.Select(i => i.Id == binding.ActionId ? binding : i.Binding));
        return _shortcuts.Values.First(p => p.Items().Any(i => i.Id == binding.ActionId)).Save(binding);
    }
    public void AddHotkeyHandler(Func<nint, bool> handler) => _hotkeys.Add(handler);
    public void AddTrayItem(Forms.ToolStripMenuItem item) => _notifyIcon.ContextMenuStrip!.Items.Insert(_notifyIcon.ContextMenuStrip.Items.Count - 2, item);
    public void Notify(string title, string text, bool error = false) { StatusText.Text = text; if (error && !IsVisible) _notifyIcon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Warning); }
    public MainWindow()
    {
        InitializeComponent(); VersionLabel.Text = "v2.1.0"; if (AppPaths.InstanceSuffix.Length != 0) Title = "SEEIN 2.1.0 预览";
        var theme = DesktopThemeManager.Load(); ThemeSelector.SelectedItem = ThemeSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == theme); _loadingTheme = false;
        _sidebarPreferredCollapsed = DesktopThemeManager.SidebarCollapsed; ApplySidebarState(_sidebarPreferredCollapsed, false); SizeChanged += MainWindow_SizeChanged;
        EnergyHistoryPage.MonthRequested += RequestEnergyHistoryAsync; EnergyHistoryPage.DayRequested += RequestEnergyDayAsync; EnergyHistoryPage.BackRequested += ShowOverviewPage;
        OverviewPage.OpenEnergyRequested += (_, _) => ShowEnergyPage();
        OverviewPage.IsVisibleChanged += (_, _) => { if (!OverviewPage.IsVisible) return; if (_overviewSensors is not null) OverviewPage.SetSensors(_overviewSensors); if (_overviewPower is not null) UpdateOverviewPower(_overviewPower); };
        _notifyIcon = new Forms.NotifyIcon { Icon = LoadApplicationIcon(), Text = "SEEIN 桌面控制台", Visible = true, ContextMenuStrip = FluentTrayMenu.Create() };
        _notifyIcon.ContextMenuStrip.Items.Add(FluentTrayMenu.Item("打开 SEEIN", "\uE8A7", (_, _) => Dispatcher.Invoke(ShowFromTray)));
        _notifyIcon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(FluentTrayMenu.Item("退出托盘程序", "\uE8BB", (_, _) => Dispatcher.Invoke(ExitApplication), destructive: true));
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        Closing += (_, e) => { if (!_reallyClose) { e.Cancel = true; Hide(); } };
        DesktopThemeManager.Changed += ApplyWindowTheme;
        SourceInitialized += (_, _) => { ApplyWindowTheme(); HwndSource.FromHwnd(Handle)?.AddHook(WindowProc); LoadModules(); };
        new WindowInteropHelper(this).EnsureHandle(); ShowOverviewPage();
        _ = Task.Run(() => ServiceLoopAsync(_shutdown.Token)); _ = Task.Run(() => VolumeLoopAsync(_shutdown.Token));
    }
    private void LoadModules()
    {
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var catalog = new ModuleCatalog(root, Path.Combine(AppPaths.SystemDefault.DataRoot, "modules.json"));
        var installedModules = catalog.Read();
        _serviceModuleIds = installedModules.Where(module => module.Enabled && module.Error is null && module.Manifest.Service is not null).Select(module => module.Manifest.Id).ToArray();
        foreach (var installed in installedModules.Where(m => m.Enabled && m.Error is null && m.Manifest.Desktop is not null))
        {
            IDesktopModule? module = null;
            try
            {
                var entry = installed.Manifest.Desktop!; module = new ModuleLoadContext(installed.Directory, entry.Assembly).Create<IDesktopModule>(entry);
                module.Initialize(new DesktopServices(root, installed.Directory, this));
                var pages = module.Pages.ToArray(); if (pages.Any(p => _pages.Any(existing => existing.Route == p.Route))) throw new InvalidDataException("插件页面路由重复。");
                _modules.Add(module); _pages.AddRange(pages); _ = StartModuleAsync(module, installed.Manifest.Name);
            }
            catch (Exception ex) { module?.Dispose(); StatusText.Text = $"插件 {installed.Manifest.Name} 未能加载：{ex.Message}"; }
        }
        RegisterNavigation("overview","仪表盘","\uE80F",OverviewNav);RegisterNavigation("energy","耗能统计","\uE9D2",EnergyNav);RegisterNavigation("settings","应用设置","\uE713",AppSettingsNav);
        _navigation["应用设置"] = AppSettingsNav;
        _pages.AddRange([new("plugins", "插件管理", "应用设置", () => GetModuleManager()), new("shortcuts", "快捷键管理", "应用设置", () => GetShortcutSettings())]);
        if (!_pages.Any(p => p.Route == "themes")) OverviewPage.DeviceStatus = "副屏插件未启用";
        foreach (var group in _pages.Where(p => p.Section != "应用设置").GroupBy(p => p.Section))
        {
            var first = group.First(); var button = new Button { Content = group.Key, ToolTip = group.Key, Style = (Style)FindResource("SidebarNavigationButtonStyle") };
            button.Click += (_, _) => NavigateTo(_lastRoutes.GetValueOrDefault(group.Key) ?? first.Route); NavigationItems.Children.Add(button); _navigation[group.Key] = button;
            RegisterNavigation(NavigationKey(first.Route),group.Key,NavigationGlyph(first.Route),button);
        }
        ApplyNavigationOrder();UpdateNavigationPresentation();
        _shellShortcuts = new(this, _pages.Select(page => page.Route));
        AddShortcutProvider("shell", new(() => _shellShortcuts.Items, _shellShortcuts.Save));
        AddHotkeyHandler(_shellShortcuts.Handle);
    }
    private async Task StartModuleAsync(IDesktopModule module, string name) { try { await module.StartAsync(_shutdown.Token); } catch (Exception ex) { StatusText.Text = $"{name} 启动失败：{ex.Message}"; } }
    private sealed record DesktopServices(string InstallRoot, string ModuleRoot, IDesktopHost Host) : IModuleServices
    {
        public string UserDataRoot => AppPaths.UserDefault.DataRoot; public string SystemDataRoot => AppPaths.SystemDefault.DataRoot;
        public object? GetService(Type type) => type == typeof(IDesktopHost) ? Host : null;
    }
    public void Navigate(string route) => NavigateTo(route);
    public void NavigateTo(string route)
    {
        if (route == _page && PageRegion.Children.OfType<FrameworkElement>().Any(v => v.Visibility == Visibility.Visible)) return;
        if (route == "overview") { ShowOverviewPage(); return; }
        if (route == "energy") { ShowEnergyPage(); return; }
        if (route == "tools") route = _lastRoutes.GetValueOrDefault("小工具") ?? _pages.FirstOrDefault(p => p.Section == "小工具")?.Route ?? "overview";
        var page = _pages.FirstOrDefault(p => p.Route == route); if (page is null) { StatusText.Text = "此功能插件未安装或未启用。"; return; }
        _page = route;
        try { var view = (FrameworkElement)page.Create(); _views[route] = view; ShowContent(route, view, page.Title, page.Section); }
        catch (Exception ex) { StatusText.Text = "页面加载失败：" + ex.Message; }
    }
    public void ShowContent(string route, FrameworkElement view, string title, string section)
    {
        if (!PageRegion.Children.Contains(view)) { view.Visibility = Visibility.Collapsed; PageRegion.Children.Add(view); }
        foreach (FrameworkElement child in PageRegion.Children) child.Visibility = child == view ? Visibility.Visible : Visibility.Collapsed;
        _page = route; _lastRoutes[section] = route; PageTitle.Text = section; PageSubtitle.Visibility = Visibility.Collapsed;
        OverviewNav.Tag = route == "overview" ? "selected" : null; EnergyNav.Tag = route == "energy" ? "selected" : null;
        foreach (var p in _navigation) p.Value.Tag = p.Key == section ? "selected" : null;
        _updatingTabs = true;
        var siblings = _pages.Where(p => p.Section == section).ToList();
        var stamp = section + ":" + string.Join('|', siblings.Select(p => p.Route + ":" + p.Title));
        if (_sectionTabsStamp != stamp)
        {
            ModuleTabs.Children.Clear();
            foreach (var item in siblings) { var tab = new System.Windows.Controls.RadioButton { Content = item.Title, Tag = item.Route, GroupName = "Section", IsChecked = item.Route == route, Style = (Style)FindResource("SegmentedButtonStyle") }; tab.Checked += (_, _) => { if (!_updatingTabs) NavigateTo(item.Route); }; ModuleTabs.Children.Add(tab); }
            AnimatedReorder.Attach(ModuleTabs, "section:" + section); _sectionTabsStamp = stamp;
        }
        else foreach (var tab in ModuleTabs.Children.OfType<System.Windows.Controls.RadioButton>()) tab.IsChecked = Equals(tab.Tag, route);
        GroupTabs.Visibility = siblings.Count > 1 ? Visibility.Visible : Visibility.Collapsed; _updatingTabs = false;
    }
    private void ShowPage(FrameworkElement page, Button nav, string key, string title, string subtitle) => ShowContent(key, page, title, title);
    private void RefreshCurrentPageData() { if (_page == "overview") _ = RequestOverviewCalendarAsync(); if (_page == "energy") _ = EnergyHistoryPage.RefreshMonthAsync(); Connected?.Invoke(); }
    private void AppSettings_Click(object sender, RoutedEventArgs e) => NavigateTo(_lastRoutes.GetValueOrDefault("应用设置") ?? _pages.First(p => p.Section == "应用设置").Route);
    private FrameworkElement GetModuleManager()
    {
        if (!_views.TryGetValue("plugins", out var view)) _views["plugins"] = view = new ModuleManagerView(this);
        return view;
    }
    private FrameworkElement GetShortcutSettings()
    {
        if (!_views.TryGetValue("shortcuts", out var view)) _views["shortcuts"] = view = new ShortcutSettingsView { GetItems = () => Shortcuts, SaveBinding = SaveShortcut };
        ((ShortcutSettingsView)view).CaptureCommand = CaptureCommand;
        ((ShortcutSettingsView)view).Refresh();
        return view;
    }
    private async Task ServiceLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested) try
            {
                await _pipe.ConnectAsync(TimeSpan.FromSeconds(3), token); await _pipe.SendAsync(PipeMessageType.Hello, new { client = "tray", version = "2.1.0" }, token);
                await Dispatcher.InvokeAsync(() => { StatusText.Text = "已连接后台服务"; RefreshCurrentPageData(); });
                // Hello/page reads can arrive while independent backend modules are
                // still loading. Refresh once startup settles; then stop probing.
                var awaitingModules = _serviceModuleIds.Length != 0;
                var probeUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                var nextProbe = DateTimeOffset.MinValue;
                var probeId = Guid.NewGuid().ToString("N");
                await foreach (var message in _pipe.ReadAllAsync(token))
                {
                    await Dispatcher.InvokeAsync(() => HandleMessage(message));
                    if (!awaitingModules) continue;
                    if (message.Type == "moduleHealthResult" && message.Read<ModuleHealthResult>() is { } health && health.RequestId == probeId && Seein.Core.Hosting.ModuleStartupReadiness.IsSettled(_serviceModuleIds, health))
                    {
                        awaitingModules = false;
                        await _pipe.SendAsync(PipeMessageType.Hello, new { client = "tray", version = "2.1.0" }, token);
                        await Dispatcher.InvokeAsync(RefreshCurrentPageData);
                    }
                    else if (DateTimeOffset.UtcNow >= probeUntil) awaitingModules = false;
                    else if (DateTimeOffset.UtcNow >= nextProbe)
                    {
                        nextProbe = DateTimeOffset.UtcNow.AddSeconds(1);
                        await _pipe.SendAsync("moduleHealthRequest", new ModuleHealthRequest(probeId), token);
                    }
                }
                throw new IOException("后台连接已结束。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { await Dispatcher.InvokeAsync(() => { StatusText.Text = "后台服务未连接，正在重试…"; OverviewPage.DeviceStatus = "服务离线"; }); try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; } }
    }
    private async Task VolumeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested) { try { var volume = _volume.ReadPercent(); if (volume is not null && _pipe.IsConnected) await _pipe.SendAsync(PipeMessageType.SensorSnapshot, new SensorSnapshot(DateTimeOffset.Now, new Dictionary<string, double?> { [SensorKeys.SystemVolume] = volume }), token); await Task.Delay(1000, token); } catch (OperationCanceledException) { break; } catch { } }
    }
    private void HandleMessage(PipeEnvelope message)
    {
        switch (message.Type)
        {
            case PipeMessageType.DeviceStatus:
                var device = message.Read<DeviceStatus>();
                if (device is not null) OverviewPage.DeviceStatus = device.PortName is null ? device.Message : $"{device.PortName} · {device.Message}";
                break;
            case PipeMessageType.SensorSnapshot:
                var sensors = message.Read<SensorSnapshot>();
                _overviewSensors = sensors;
                if (OverviewPage.IsVisible && sensors is not null) OverviewPage.SetSensors(sensors);
                break;
            case PipeMessageType.PowerSnapshot:
                var power = message.Read<PowerSnapshot>();
                _overviewPower = power;
                if (OverviewPage.IsVisible && power is not null) UpdateOverviewPower(power);
                break;
            case PipeMessageType.EnergyHistoryResult:
                var history = message.Read<EnergyHistoryResult>();
                if (history is null) return;
                if (_overviewEnergyRequest.IsCurrent(history.RequestId))
                    OverviewPage.SetCalendar(history.Month, history.Entries);
                else
                    EnergyHistoryPage.LoadHistory(history);
                break;
            case PipeMessageType.EnergyDayResult:
                var day = message.Read<EnergyDayResult>();
                if (day is not null) EnergyHistoryPage.LoadDay(day);
                break;
        }
        Message?.Invoke(message);
    }
    private void UpdateOverviewPower(PowerSnapshot power)
    {
        OverviewPage.TotalEnergy = $"{power.TotalWh / 1000d:0.000} kWh";
        OverviewPage.SetHighestDay(power.HighestDayWh, power.HighestDayDate);
        OverviewPage.CurrentPower = $"{power.Watts:0} W";
        OverviewPage.SessionEnergy = $"{power.SessionWh / 1000d:0.000} kWh";
    }
    private void ShowOverviewPage()
    {
        ShowPage(OverviewPage, OverviewNav, "overview", "仪表盘", "");
        _ = RequestOverviewCalendarAsync();
    }
    private async Task RequestOverviewCalendarAsync()
    {
        if (!_pipe.IsConnected) return;
        try
        {
            var month = DateTime.Now.ToString("yyyy-MM");
            var requestId = _overviewEnergyRequest.Begin();
            await _pipe.SendAsync(PipeMessageType.EnergyHistoryRequest, new EnergyHistoryRequest(month, requestId), _shutdown.Token);

        }
        catch { }
    }
    private void ShowEnergyPage()
    {
        ShowPage(EnergyHistoryPage, EnergyNav, "energy", "耗能日历", "每一天的用电记录，以千瓦时（kWh）计。");
        _ = EnergyHistoryPage.RefreshMonthAsync();
    }
    private async Task RequestEnergyHistoryAsync(string month, string requestId)
    {
        try
        {
            if (!_pipe.IsConnected)
            {
                StatusText.Text = "后台服务未连接，连接恢复后请重新打开耗能日历。";
                EnergyHistoryPage.LoadHistory(new EnergyHistoryResult(month, [], null, null, RequestId: requestId, Error: "后台服务未连接。"));
                return;
            }
            await _pipe.SendAsync(PipeMessageType.EnergyHistoryRequest, new EnergyHistoryRequest(month, requestId), _shutdown.Token);
        }
        catch (Exception)
        {
            StatusText.Text = "暂时无法读取耗能记录，请稍后重试。";
            EnergyHistoryPage.LoadHistory(new EnergyHistoryResult(month, [], null, null, RequestId: requestId, Error: "暂时无法读取耗能记录。"));
        }
    }
    private async Task RequestEnergyDayAsync(string localDate, string requestId)
    {
        try
        {
            if (!_pipe.IsConnected)
            {
                EnergyHistoryPage.LoadDay(new EnergyDayResult(localDate, requestId, 0, null, null, null, null,
                    0, 0, 0, [], "1 分钟", false, "后台服务未连接。"));
                return;
            }
            await _pipe.SendAsync(PipeMessageType.EnergyDayRequest, new EnergyDayRequest(localDate, requestId), _shutdown.Token);
        }
        catch (Exception)
        {
            EnergyHistoryPage.LoadDay(new EnergyDayResult(localDate, requestId, 0, null, null, null, null,
                0, 0, 0, [], "1 分钟", false, "暂时无法读取当天记录。"));
        }
    }
    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ActualWidth < 1120)
            _sidebarCompactExpanded = !_sidebarCompactExpanded;
        else
        {
            _sidebarPreferredCollapsed = !_sidebarPreferredCollapsed;
            DesktopThemeManager.SetSidebarCollapsed(_sidebarPreferredCollapsed);
        }
        ApplySidebarState(ActualWidth < 1120 ? !_sidebarCompactExpanded : _sidebarPreferredCollapsed, persist: false);
    }
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ActualWidth >= 1120) _sidebarCompactExpanded = false;
        ApplySidebarState(ActualWidth < 1120 ? !_sidebarCompactExpanded : _sidebarPreferredCollapsed, persist: false);
    }
    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTheme || ThemeSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
        try
        {
            DesktopThemeManager.Set(theme);
            StatusText.Text = "已切换界面外观";
        }
        catch (Exception ex) { StatusText.Text = $"外观切换失败：{ex.Message}"; }
    }
    private void ApplyWindowTheme()
    {
        OverviewPage.RefreshAppearance();
        if (WindowBackdrop.TryApply(this, DesktopThemeManager.IsDark))
            Background = System.Windows.Media.Brushes.Transparent;
        else
            SetResourceReference(BackgroundProperty, "WindowBrush");
    }
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    public void ShowFromExternalRequest() => ShowFromTray();
    public void ShowWindow() => ShowFromTray();
    private void WindowHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && (ShortcutSettingsView.IsRecording || _hotkeys.Any(handler => handler(wParam))))
        {
            handled = true;
            return IntPtr.Zero;
        }
        // The non-client hit test enables the Windows 11 Snap flyout. WPF's Button.Click
        // does not receive mouse input in that area, so handle the native caption click.
        if ((message == WmNcLeftButtonDown || message == WmNcLeftButtonUp) && wParam.ToInt64() == HtMaxButton)
        {
            handled = true;
            if (message == WmNcLeftButtonUp) ToggleWindowState();
            return IntPtr.Zero;
        }
        if (message == WmNcHitTest && WindowState != WindowState.Maximized && MaximizeWindowButton.IsVisible)
        {
            var packed = lParam.ToInt64();
            var screenPoint = new System.Windows.Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            try
            {
                var buttonPoint = MaximizeWindowButton.PointFromScreen(screenPoint);
                if (new Rect(new System.Windows.Point(0, 0), MaximizeWindowButton.RenderSize).Contains(buttonPoint))
                {
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }
            }
            catch (InvalidOperationException) { }
        }
        return IntPtr.Zero;
    }
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleWindowState_Click(object sender, RoutedEventArgs e) => ToggleWindowState();
    private void ToggleWindowState() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeWindowButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.ToolTip = maximized ? "还原" : "最大化";
    }
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private static Icon LoadApplicationIcon()
    {
        try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }
    private void VersionHelp_Click(object sender, RoutedEventArgs e) => new ReleaseNotesWindow { Owner = this }.ShowDialog();
    internal void ExitFromExternalRequest() => ExitApplication();
    private void ApplySidebarState(bool collapsed, bool persist)
    {
        _sidebarCollapsed = collapsed; SidebarColumn.Width = new GridLength(collapsed ? 62 : 196);
        BrandPanel.Visibility = PreferencesCard.Visibility = VersionHelpButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarHeaderColumn.Width = new GridLength(48); SidebarBrandColumn.Width = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        UpdateNavigationPresentation();
        if (persist) DesktopThemeManager.SetSidebarCollapsed(collapsed);
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => ShowOverviewPage();
    private void Energy_Click(object sender, RoutedEventArgs e) => ShowEnergyPage();
    private async void ExitApplication()
    {
        try { foreach (var module in _modules) await module.PrepareStopAsync(CancellationToken.None); foreach (var module in _modules.AsEnumerable().Reverse()) await module.StopAsync(CancellationToken.None); }
        catch (Exception ex) { ShowFromTray(); StatusText.Text = ex.Message; return; }
        _reallyClose = true; Close(); WpfApplication.Current.Shutdown();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _shutdown.Cancel(); DesktopThemeManager.Changed -= ApplyWindowTheme;
        foreach (var module in _modules.AsEnumerable().Reverse()) try { module.Dispose(); } catch { }
        _shellShortcuts?.Dispose();
        _notifyIcon.Visible = false; var menu = _notifyIcon.ContextMenuStrip; _notifyIcon.Dispose(); menu?.Dispose();
        _volume.Dispose(); _pipe.DisposeAsync().AsTask().GetAwaiter().GetResult(); _shutdown.Dispose();
    }
}

