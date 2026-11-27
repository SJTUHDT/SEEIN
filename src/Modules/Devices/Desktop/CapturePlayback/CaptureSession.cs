using System.Diagnostics;
using System.IO;
using FlashCap;
using Seein.Devices.CapturePlayback;

namespace Seein.Tray.CapturePlayback;

internal sealed record CaptureVideoDevice(CaptureDeviceDescriptor Descriptor)
{
    public string Id => Descriptor.Identity.ToString() ?? "";
    public string Name => Descriptor.Name;
    public CaptureMode[] Modes => Descriptor.Characteristics.Select(ToMode).DistinctBy(m => m.Key)
        .OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.FramesPerSecond).ToArray();
    internal static CaptureMode ToMode(VideoCharacteristics mode) => new(mode.Width, mode.Height,
        mode.FramesPerSecond.Numerator, mode.FramesPerSecond.Denominator, mode.PixelFormat.ToString());
}

internal interface ICaptureConnection : IAsyncDisposable
{
    CaptureMode Mode { get; }
    Task StopAsync();
}

internal sealed class FlashCapConnection(CaptureDevice device) : ICaptureConnection
{
    public CaptureMode Mode => CaptureVideoDevice.ToMode(device.Characteristics);
    public Task StopAsync() => device.StopAsync();
    public ValueTask DisposeAsync() => new(device.DisposeAsync());
}

