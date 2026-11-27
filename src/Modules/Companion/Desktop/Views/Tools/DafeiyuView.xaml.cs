using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Seein.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using PageKind = Seein.Core.DafeiyuPage;

namespace Seein.Tray;

public partial class DafeiyuView : System.Windows.Controls.UserControl
{
    private sealed class BrowserSlot
    {
        public required WebView2CompositionControl View { get; init; }
        public Uri? Origin { get; set; }
        public string? Error { get; set; }
        public bool Loaded { get; set; }
        public ulong NavigationId { get; set; }
        public Uri? Pending { get; set; }
    }

    private static readonly Lazy<string> FluentCss = new(() =>
    {
        using var stream = typeof(DafeiyuView).Assembly.GetManifestResourceStream("DafeiyuFluent.css")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private DafeiyuWorkbench _workbench = new();
    private readonly Dictionary<PageKind, BrowserSlot> _slots = new();
    private CancellationTokenSource? _session;
    private PageKind _selected;
    private bool _watchingTheme;

    public DafeiyuView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Open(_selected);
            else ReleaseViews();
        };
    }

    private static string Title(PageKind page) => page switch
    {
        PageKind.AstrBot => "AstrBot 后台",
        PageKind.SnowLuma => "SnowLuma 后台",
        PageKind.PetSettings => "桌宠设置",
        _ => "大肥鱼工作台"
    };

