using System.IO.Ports;
using System.Diagnostics;

namespace Seein.Core.Protocol;

public sealed class Ds916Transport : IAsyncDisposable
{
    private readonly ISerialDeviceLocator _locator;
    private byte[]? _latestFrame;
    private long _lastFrameSubmitted;
    private readonly Action<string>? _diagnostic;
    private readonly Action<DeviceStatus>? _statusChanged;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runTask;

    public Ds916Transport(ISerialDeviceLocator locator, Action<DeviceStatus>? statusChanged = null, Action<string>? diagnostic = null)
    {
        _locator = locator;
        _statusChanged = statusChanged;
        _diagnostic = diagnostic;
    }

    public void Start() => _runTask ??= Task.Run(() => RunAsync(_shutdown.Token));

    public bool SubmitLatest(byte[] jpeg)
    {
        if (jpeg is null || jpeg.Length == 0 || _shutdown.IsCancellationRequested) return false;
        Volatile.Write(ref _latestFrame, jpeg);
        Interlocked.Exchange(ref _lastFrameSubmitted, Stopwatch.GetTimestamp());
        return true;
    }

    private async Task RunAsync(CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            var portName = _locator.FindPort();
            if (portName is null)
            {
                Publish(DeviceConnectionState.Disconnected, null, "未找到 VID 33C3 / PID F101 副屏");
                await BackoffAsync(attempt++, token).ConfigureAwait(false);
                continue;
            }

            Publish(DeviceConnectionState.Connecting, portName, "正在连接副屏");
            try
            {
                using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    WriteTimeout = 3000,
                    ReadTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true
                };
                port.Open();
                Publish(DeviceConnectionState.Connected, portName, "副屏已连接");
                attempt = 0;
                bool firstFrame = true;

                var staleReported = false;
                var previousSend = Stopwatch.GetTimestamp();
                while (!token.IsCancellationRequested)
                {
                    var frameStart = Stopwatch.GetTimestamp();
                    var jpeg = Volatile.Read(ref _latestFrame);
                    if (jpeg is null)
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                        previousSend = Stopwatch.GetTimestamp();
                        continue;
                    }
                    var gap = Stopwatch.GetElapsedTime(previousSend);
                    if (gap.TotalSeconds > 2) _diagnostic?.Invoke($"Frame transmission gap: {gap.TotalSeconds:F1} s");
                    var submitted = Interlocked.Read(ref _lastFrameSubmitted);
                    var stale = submitted != 0 && Stopwatch.GetElapsedTime(submitted).TotalSeconds > 3;
                    if (stale && !staleReported) _diagnostic?.Invoke("No new frame for 3 seconds; repeating the last frame.");
                    if (!stale && staleReported) _diagnostic?.Invoke("New frames resumed.");
                    staleReported = stale;
                    var header = Ds916Protocol.CreateHeader(jpeg.Length, firstFrame);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await port.BaseStream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                    await port.BaseStream.WriteAsync(jpeg, timeout.Token).ConfigureAwait(false);
                    await port.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
                    firstFrame = false;
                    previousSend = Stopwatch.GetTimestamp();
                    var remaining = TimeSpan.FromMilliseconds(1000d / 6) - Stopwatch.GetElapsedTime(frameStart);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
                }
            }
            catch (UnauthorizedAccessException)
            {
                Publish(DeviceConnectionState.Busy, portName, "串口被占用；请先退出 SEEIN-AIO");
                await BackoffAsync(attempt++, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Publish(DeviceConnectionState.Error, portName, $"副屏连接失败：{ex.Message}");
                await BackoffAsync(attempt++, token).ConfigureAwait(false);
            }
        }
    }

    private static Task BackoffAsync(int attempt, CancellationToken token)
    {
        int[] delays = [1, 2, 5, 10];
        return Task.Delay(TimeSpan.FromSeconds(delays[Math.Min(attempt, delays.Length - 1)]), token);
    }

    private void Publish(DeviceConnectionState state, string? port, string message) =>
        _statusChanged?.Invoke(new DeviceStatus(state, port, message, DateTimeOffset.Now));

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_runTask is not null)
            try { await _runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}

