using System.Buffers;
using System.IO;
using Vortice.WIC;

namespace Seein.Tray.CapturePlayback;

internal sealed class CaptureFrameDecoder : IDisposable
{
    private readonly IWICImagingFactory _factory = new();
    private byte[] _pixels = [];
    public (byte[] Pixels, int Width, int Height) Decode(byte[] bytes, int length)
    {
        // FlashCap supplies JPEG for MJPEG and a BMP container for uncompressed formats.
        using var stream = new MemoryStream(bytes, 0, length, false);
        using var image = _factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
        using var source = image.GetFrame(0);
        var size = source.Size;
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 8192 || size.Height > 8192)
            throw new InvalidDataException("采集画面尺寸无效");
        var count = checked(size.Width * size.Height * 4);
        if (_pixels.Length < count)
        {
            if (_pixels.Length > 0) ArrayPool<byte>.Shared.Return(_pixels);
            _pixels = ArrayPool<byte>.Shared.Rent(count);
        }
        using var converter = _factory.CreateFormatConverter();
        converter.Initialize(source, PixelFormat.Format32bppBGRA).CheckError();
        converter.CopyPixels((uint)(size.Width * 4), _pixels.AsSpan(0, count));
        return (_pixels, size.Width, size.Height);
    }
    public void Dispose() { _factory.Dispose(); if (_pixels.Length > 0) ArrayPool<byte>.Shared.Return(_pixels); _pixels = []; }
}
