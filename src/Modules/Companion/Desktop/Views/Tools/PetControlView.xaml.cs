using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Seein.Core;
using System.IO;
using System.Text;
using System.Windows;

namespace Seein.Tray;

public partial class PetControlView : System.Windows.Controls.UserControl
{
    private const string VirtualHost = "seein-pet.invalid";
    private const string IndexAddress = "https://" + VirtualHost + "/standalone-ai/";
    private const int MaxRequest = 12 * 1024 * 1024;
    private const string SettingsTheme = """
        :root {
            --dsw-font-family: "Segoe UI Variable Text","Microsoft YaHei UI",sans-serif;
        }
        .rowCheck:has(#g-tray) { display: none; }
        @media (prefers-color-scheme: dark) {
            :root {
                color-scheme: dark;
                --dsw-static-neutral-bluish-00: #2b2b2b;
                --dsw-static-neutral-bluish-50: #202020;
                --dsw-static-neutral-bluish-75: #323232;
                --dsw-static-neutral-bluish-100: #1f3745;
                --dsw-static-neutral-bluish-200: #3a3a3a;
                --dsw-static-neutral-bluish-300: #9a9a9a;
                --dsw-static-neutral-bluish-600: #a8a8a8;
                --dsw-static-neutral-bluish-700: #c5c5c5;
                --dsw-static-neutral-bluish-1000: #ffffff;
                --dsw-static-blue-500: #4cc2ff;
                --dsw-static-red-500: #ffb4a9;
                --dsw-static-green-600: #6ccb5f;
                --dsw-alias-border-l1: #ffffff14;
                --dsw-alias-border-l2: #ffffff20;
                --dsw-alias-border-l3: #ffffff29;
                --dsw-alias-border-l4: #ffffff42;
                --dsw-alias-interactive-bg-hover: #ffffff14;
                --dsw-alias-interactive-bg-active: #ffffff29;
                --dsw-alias-label-primary-foreground: #10232d;
                --dsw-alias-button-info-hover: #79d2ff;
                --dsw-specific-bubble: #1f3745;
            }
        }
        @media (forced-colors: active) {
            :root { color-scheme: light dark; }
        }
        """;
    private readonly DesktopPetRuntime _pet = DesktopPetRuntime.Instance;
    private WebView2CompositionControl? _browser;
    private bool _busy;
    private bool _hidden;
    private bool _focusModel;
    private bool _pageReady;

