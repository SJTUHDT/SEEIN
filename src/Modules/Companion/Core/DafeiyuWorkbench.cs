using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seein.Core;

public enum DafeiyuPage { Workbench, AstrBot, SnowLuma, PetSettings }

public static class DafeiyuNavigation
{
    public static Uri AstrBot => new(CompanionConnectionSettings.Load().AstrBotAddress);
    public static Uri SnowLuma => new(CompanionConnectionSettings.Load().QqAddress);

    public static bool SameOrigin(Uri uri, Uri origin) => uri.IsAbsoluteUri &&
        uri.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port &&
        uri.UserInfo.Length == 0;

    public static DafeiyuPage? ManagementPage(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0 ||
            uri.Host is not ("127.0.0.1" or "localhost")) return null;
        return uri.Port == AstrBot.Port ? DafeiyuPage.AstrBot : uri.Port == SnowLuma.Port ? DafeiyuPage.SnowLuma : null;
    }

    public static Uri CanonicalManagementAddress(Uri uri) => new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
}

public sealed class DafeiyuWorkbenchException(string message) : InvalidOperationException(message);

public sealed class DafeiyuWorkbench : IDisposable
{
    public static string DefaultRoot => CompanionConnectionSettings.Load().WorkbenchRoot;
    private readonly string _root;
    private readonly SemaphoreSlim _connectLock = new(1);
    private readonly object _ownerGate = new();
    private OwnedProcess? _owner;
    private bool _disposed;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(2) };

    public DafeiyuWorkbench(string? root = null) => _root = Path.GetFullPath(root ?? DefaultRoot);

    public async Task<Uri> ConnectAsync(CancellationToken cancellation)
    {
        await _connectLock.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var script = Path.Combine(_root, "tools", "workbench", "server.py");
            if (!File.Exists(script)) throw new DafeiyuWorkbenchException("找不到本机大肥鱼工作台。");
            if (await DiscoverAsync(cancellation) is { } existing) return existing;
            var launcher = new[]
            {
                Path.Combine(_root, "python", "pythonw.exe"),
                Path.Combine(_root, "uv", "tools", "astrbot", "Scripts", "pythonw.exe"),
                Path.Combine(_root, "tools", "python", "pythonw.exe"),
                Path.Combine(_root, "uv", "tools", "astrbot", "Scripts", "python.exe")
            }.FirstOrDefault(File.Exists) ?? throw new DafeiyuWorkbenchException("找不到大肥鱼随附的 Python。");
            Process process;
            lock (_ownerGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _owner?.Dispose();
                _owner = OwnedProcess.Start(launcher, WindowsCommandLine.Join(["-X", "utf8", script, "--dir", _root, "--no-open"]), null, true, allowBreakaway: true);
                process = _owner.Process;
            }
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(16))
            {
                await Task.Delay(250, cancellation);
                if (await DiscoverAsync(cancellation) is { } ready) return ready;
                if (process.HasExited && process.ExitCode != 0)
                    throw new DafeiyuWorkbenchException($"工作台启动失败（退出码 {process.ExitCode}）。");
            }
            throw new DafeiyuWorkbenchException("工作台未在规定时间内就绪。");
        }
        catch { lock (_ownerGate) { _owner?.Dispose(); _owner = null; } throw; }
        finally { _connectLock.Release(); }
    }

    public void Dispose()
    {
        lock (_ownerGate) { _disposed = true; _owner?.Dispose(); _owner = null; }
    }

    private async Task<Uri?> DiscoverAsync(CancellationToken cancellation)
    {
        try
        {
            var path = Path.Combine(_root, "logs", "workbench", "server.json");
            if (!File.Exists(path)) return null;
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4096) return null;
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var root = json.RootElement;
            var port = root.GetProperty("port").GetInt32();
            var pid = root.GetProperty("pid").GetInt32();
            var handshake = root.GetProperty("token").GetString();
            if (port is < 1 or > 65535 || handshake is null ||
                !Regex.IsMatch(handshake, @"\A[A-Za-z0-9_-]{16,128}\z") || !IsWorkbenchRuntime(pid)) return null;

            var origin = new Uri($"http://127.0.0.1:{port}/");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "api/ping"));
            request.Headers.Add("X-DFY-Token", handshake);
            using var response = await Http.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode) return null;
            using var ping = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellation);
            if (ping is null || !ping.RootElement.GetProperty("ok").GetBoolean() ||
                ping.RootElement.GetProperty("pid").GetInt32() != pid) return null;
            return new UriBuilder(origin) { Fragment = $"t={handshake}" }.Uri;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            HttpRequestException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }

    private bool IsWorkbenchRuntime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return path is not null && path.StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(path).ToLowerInvariant() is "python.exe" or "pythonw.exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