internal sealed class CaptureSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Task? _run;
    private CaptureAudio? _audio;
    private long _received, _lastFrame;
    private readonly Func<CaptureProfile, PixelBufferArrivedDelegate, CancellationToken, Task<ICaptureConnection>> _openVideo;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    public CaptureRenderer Renderer { get; } = new();
    public string Status { get; private set; } = "尚未开始播放";
    public string AudioStatus { get; private set; } = "";
    public CaptureMode? ActiveMode { get; private set; }
    public long Received => Interlocked.Read(ref _received);
    public bool IsActive { get { lock (_gate) return _run is { IsCompleted: false }; } }

    public CaptureSession(
        Func<CaptureProfile, PixelBufferArrivedDelegate, CancellationToken, Task<ICaptureConnection>>? openVideo = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _openVideo = openVideo ?? OpenVideoAsync;
        _delay = delay ?? Task.Delay;
    }

    // Only the explicit UI refresh/start paths call this: construction never opens the capture card.
    public static CaptureVideoDevice[] EnumerateVideo() => new CaptureDevices().EnumerateDescriptors()
        .Where(d => d.DeviceType == DeviceTypes.DirectShow && d.Characteristics.Length > 0)
        .Select(d => new CaptureVideoDevice(d)).ToArray();

    public void Start(CaptureProfile profile)
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) return;
            _cancel?.Dispose(); _cancel = new();
            _received = 0; ActiveMode = null;
            Status = "正在连接采集卡…";
            var token = _cancel.Token;
            _run = Task.Run(() => RunAsync(profile, token));
        }
    }

    private async Task RunAsync(CaptureProfile profile, CancellationToken token)
    {
        var reconnecting = false; var attempt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                ICaptureConnection? device = null;
                try
                {
                    device = await _openVideo(profile, OnFrame, token).ConfigureAwait(false);
                    ActiveMode = device.Mode;
                    Status = "正在播放";
                    reconnecting = true; attempt = 0;
                    Interlocked.Exchange(ref _lastFrame, Stopwatch.GetTimestamp());
                    var lastAudioAttempt = DateTime.MinValue;
                    while (!token.IsCancellationRequested)
                    {
                        await _delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrame)).TotalSeconds > 6)
                            throw new IOException("采集卡暂时没有送出视频帧；正在重新连接");
                        if ((DateTime.UtcNow - lastAudioAttempt).TotalSeconds >= 3)
                        {
                            EnsureAudio(profile);
                            lastAudioAttempt = DateTime.UtcNow;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Renderer.Clear();
                    ActiveMode = null;
                    Status = ex.Message;
                    // Initial ownership/mode failures need user action, not an endless device-open loop.
                    if (!reconnecting && ex is not IOException) break;
                }
                finally
                {
                    lock (_gate) { _audio?.Dispose(); _audio = null; }
                    if (device != null)
                    {
                        try { await device.StopAsync().ConfigureAwait(false); } catch { }
                        try { await device.DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                }
                if (token.IsCancellationRequested) break;
                Status = "等待同一采集卡恢复连接…";
                await _delay(CapturePolicy.ReconnectDelay(attempt++), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Status = "播放失败：" + ex.Message; }
        finally
        {
            ActiveMode = null;
            Renderer.Clear();
            if (token.IsCancellationRequested) { Status = "已停止播放，采集卡已释放"; AudioStatus = ""; }
        }
    }

    private static async Task<ICaptureConnection> OpenVideoAsync(CaptureProfile profile, PixelBufferArrivedDelegate onFrame, CancellationToken token)
    {
        var descriptor = EnumerateVideo().FirstOrDefault(d => d.Id == profile.VideoId)
            ?? throw new IOException("未找到所选采集卡");
        var mode = CapturePolicy.SelectMode(descriptor.Modes, profile.ModeKey)
            ?? throw new InvalidOperationException("所选视频模式已不可用，请停止后重新选择");
        var candidates = new List<CaptureMode> { mode };
        if (profile.ModeKey.Length == 0)
            foreach (var fallback in descriptor.Modes.Where(m =>
                m.Width == 2560 && m.Height == 1440 && Math.Abs(m.FramesPerSecond - 60) < .15 ||
                m.Width == 1920 && m.Height == 1080 && Math.Abs(m.FramesPerSecond - 60) < .15))
                if (!candidates.Contains(fallback)) candidates.Add(fallback);
        Exception? openError = null;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            CaptureDevice? device = null;
            try
            {
                var characteristic = descriptor.Descriptor.Characteristics.First(c => CaptureVideoDevice.ToMode(c).Key == candidate.Key);
                var firstFrame = new CaptureFirstFrameGate();
                void Receive(PixelBufferScope scope) { firstFrame.FrameArrived(); onFrame(scope); }
                device = await descriptor.Descriptor.OpenAsync(characteristic, TranscodeFormats.Auto, false, 1, (PixelBufferArrivedDelegate)Receive, token).ConfigureAwait(false);
                await device.StartAsync(token).ConfigureAwait(false);
                // IsRunning only describes the graph. Actual sample delivery must be verified
                // before reporting success, starting audio, or enabling the reconnect watchdog.
                await firstFrame.WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
                return new FlashCapConnection(device);
            }
            catch (Exception ex)
            {
                if (device != null) await device.DisposeAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException) throw;
                openError = ex;
            }
        }
        throw new InvalidOperationException(openError is TimeoutException
            ? "采集卡未送出视频帧。请先退出 OBS 或停用其中的视频采集源，再检查设备输入信号与 USB 连接，然后点击开始播放。"
            : "无法打开采集卡。若 OBS 正在使用它，请先停用 OBS 的视频采集源。" + openError?.Message, openError);
    }

    private void OnFrame(PixelBufferScope scope)
    {
        try
        {
            Interlocked.Increment(ref _received);
            Interlocked.Exchange(ref _lastFrame, Stopwatch.GetTimestamp());
            if (Renderer.HasVisibleTarget) Renderer.Offer(new EncodedCaptureFrame(scope.Buffer.ReferImage()));
        }
        catch (Exception ex) { Status = "视频帧读取失败：" + ex.Message; }
        finally { scope.ReleaseNow(); }
    }

    private void EnsureAudio(CaptureProfile profile)
    {
        lock (_gate)
        {
            if (profile.AudioId.Length == 0) { AudioStatus = "未选择音频输入（不会自动启用麦克风）"; return; }
            if (_audio?.Failure == null && _audio != null) return;
            var hadFailure = _audio?.Failure != null;
            _audio?.Dispose(); _audio = null;
            try
            {
                // If an output disappears, retry the system default. Never change Windows master volume.
                try { _audio = new(profile.AudioId, hadFailure ? "" : profile.OutputId); }
                catch when (profile.OutputId.Length > 0) { _audio = new(profile.AudioId, ""); hadFailure = true; }
                AudioStatus = hadFailure ? "音频已改用系统默认输出 · 跟随系统音量" : "采集卡音频已连接 · 跟随系统音量";
            }
            catch (Exception ex) { AudioStatus = "音频暂不可用，将自动重试：" + ex.Message; }
        }
    }

    public async Task StopAsync()
    {
        Task? run;
        lock (_gate) { _cancel?.Cancel(); run = _run; }
        if (run != null) await run.ConfigureAwait(false);
        Renderer.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate) { _cancel?.Dispose(); _cancel = null; }
        Renderer.Dispose();
    }
}