    private void Workbench_Click(object sender, RoutedEventArgs e) => Open(PageKind.Workbench);
    private void ConnectionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (new CompanionConnectionDialog(Window.GetWindow(this)).ShowDialog() != true) return;
        ReleaseViews(); _workbench.Dispose(); _workbench = new(); Open(_selected);
    }
    private void AstrBot_Click(object sender, RoutedEventArgs e) => Open(PageKind.AstrBot);
    private void SnowLuma_Click(object sender, RoutedEventArgs e) => Open(PageKind.SnowLuma);
    private void PetSettings_Click(object sender, RoutedEventArgs e) => Open(PageKind.PetSettings);

    internal void OpenToolsSection(string route)
    {
        Sections.Visibility = Visibility.Collapsed;
        Open(route switch { "pet" => PageKind.PetSettings, "astrbot" => PageKind.AstrBot, "qq" => PageKind.SnowLuma, _ => PageKind.Workbench });
    }

    internal void OpenPetSettings(bool model)
    {
        if (model && DesktopPetRuntime.Instance.IsNative) { Open(PageKind.AstrBot); return; }
        Open(PageKind.PetSettings);
        if (model) PetControls.OpenModelSettings();
    }

    internal Task RefreshPetAsync() => PetControls.RefreshAsync();
    internal void ReleasePetView() => PetControls.ReleaseView();

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == PageKind.PetSettings)
        {
            _ = PetControls.ReconnectAsync();
            return;
        }
        Remove(_selected);
        Open(_selected);
    }

    private void Open(PageKind page, Uri? destination = null)
    {
        _selected = page;
        if (!IsVisible) return;
        _session ??= new CancellationTokenSource();
        if (!_watchingTheme)
        {
            DesktopThemeManager.Changed += UpdateTheme;
            _watchingTheme = true;
        }

        SelectTab(WorkbenchTab, page == PageKind.Workbench);
        SelectTab(AstrBotTab, page == PageKind.AstrBot);
        SelectTab(SnowLumaTab, page == PageKind.SnowLuma);
        SelectTab(PetSettingsTab, page == PageKind.PetSettings);
        PetControls.Visibility = page == PageKind.PetSettings ? Visibility.Visible : Visibility.Collapsed;
        BrowserHost.Visibility = page == PageKind.PetSettings ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (kind, existing) in _slots)
            existing.View.Visibility = kind == page ? Visibility.Visible : Visibility.Collapsed;

        if (page == PageKind.PetSettings)
        {
            ConnectionCard.Hide();
            return;
        }

        if (_slots.TryGetValue(page, out var slot))
        {
            if (destination is not null)
            {
                if (slot.Origin is not null && slot.View.CoreWebView2 is { } core)
                    core.Navigate(destination.AbsoluteUri);
                else slot.Pending = destination;
            }
            ShowConnection(slot);
            return;
        }

        var view = new WebView2CompositionControl();
        AutomationProperties.SetName(view, Title(page));
        slot = new BrowserSlot { View = view, Pending = destination };
        _slots.Add(page, slot);
        BrowserHost.Children.Add(view);
        ShowConnection(slot);
        _ = InitializeAsync(page, slot, _session.Token);
    }

    private static void SelectTab(Button button, bool active)
    {
        button.Tag = active ? "selected" : null;
        button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, active ? "AccentBrush" : "TextBrush");
        AutomationProperties.SetItemStatus(button, active ? "当前页面" : "");
    }

    private async Task InitializeAsync(PageKind page, BrowserSlot slot, CancellationToken cancellation)
    {
        try
        {
            var availableRuntime = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(availableRuntime))
                throw new WebView2RuntimeNotFoundException();

            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppPaths.FolderName, "DafeiyuWebView", page.ToString());
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            if (!IsCurrent(page, slot, cancellation)) return;
            await slot.View.EnsureCoreWebView2Async(environment);
            if (!IsCurrent(page, slot, cancellation)) return;

            var core = slot.View.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                var uri = Parse(args.Uri);
                if (uri is not null && slot.Origin is not null && DafeiyuNavigation.SameOrigin(uri, slot.Origin))
                {
                    slot.NavigationId = args.NavigationId;
                    slot.Loaded = false;
                    slot.Error = null;
                    if (page == _selected) ShowConnection(slot);
                    return;
                }
                args.Cancel = true;
                if (args.IsUserInitiated && !args.IsRedirected) RouteLink(page, uri);
            };
            core.FrameNavigationStarting += (_, args) =>
            {
                var uri = Parse(args.Uri);
                args.Cancel = uri is null || slot.Origin is null || !DafeiyuNavigation.SameOrigin(uri, slot.Origin);
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!args.IsUserInitiated) return;
                var uri = Parse(args.Uri);
                if (uri is not null && slot.Origin is not null && DafeiyuNavigation.SameOrigin(uri, slot.Origin))
                    Dispatcher.BeginInvoke(() => { if (IsCurrent(page, slot, cancellation)) core.Navigate(uri.AbsoluteUri); });
                else RouteLink(page, uri);
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, args) =>
            {
                if (!IsCurrent(page, slot, cancellation)) return;
                if (slot.Origin is null || args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                if (slot.NavigationId != args.NavigationId) return;
                if (page == PageKind.Workbench && args.IsSuccess)
                {
                    _ = WaitForChecksAsync(slot, args.NavigationId, cancellation);
                    return;
                }
                slot.Loaded = args.IsSuccess;
                slot.Error = args.IsSuccess ? null : $"{Title(page)}无法连接。请确认对应程序正在运行。";
                if (page == _selected) ShowConnection(slot);
            };
            core.ProcessFailed += (_, _) =>
            {
                if (!IsCurrent(page, slot, cancellation)) return;
                slot.Error = "内嵌页面已停止响应。可重新连接。";
                slot.Loaded = false;
                if (page == _selected) ShowConnection(slot);
            };
            core.DOMContentLoaded += (_, _) =>
            {
                if (page == PageKind.Workbench && IsCurrent(page, slot, cancellation))
                    _ = ApplyWorkbenchStyleAsync(slot);
            };

            var address = page switch
            {
                PageKind.Workbench => await _workbench.ConnectAsync(cancellation),
                PageKind.AstrBot => DafeiyuNavigation.AstrBot,
                _ => DafeiyuNavigation.SnowLuma
            };
            if (!IsCurrent(page, slot, cancellation)) return;
            slot.Origin = new Uri(address.GetLeftPart(UriPartial.Authority));
            var initial = slot.Pending ?? address;
            slot.Pending = null;
            core.Navigate(initial.AbsoluteUri);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (WebView2RuntimeNotFoundException)
        {
            SetError("缺少 Microsoft Edge WebView2 Runtime。安装后可重新连接。");
        }
        catch (DafeiyuWorkbenchException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception)
        {
            SetError("内嵌页面无法启动。请确认本机工作台和 WebView2 可用。");
        }

        void SetError(string text)
        {
            if (!IsCurrent(page, slot, cancellation)) return;
            slot.Loaded = false;
            slot.Error = text;
            if (page == _selected) ShowConnection(slot);
        }
    }

    private static Uri? Parse(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null;

    private void RouteLink(PageKind from, Uri? uri)
    {
        if (uri is null) return;
        if (DafeiyuNavigation.ManagementPage(uri) is { } page)
        {
            var target = DafeiyuNavigation.CanonicalManagementAddress(uri);
            Dispatcher.BeginInvoke(() => { if (_selected == from && IsVisible) Open(page, target); });
            return;
        }
        if (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback || uri.UserInfo.Length != 0) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private bool IsCurrent(PageKind page, BrowserSlot slot, CancellationToken cancellation) =>
        !cancellation.IsCancellationRequested && _slots.GetValueOrDefault(page) == slot;

    private async Task WaitForChecksAsync(BrowserSlot slot, ulong navigationId, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        bool Current() => IsCurrent(PageKind.Workbench, slot, cancellation) &&
            slot.NavigationId == navigationId && slot.Error is null;

        try
        {
            while (Current())
            {
                // The heading that ships with the HTML is a placeholder. The status API populates
                // the check rows and their actual count together on its first successful response.
                var ready = await slot.View.CoreWebView2
                    .ExecuteScriptAsync("Boolean(document.querySelector('#items > .row'))").WaitAsync(deadline.Token);
                if (!Current()) return;
                if (ready == "true")
                {
                    slot.Loaded = true;
                    if (_selected == PageKind.Workbench) ShowConnection(slot);
                    return;
                }
                await Task.Delay(180, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }

        if (!Current()) return;
        slot.Error = "首轮检查未返回结果，请点击「重新连接」再试。";
        if (_selected == PageKind.Workbench) ShowConnection(slot);
    }

    private void ShowConnection(BrowserSlot slot)
    {
        if (slot.Loaded)
        {
            var animate = ConnectionCard.Visibility == Visibility.Visible;
            ConnectionCard.Hide();
            PageLoadPanel.Reveal(BrowserHost, animate);
            return;
        }
        BrowserHost.BeginAnimation(OpacityProperty, null);
        BrowserHost.Opacity = 0;
        BrowserHost.IsHitTestVisible = false;
        var title = slot.Error is not null ? "加载失败" : _selected == PageKind.Workbench
            ? "正在加载大肥鱼工作台" : $"正在连接{Title(_selected)}";
        ConnectionCard.Show(title, slot.Error ?? (_selected == PageKind.Workbench
            ? "正在等待本机工作台完成首轮检查。" : "正在载入本机页面。"), busy: slot.Error is null);
    }

    private void UpdateTheme()
    {
        if (_slots.TryGetValue(PageKind.Workbench, out var slot)) _ = ApplyWorkbenchStyleAsync(slot);
    }

    private async Task ApplyWorkbenchStyleAsync(BrowserSlot slot)
    {
        var core = slot.View.CoreWebView2;
        if (core is null || slot.Origin is null || Parse(core.Source) is not { } uri ||
            !DafeiyuNavigation.SameOrigin(uri, slot.Origin)) return;
        var css = JsonSerializer.Serialize(FluentCss.Value);
        var theme = JsonSerializer.Serialize(SystemParameters.HighContrast ? "contrast" : DesktopThemeManager.IsDark ? "dark" : "light");
        try
        {
            await core.ExecuteScriptAsync($$"""
                (() => {
                  const root = document.documentElement;
                  root.dataset.seeinTheme = {{theme}};
                  let style = document.getElementById('seein-workbench-style');
                  if (!style) {
                    style = document.createElement('style');
                    style.id = 'seein-workbench-style';
                    style.textContent = {{css}};
                    document.head.appendChild(style);
                    const nav = document.querySelector('.tabs[role="tablist"]');
                    if (!nav) return;
                    const tabs = Array.from(nav.querySelectorAll('button[data-tab]'));
                    const update = () => tabs.forEach(tab => {
                      const selected = tab.classList.contains('on');
                      tab.setAttribute('aria-selected', String(selected));
                      tab.tabIndex = selected ? 0 : -1;
                    });
                    tabs.forEach(tab => {
                      tab.setAttribute('role', 'tab');
                      tab.id = 'seein-tab-' + tab.dataset.tab;
                      tab.setAttribute('aria-controls', 'p-' + tab.dataset.tab);
                      const panel = document.getElementById('p-' + tab.dataset.tab);
                      panel?.setAttribute('role', 'tabpanel');
                      panel?.setAttribute('aria-labelledby', tab.id);
                    });
                    update();
                    const dialogs = Array.from(document.querySelectorAll('.modal, .drawer'));
                    let activeDialog, previousFocus;
                    const focusables = dialog => Array.from(dialog.querySelectorAll('button, a[href], input, select, textarea, [tabindex="0"]'))
                      .filter(el => !el.disabled && el.getClientRects().length && getComputedStyle(el).visibility === 'visible');
                    const dialogChanges = new MutationObserver(() => {
                      const next = dialogs.find(dialog => dialog.classList.contains('on'));
                      if (next === activeDialog) return;
                      if (!activeDialog && next) previousFocus = document.activeElement;
                      activeDialog = next;
                      if (next) {
                        next.tabIndex = -1;
                        (focusables(next)[0] || next).focus();
                      } else if (previousFocus?.isConnected) previousFocus.focus();
                    });
                    dialogs.forEach(dialog => dialogChanges.observe(dialog, { attributes: true, attributeFilter: ['class'] }));
                    document.addEventListener('keydown', e => {
                      if (!activeDialog || e.key !== 'Tab') return;
                      const items = focusables(activeDialog);
                      if (!items.length) { e.preventDefault(); return; }
                      const current = items.indexOf(document.activeElement);
                      if (current < 0 || (e.shiftKey ? current === 0 : current === items.length - 1)) {
                        e.preventDefault();
                        items[e.shiftKey ? items.length - 1 : 0].focus();
                      }
                    });
                    const observer = new MutationObserver(update);
                    tabs.forEach(tab => observer.observe(tab, { attributes: true, attributeFilter: ['class'] }));
                    nav.addEventListener('keydown', e => {
                      const index = tabs.indexOf(e.target);
                      if (index < 0) return;
                      const step = { ArrowLeft: -1, ArrowRight: 1 }[e.key];
                      const next = step ? (index + step + tabs.length) % tabs.length :
                                   e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : -1;
                      if (next < 0) return;
                      e.preventDefault();
                      tabs[next].click();
                      tabs[next].focus();
                    });
                  }
                })();
                """);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void Remove(PageKind page)
    {
        if (!_slots.Remove(page, out var slot)) return;
        BrowserHost.Children.Remove(slot.View);
        slot.View.Dispose();
    }

    public void ReleaseViews()
    {
        if (_watchingTheme)
        {
            DesktopThemeManager.Changed -= UpdateTheme;
            _watchingTheme = false;
        }
        _session?.Cancel();
        _session?.Dispose();
        _session = null;
        foreach (var page in _slots.Keys.ToArray()) Remove(page);
    }
    internal void StopOwnedWorkbench() { ReleaseViews(); _workbench.Dispose(); }
}
