using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Seein.Pet;

namespace Seein.PetHost;

internal sealed record AnimationFrame(byte[] Pixels, int Width, int Height, double Timestamp);

internal unsafe sealed class AnimationDecoder : IDisposable
{
    static AnimationDecoder()
    {
        var directory = Path.Combine(Seein.Core.ProcessNames.ModuleDirectory("companion"), "pet-codecs");
        if (!SetDefaultDllDirectories(0x1000) || AddDllDirectory(directory) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ffmpeg.RootPath = directory;
    }
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwsContext* _scale;
    private int _stream;
    private bool _eof;
    private readonly int _outputWidth;
    // Read and present are serialized by the player. Keep one displayed buffer and one decoding buffer.
    private readonly byte[]?[] _buffers = new byte[2][];
    private int _bufferIndex;
    public double Duration { get; private set; }

    public AnimationDecoder(string path, int outputWidth = 0)
    {
        _outputWidth = Math.Clamp(outputWidth, 0, 4096);
        ffmpeg.RootPath = Path.Combine(Seein.Core.ProcessNames.ModuleDirectory("companion"), "pet-codecs");
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        try
        {
            var format = ffmpeg.avformat_alloc_context();
            _format = format;
            Check(ffmpeg.avformat_open_input(&format, path, null, null));
            _format = format;
            Check(ffmpeg.avformat_find_stream_info(_format, null));
            _stream = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            Check(_stream);
            var parameters = _format->streams[_stream]->codecpar;
            var codec = parameters->codec_id switch
            {
                AVCodecID.AV_CODEC_ID_VP9 => ffmpeg.avcodec_find_decoder_by_name("libvpx-vp9"),
                AVCodecID.AV_CODEC_ID_VP8 => ffmpeg.avcodec_find_decoder_by_name("libvpx"),
                _ => ffmpeg.avcodec_find_decoder(parameters->codec_id)
            };
            if (codec == null) throw new InvalidDataException("桌宠动画编码暂不支持。");
            _codec = ffmpeg.avcodec_alloc_context3(codec);
            if (_codec == null) throw new OutOfMemoryException();
            Check(ffmpeg.avcodec_parameters_to_context(_codec, parameters));
            _codec->thread_count = 2;
            Check(ffmpeg.avcodec_open2(_codec, codec, null));
            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_frame == null || _packet == null) throw new OutOfMemoryException();
            Duration = _format->duration > 0 ? _format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        }
        catch { Dispose(); throw; }
    }

    public AnimationFrame? Read(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var result = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (result >= 0) return ConvertFrame();
            if (result == ffmpeg.AVERROR_EOF || _eof) return null;
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(result);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = ffmpeg.av_read_frame(_format, _packet);
                if (read == ffmpeg.AVERROR_EOF)
                {
                    _eof = true;
                    Check(ffmpeg.avcodec_send_packet(_codec, null));
                    break;
                }
                Check(read);
                try
                {
                    if (_packet->stream_index != _stream) continue;
                    Check(ffmpeg.avcodec_send_packet(_codec, _packet));
                    break;
                }
                finally { ffmpeg.av_packet_unref(_packet); }
            }
        }
        token.ThrowIfCancellationRequested();
        return null;
    }

    private AnimationFrame ConvertFrame()
    {
        var sourceWidth = _frame->width;
        var sourceHeight = _frame->height;
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > 4096 || sourceHeight > 4096) throw new InvalidDataException("桌宠动画尺寸超出限制。");
        var width = _outputWidth == 0 ? sourceWidth : Math.Min(_outputWidth, sourceWidth);
        var height = Math.Max(1, (int)Math.Round(sourceHeight * (double)width / sourceWidth));
        var length = checked(width * height * 4);
        var slot = _bufferIndex++ % 2;
        var bytes = _buffers[slot];
        if (bytes is null || bytes.Length != length) _buffers[slot] = bytes = new byte[length];
        _scale = ffmpeg.sws_getCachedContext(_scale, sourceWidth, sourceHeight, (AVPixelFormat)_frame->format, width, height,
            AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_LANCZOS, null, null, null);
        if (_scale == null) throw new InvalidDataException("动画像素转换失败。");
        var colorSpace = _frame->colorspace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
            AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
            _ => ffmpeg.SWS_CS_DEFAULT
        };
        var coefficients = ffmpeg.sws_getCoefficients(colorSpace);
        var table = new int_array4();
        for (uint index = 0; index < 4; index++) table[index] = coefficients[index];
        Check(ffmpeg.sws_setColorspaceDetails(_scale, in table, _frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0, in table, 1, 0, 1 << 16, 1 << 16));
        fixed (byte* target = bytes)
        {
            var destination = new byte_ptrArray4();
            var strides = new int_array4();
            destination[0] = target;
            strides[0] = width * 4;
            Check(ffmpeg.sws_scale(_scale, _frame->data, _frame->linesize, 0, sourceHeight, destination, strides));
            PetPixels.PrepareForLayeredWindow(bytes);
        }
        var timeBase = _format->streams[_stream]->time_base;
        var timestamp = _frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? 0 : _frame->best_effort_timestamp * (double)timeBase.num / timeBase.den;
        ffmpeg.av_frame_unref(_frame);
        return new(bytes, width, height, timestamp);
    }

    public void Rewind()
    {
        Check(ffmpeg.av_seek_frame(_format, _stream, 0, ffmpeg.AVSEEK_FLAG_BACKWARD));
        ffmpeg.avcodec_flush_buffers(_codec);
        _eof = false;
    }

    private static void Check(int code)
    {
        if (code < 0) throw new InvalidDataException($"动画解码失败（{code}）。");
    }

    public void Dispose()
    {
        if (_scale != null) { ffmpeg.sws_freeContext(_scale); _scale = null; }
        var frame = _frame; if (frame != null) { ffmpeg.av_frame_free(&frame); _frame = null; }
        var packet = _packet; if (packet != null) { ffmpeg.av_packet_free(&packet); _packet = null; }
        var codec = _codec; if (codec != null) { ffmpeg.avcodec_free_context(&codec); _codec = null; }
        var format = _format; if (format != null) { ffmpeg.avformat_close_input(&format); _format = null; }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string path);
}
