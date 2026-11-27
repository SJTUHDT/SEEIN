using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Seein.Core.Displays;

public interface IDisplayControlClient : IDisposable
{
    Task<DisplayResponse> SendAsync(DisplayRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Native DDC calls run in an owned user-session process, never on the UI or service thread.</summary>
public sealed class DisplayControlClient : IDisplayControlClient
{
    private readonly string _executable, _arguments;
    private readonly TimeSpan _responseTimeout;
    private readonly object _processLock = new();
    public DisplayControlClient(string executable) : this(executable, "--display-host", TimeSpan.FromSeconds(15)) { }
    internal DisplayControlClient(string executable, string arguments, TimeSpan responseTimeout)
    { _executable = executable; _arguments = arguments; _responseTimeout = responseTimeout; }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1);
    private Process? _process;
    private bool _disposed;

    public async Task<DisplayResponse> SendAsync(DisplayRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { RequestId = Guid.NewGuid().ToString("N") };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_responseTimeout);
            Process process;
            lock (_processLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_process is null || _process.HasExited)
                {
                    Reset();
                    _process = Process.Start(new ProcessStartInfo(_executable, _arguments)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                        RedirectStandardOutput = true, StandardInputEncoding = new UTF8Encoding(false),
                        StandardOutputEncoding = new UTF8Encoding(false)
                    }) ?? throw new IOException("显示器控制进程无法启动。");
                }
                process = _process;
            }
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var response = line is null ? null : JsonSerializer.Deserialize<DisplayResponse>(line, Json);
            if (response is null || response.RequestId != request.RequestId) throw new IOException("显示器响应不匹配，请刷新设备。");
            return response;
        }
        catch (OperationCanceledException)
        {
            Reset();
            return new(request.RequestId, false, Error: cancellationToken.IsCancellationRequested
                ? "操作已取消，请刷新设备。" : "显示器响应超时（15秒），控制进程已重置。可点击刷新重试。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            Reset();
            return new(request.RequestId, false, Error: ex.Message);
        }
        finally { _gate.Release(); }
    }

    private void Reset()
    {
        Process? process;
        lock (_processLock) { process = _process; _process = null; }
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        process.Dispose();
    }

    public void Dispose() { lock (_processLock) { _disposed = true; Reset(); } }
}
