using Seein.Core;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Seein.Tray;

public sealed class MediaSessionCollector : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task<MediaSnapshot?> ReadAsync(CancellationToken token)
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);
        var session = _manager.GetCurrentSession();
        if (session is null) return null;
        var properties = await session.TryGetMediaPropertiesAsync().AsTask(token);
        var playback = session.GetPlaybackInfo();
        byte[]? artwork = null;
        if (properties.Thumbnail is not null)
        {
            using var stream = await properties.Thumbnail.OpenReadAsync().AsTask(token);
            using var reader = new DataReader(stream);
            var size = (uint)Math.Min(stream.Size, 5 * 1024 * 1024);
            await reader.LoadAsync(size).AsTask(token);
            artwork = new byte[(int)size];
            reader.ReadBytes(artwork);
        }
        return new MediaSnapshot(DateTimeOffset.Now, properties.Title ?? "", properties.Artist ?? "", properties.AlbumTitle ?? "",
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, artwork);
    }

    public void Dispose() { _manager = null; }
}

