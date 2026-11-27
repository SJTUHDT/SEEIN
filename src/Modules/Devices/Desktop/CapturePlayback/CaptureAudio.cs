using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Seein.Devices.CapturePlayback;

namespace Seein.Tray.CapturePlayback;

internal sealed class CaptureAudio : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice _input, _output;
    private readonly WasapiCapture _capture;
    private readonly WasapiOut _playback;
    private bool _disposed;
    public Exception? Failure { get; private set; }

    public static CaptureEndpoint[] Enumerate(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var endpoints = new List<CaptureEndpoint>();
        foreach (var device in devices)
            using (device) endpoints.Add(new(device.ID, device.FriendlyName));
        return endpoints.ToArray();
    }

    public CaptureAudio(string input, string output)
    {
        try
        {
            _input = _enumerator.GetDevice(input);
            _output = string.IsNullOrEmpty(output)
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _enumerator.GetDevice(output);
            _capture = new WasapiCapture(_input, true, 20);
            var buffer = new LiveAudioBuffer(_capture.WaveFormat.AverageBytesPerSecond, _capture.WaveFormat.BlockAlign);
            _playback = new WasapiOut(_output, AudioClientShareMode.Shared, true, 40);
            // Unity gain: Windows endpoint/session volume is the only volume control.
            _playback.Init(new PcmProvider(buffer, _capture.WaveFormat).ToSampleProvider());
            _capture.DataAvailable += (_, e) => buffer.Write(e.Buffer, 0, e.BytesRecorded);
            _capture.RecordingStopped += (_, e) => { if (!_disposed) Failure = e.Exception ?? new IOException("采集卡音频输入已断开"); };
            _playback.PlaybackStopped += (_, e) => { if (!_disposed) Failure = e.Exception ?? new IOException("音频输出已断开"); };
            _capture.StartRecording();
            _playback.Play();
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture?.StopRecording(); } catch { }
        try { _playback?.Stop(); } catch { }
        _capture?.Dispose(); _playback?.Dispose(); _input?.Dispose(); _output?.Dispose(); _enumerator.Dispose();
    }

    private sealed class PcmProvider(LiveAudioBuffer buffer, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;
        public int Read(byte[] data, int offset, int count) => buffer.Read(data, offset, count);
    }
}
