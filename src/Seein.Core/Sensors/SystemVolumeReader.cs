using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace Seein.Core.Sensors;

/// <summary>Reads the current Windows default playback-device master volume.</summary>
public sealed class SystemVolumeReader : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public double? ReadPercent()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Math.Clamp(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100d, 0d, 100d);
        }
        catch (COMException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose() => _enumerator.Dispose();
}
