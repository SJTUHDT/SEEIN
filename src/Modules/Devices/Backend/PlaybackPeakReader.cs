using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Seein.Core.Sensors;

public readonly record struct PlaybackLevels(double Master, double Left, double Right);

/// <summary>Reads the default playback endpoint's instantaneous peak, from silence (0) to full scale (1).</summary>
public sealed class PlaybackPeakReader : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _endpoint;
    private long _nextEndpointCheck;

    public double? Read() => ReadLevels()?.Master;

    public PlaybackLevels? ReadLevels()
    {
        try
        {
            // Follow default-output changes without enumerating devices for every animation frame.
            var now = Environment.TickCount64;
            if (now >= _nextEndpointCheck)
            {
                _nextEndpointCheck = now + 2000;
                _enumerator ??= new MMDeviceEnumerator();
                _endpoint?.Dispose();
                _endpoint = null;
                _endpoint = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            if (_endpoint is null) return null;
            if (_endpoint.AudioEndpointVolume.Mute) return default(PlaybackLevels);
            var meter = _endpoint.AudioMeterInformation;
            var master = Math.Clamp(meter.MasterPeakValue, 0f, 1f);
            var channels = meter.PeakValues;
            var count = channels.Count;
            var left = count > 0 ? Math.Clamp(channels[0], 0f, 1f) : master;
            var right = count > 1 ? Math.Clamp(channels[1], 0f, 1f) : left;
            return new(master, left, right);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _endpoint?.Dispose();
            _endpoint = null;
            return null;
        }
    }

    public void Dispose()
    {
        _endpoint?.Dispose();
        _enumerator?.Dispose();
        _endpoint = null;
        _enumerator = null;
        _nextEndpointCheck = 0;
    }
}