    public PetControlView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _ = RefreshAsync();
        };
    }

    internal void OpenModelSettings()
    {
        _focusModel = true;
        _ = FocusModelAsync();
    }

    private async Task FocusModelAsync()
    {
        if (_pet.IsNative && _focusModel && IsVisible && _pet.IsRunning)
        {
            _focusModel = false;
            await _pet.CallAsync("modelSettings");
            return;
        }
        var view = _browser;
        if (!_focusModel || !_pageReady || !IsVisible || view?.CoreWebView2 is not { } core) return;
        try
        {
            var result = await core.ExecuteScriptAsync("""
                (() => {
                    const tab = document.getElementById('tab-config');
                    const model = document.querySelector('#settings .cardTitle');
                    if (!tab || !model) return false;
                    tab.click();
                    model.tabIndex = -1;
                    model.focus({preventScroll:true});
                    model.scrollIntoView({block:'start'});
                    return true;
                })()
                """);
            if (view == _browser && result == "true") _focusModel = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    internal Task ReconnectAsync()
    {
        ReleaseView();
        return RefreshAsync();
    }

    internal async Task RefreshAsync(bool updateMessage = true)
    {
        RefreshButtons();
        if (!_pet.IsRunning)
        {
            if (updateMessage) StateText.Text = _pet.HasData ? "SEEIN 桌宠已停止。" : "点击启动即可使用随包桌宠。";
            ReleaseView();
            return;
        }

        try
        {
            var state = await _pet.CallAsync("status");
            _hidden = state.GetProperty("hidden").GetBoolean();
            if (updateMessage)
            {
                var windows = state.GetProperty("windows").GetInt32();
                StateText.Text = $"{(_pet.IsNative ? "SEEIN 原生桌宠" : "dsh-pet 0.4.9")} · {(_hidden ? "已隐藏" : $"运行中 · {windows} 个桌宠窗口")}";
            }
            if (IsVisible && _browser is null) await OpenSettingsAsync();
            else if (_focusModel) await FocusModelAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ReleaseView();
            StateText.Text = "桌宠仍在运行；内嵌设置需要 Microsoft Edge WebView2 Runtime。";
        }
        catch (Exception ex) { ReleaseView(); StateText.Text = ex.Message; }
        finally { RefreshButtons(); }
    }

    private async Task OpenSettingsAsync()
    {
        if (_pet.IsNative)
        {
            if (BrowserHost.Children.Count == 0)
            {
                BrowserHost.Children.Add(new PetSettingsPanel(_pet));
            }
            _focusModel = false;
            return;
        }
        var view = new WebView2CompositionControl();
        System.Windows.Automation.AutomationProperties.SetName(view, "SEEIN 桌宠设置");
        _browser = view;
        BrowserHost.Children.Add(view);
        var userData = Path.Combine(AppPaths.UserDefault.DataRoot, "DafeiyuWebView", "StandalonePet");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        if (_browser != view) return;
        await view.EnsureCoreWebView2Async(environment);
        if (_browser != view) return;
        var core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.NavigationStarting += (_, args) => args.Cancel = args.Uri != IndexAddress;
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        DesktopThemeManager.Changed += ApplyTheme;
        ApplyTheme();
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) => _ = RespondAsync(environment, args);
        core.NavigationCompleted += async (_, args) =>
        {
            if (_browser != view) return;
            _pageReady = args.IsSuccess;
            if (_pageReady) await FocusModelAsync();
            else StateText.Text = "SEEIN 桌宠设置页未能加载。可重新打开此子页。";
        };
        core.Navigate(IndexAddress);
    }

    private async Task RespondAsync(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != VirtualHost || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        {
            args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            return;
        }

        using var defer = args.GetDeferral();
        try
        {
            var content = await ReadBodyAsync(args.Request.Content);
            var result = await _pet.CallAsync("request", args.Request.Method, uri.PathAndQuery, content,
                args.Request.Headers.Contains("Content-Type") ? args.Request.Headers.GetHeader("Content-Type") : null);
            var bytes = result.GetProperty("body").GetBytesFromBase64();
            var type = result.GetProperty("mediaType").GetString() ?? "application/octet-stream";
            var status = result.GetProperty("status").GetInt32();
            if (type.Contains('\r') || type.Contains('\n') || status is < 200 or > 599) throw new IOException();
            if (uri.PathAndQuery == "/standalone-ai/settings.css" && status == 200)
            {
                bytes = [.. bytes, .. Encoding.UTF8.GetBytes("\n" + SettingsTheme)];
            }
            const string csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                "connect-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self'; " +
                "form-action 'none'; frame-ancestors 'none'; base-uri 'none'";
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), status,
                status < 400 ? "OK" : "Request failed",
                $"Content-Type: {type}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: {csp}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or System.Runtime.InteropServices.COMException)
        {
            try { args.Response = environment.CreateWebResourceResponse(null, 503, "Pet unavailable", ""); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream? content)
    {
        if (content is null) return null;
        using var data = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read = await content.ReadAsync(buffer)) != 0)
        {
            if (data.Length + read > MaxRequest) throw new IOException("Request exceeds limit");
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }

    private async Task ExecuteAsync(Func<Task> operation, string? success = null)
    {
        if (_busy) return;
        _busy = true;
        RefreshButtons();
        try
        {
            await operation();
            await RefreshAsync(success is null);
            if (success is not null) StateText.Text = success;
        }
        catch (Exception ex) { StateText.Text = ex.Message; }
        finally { _busy = false; RefreshButtons(); }
    }

    private void RefreshButtons()
    {
        StartButton.IsEnabled = !_busy && !_pet.IsRunning;
        var running = !_busy && _pet.IsRunning;
        VisibilityButton.IsEnabled = HomeButton.IsEnabled = ReloadButton.IsEnabled = StopButton.IsEnabled = running;
        VisibilityButton.Content = _hidden ? "显示桌宠" : "隐藏桌宠";
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => _pet.StartAsync());
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(_pet.DisableAsync);
    private async void Visibility_Click(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () => await _pet.CallAsync(_hidden ? "show" : "hide"));
    private async void Home_Click(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(async () => await _pet.CallAsync("home"), "桌宠已回到配置的停靠位置。");
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(async () =>
        {
            await _pet.CallAsync("reload");
            ReleaseView();
        }, "桌宠配置已重载。");
    }

    internal void ReleaseView()
    {
        var view = _browser;
        _browser = null;
        _pageReady = false;
        BrowserHost.Children.Clear();
        if (view is null) return;
        DesktopThemeManager.Changed -= ApplyTheme;
        BrowserHost.Children.Remove(view);
        view.Dispose();
    }

    private void ApplyTheme()
    {
        if (_browser?.CoreWebView2?.Profile is not { } profile) return;
        profile.PreferredColorScheme = SystemParameters.HighContrast
            ? CoreWebView2PreferredColorScheme.Auto
            : DesktopThemeManager.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
    }
}
