using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Seein.Core;
using Seein.SmartHome;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Seein.Tray;

public partial class RouterControlView : System.Windows.Controls.UserControl, IDisposable
{
    private readonly SmartHomeStore _store = new(AppPaths.UserDefault.DataRoot);
    private WebView2CompositionControl? _browser;
    private Uri? _origin;
    private bool _disposed;
    private bool _autoAttempted;

    public RouterControlView()
    {
        InitializeComponent();
        RouterAddress.Text = _store.LoadSettings().RouterAddress;
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                if (_browser?.CoreWebView2 is {} core)
                {
                    try { core.Resume(); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                    { Release(); await ConnectAsync(); }
                }
                else await ConnectAsync();
            }
            else if (_browser is {} view) await SuspendHiddenAsync(view);
        };
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try { var settings = _store.LoadSettings(); _store.SaveSettings(settings with { RouterAddress = RouterAddress.Text.Trim() }); Release(); await ConnectAsync(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { StateText.Text = ex.Message; }
    }
    private async Task ConnectAsync()
    {
        if (_disposed || !IsVisible || _browser is not null) return;
        WebView2CompositionControl? view = null;
        try
        {
            _origin = LocalDeviceAddress.Router(_store.LoadSettings().RouterAddress);
            _autoAttempted = false;
            ErrorPanel.Visibility = Visibility.Collapsed;
            view = new();
            _browser = view;
            BrowserHost.Children.Add(view);
            StateText.Text = "正在连接路由器…";
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppPaths.UserDefault.DataRoot, "SmartHome", "RouterWebView"));
            if (_browser != view) return;
            await view.EnsureCoreWebView2Async(environment);
            if (_browser != view) return;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.WebMessageReceived += async (_, args) =>
            {
                if (_browser != view || _origin is null || !LocalDeviceAddress.SameOrigin(_origin, args.Source)) return;
                try
                {
                    using var message = JsonDocument.Parse(args.WebMessageAsJson);
                    var root = message.RootElement;
                    if (!root.TryGetProperty("type", out var type)) return;
                    if (type.GetString() == "router-login-ready" && !_autoAttempted)
                    {
                        _autoAttempted = true;
                        var saved = _store.LoadRouterCredential(_origin);
                        if (saved is not null) await core.ExecuteScriptAsync("window.__seeinRouterLogin?.(" + JsonSerializer.Serialize(saved.Password) + ")");
                    }
                    else if (type.GetString() == "router-login-success" && root.TryGetProperty("password", out var password))
                        _store.SaveRouterCredential(_origin, password.GetString() ?? "");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* Vault failures must not interrupt the router page. */ }
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(RouterLoginScript.Source);
            if (_browser != view) return;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, args) => args.Cancel = _origin is null || !LocalDeviceAddress.SameOrigin(_origin, args.Uri);
            core.FrameNavigationStarting += (_, args) => args.Cancel = _origin is null || !LocalDeviceAddress.SameOrigin(_origin, args.Uri);
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (args.IsUserInitiated && _origin is not null && LocalDeviceAddress.SameOrigin(_origin, args.Uri))
                    Dispatcher.BeginInvoke(() => { if (_browser == view) core.Navigate(args.Uri); });
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (_browser == view && !args.IsSuccess) ShowError("无法加载路由器，请检查网络后重新连接。");
            };
            core.ProcessFailed += (_, _) => { if (_browser == view) { ShowError("管理页面已停止响应，请重新连接。"); Release(); } };
            core.Navigate(_origin.AbsoluteUri);
            if (!IsVisible) await SuspendHiddenAsync(view);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (view is not null && _browser != view) return;
            Release();
            ShowError(ex is ArgumentException ? ex.Message : "管理页面无法启动，请确认网络和 Microsoft Edge WebView2 可用。");
        }
    }

    private void ShowError(string message) { StateText.Text = message; ErrorPanel.Visibility = Visibility.Visible; }

    private async Task SuspendHiddenAsync(WebView2CompositionControl view)
    {
        if (_disposed || _browser != view || view.CoreWebView2 is not {} core) return;
        try
        {
            await core.TrySuspendAsync();
            // A quick return can race the asynchronous suspension request.
            if (!_disposed && _browser == view && IsVisible) core.Resume();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { System.Diagnostics.Trace.WriteLine(ex.Message); }
    }

    private void Release()
    {
        var view = _browser;
        _browser = null;
        BrowserHost.Children.Clear();
        view?.Dispose();
    }

    public void Dispose() { _disposed = true; Release(); }
}
