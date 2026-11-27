using SkiaSharp;
using System.Diagnostics;

namespace Seein.Core.Rendering;

// GIFs have per-frame delays. VideoCapture's nominal FPS can be 100 even when
// frames are meant to last 100 ms, so use the actual GIF frame metadata.
internal sealed class AnimatedGifBackground : IDisposable
{
    private readonly SKCodec _codec;
    private readonly SKBitmap _bitmap;
    private readonly int[] _ends;
    private readonly Stopwatch _clock=new();
    private int _frame=-1;
    public AnimatedGifBackground(string path)
    {
        _codec=SKCodec.Create(path)??throw new InvalidDataException("无法解码 GIF 背景。");
        var info=_codec.Info;
        if((long)info.Width*info.Height>64_000_000){_codec.Dispose();throw new InvalidDataException("GIF 画面超过 6400 万像素。");}
        _bitmap=new SKBitmap(info.Width,info.Height,SKColorType.Bgra8888,SKAlphaType.Premul);
        var frames=_codec.FrameInfo;_ends=new int[Math.Max(1,frames.Length)];var total=0;
        for(int i=0;i<_ends.Length;i++){total+=frames.Length>0?Math.Max(10,frames[i].Duration):100;_ends[i]=total;}
        _clock.Start();
    }
    public SKBitmap GetFrame(bool paused,double? seconds)
    {
        if(paused)_clock.Stop();else _clock.Start();
        if(paused&&_frame>=0)return _bitmap.Copy();
        var at=paused&&_frame<0?0:(long)((seconds??_clock.Elapsed.TotalSeconds)*1000)%_ends[^1];var index=Array.FindIndex(_ends,end=>at<end);if(index<0)index=0;
        if(_frame!=index)
        {
            _bitmap.Erase(SKColors.Transparent);
            var result=_codec.GetPixels(_bitmap.Info,_bitmap.GetPixels(),new SKCodecOptions(index));
            if(result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))throw new InvalidDataException("GIF 帧解码失败："+result);
            _frame=index;
        }
        return _bitmap.Copy();
    }
    public void Dispose(){_bitmap.Dispose();_codec.Dispose();}
}
