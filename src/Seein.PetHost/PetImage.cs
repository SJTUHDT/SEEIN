using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using Image = System.Windows.Controls.Image;

namespace Seein.PetHost;

// Share SEEIN's existing Skia library for GIF/WebP; no additional browser or codec runtime.
internal sealed class PetImage : Image, IDisposable
{
    private readonly SKCodec _codec;
    private readonly SKBitmap _pixels;
    private readonly WriteableBitmap _bitmap;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private int _index;
    private int _repeat;
    private bool _disposed;

    public PetImage(string path) : this(Read(path)) { }
    public PetImage(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        _codec = SKCodec.Create(data) ?? throw new InvalidDataException("无法读取这张图片。");
        var info = _codec.Info;
        if (info.Width is <= 0 or > 4096 || info.Height is <= 0 or > 4096)
        { _codec.Dispose(); throw new InvalidDataException("图片尺寸超出限制。"); }
        info = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _pixels = new SKBitmap(info);
        _bitmap = new WriteableBitmap(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32, null);
        _frames = _codec.FrameInfo;
        Source = _bitmap;
        Stretch = Stretch.Uniform;
        _timer.Tick += (_, _) => Advance();
        Loaded += (_, _) => { if (!_disposed && _frames.Length > 1) _timer.Start(); };
        IsVisibleChanged += (_, _) =>
        {
            if (_disposed || _frames.Length < 2) return;
            if (IsVisible) _timer.Start(); else _timer.Stop();
        };
        Unloaded += (_, _) => Dispose();
        Draw();
    }

    private static byte[] Read(string path)
    {
        if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("图片文件过大。");
        return File.ReadAllBytes(path);
    }

    private void Draw()
    {
        var result = _codec.GetPixels(_pixels.Info, _pixels.GetPixels(), _pixels.RowBytes, new SKCodecOptions(_index));
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) throw new InvalidDataException("图片解码失败。");
        _bitmap.WritePixels(new Int32Rect(0, 0, _pixels.Width, _pixels.Height), _pixels.GetPixels(), _pixels.ByteCount, _pixels.RowBytes);
        if (_frames.Length > 1) _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames[_index].Duration));
    }

    private void Advance()
    {
        if (_disposed || !IsVisible) return;
        if (++_index >= _frames.Length)
        {
            _index = 0;
            if (_codec.RepetitionCount >= 0 && _repeat++ >= _codec.RepetitionCount) { _timer.Stop(); return; }
        }
        try { Draw(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _timer.Stop(); ToolTip = ex.Message; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _pixels.Dispose();
        _codec.Dispose();
    }
}
