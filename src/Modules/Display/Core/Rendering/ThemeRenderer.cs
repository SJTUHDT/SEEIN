using Seein.Core.Themes;
using OpenCvSharp;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Globalization;

namespace Seein.Core.Rendering;

public sealed partial class ThemeRenderer : IDisposable
{
    private readonly ConcurrentDictionary<string, SKBitmap> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<float>> _history = new(StringComparer.OrdinalIgnoreCase);
    private VideoBackground? _video;
    private string? _videoPath;

    public byte[] RenderJpeg(LoadedTheme theme, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media)
        => RenderEncoded(theme, sensors, power, media, new(), SKEncodedImageFormat.Jpeg);

    public byte[] RenderPng(LoadedTheme theme, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media, ThemeRenderOptions? options = null)
        => RenderEncoded(theme, sensors, power, media, options ?? new(), SKEncodedImageFormat.Png);

    private byte[] RenderEncoded(LoadedTheme theme, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media, ThemeRenderOptions options, SKEncodedImageFormat encoding)
    {
        using var surface=DrawFrame(theme,sensors,power,media,options,1);
        using var image=surface.Snapshot();using var data=image.Encode(encoding,theme.Manifest.JpegQuality);return data.ToArray();
    }
    public ThemePreviewFrame RenderPreview(LoadedTheme theme, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media, float scale=1, ThemeRenderOptions? options=null)
    {
        using var surface=DrawFrame(theme,sensors,power,media,options??new(),Math.Clamp(scale,.05f,1));
        using var bitmap=new SKBitmap(surface.Canvas.DeviceClipBounds.Width,surface.Canvas.DeviceClipBounds.Height,SKColorType.Bgra8888,SKAlphaType.Premul);
        if(!surface.ReadPixels(bitmap.Info,bitmap.GetPixels(),bitmap.RowBytes,0,0))throw new InvalidOperationException("预览帧读取失败。");
        return new(bitmap.Width,bitmap.Height,bitmap.RowBytes,bitmap.Bytes);
    }
    private SKSurface DrawFrame(LoadedTheme theme, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media, ThemeRenderOptions options,float scale)
    {
        _options = options; _sampleTime = sensors.Timestamp; _assetRoot = theme.RootDirectory;
        var manifest = theme.Manifest;
        var surface = SKSurface.Create(new SKImageInfo(Math.Max(1,(int)Math.Round(manifest.Canvas.Width*scale)),Math.Max(1,(int)Math.Round(manifest.Canvas.Height*scale)), SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
        var canvas = surface.Canvas;
        canvas.Scale(scale);
        canvas.Clear(ParseColor(manifest.BackgroundColor, SKColors.Black));
        DrawBackground(canvas, theme);

        foreach (var layer in manifest.Layers.OrderBy(l => l.Z))
            DrawLayer(canvas, layer, theme, sensors, power, media);

        // Portrait DS916 panels decode a portrait JPEG directly. Rotating it
        // into a landscape frame makes the panel render only a fraction of it.
        return surface;
        }catch{surface.Dispose();throw;}
    }

    private void DrawBackground(SKCanvas canvas, LoadedTheme theme)
    {
        if (theme.Manifest.Background is null) return;
        var path = ResolveAsset(theme, theme.Manifest.Background);
        var ext = Path.GetExtension(path);
        if (ext.Equals(".gif",StringComparison.OrdinalIgnoreCase))
        {
            if(_gifPath!=path){_gif?.Dispose();_gif=new AnimatedGifBackground(path);_gifPath=path;}
            using var frame=_gif!.GetFrame(_options.PauseAnimation,_options.AnimationSeconds);DrawBackgroundImage(canvas,frame,theme.Manifest);return;
        }
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(path, _videoPath, StringComparison.OrdinalIgnoreCase))
            {
                _video?.Dispose();
                _video = new VideoBackground(path);
                _videoPath = path;
            }
            using var frame = _video!.GetFrame(_options.PauseAnimation,_options.AnimationSeconds);
            if (frame is not null) DrawBackgroundImage(canvas, frame, theme.Manifest);
            return;
        }

        var bitmap = LoadBitmap(path);
        DrawBackgroundImage(canvas, bitmap, theme.Manifest);
    }

    private void DrawLayer(SKCanvas canvas, ThemeLayer layer, LoadedTheme theme, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media)
    {
        if (!layer.Visible || layer.Opacity <= 0) return;
        canvas.Save();
        try
        {
        var rect = new SKRect(layer.X, layer.Y, layer.X + layer.Width, layer.Y + layer.Height);
        canvas.Translate(rect.MidX, rect.MidY); canvas.RotateDegrees(layer.Rotation); canvas.Translate(-rect.MidX, -rect.MidY);
        using var opacity = new SKPaint { Color = SKColors.White.WithAlpha((byte)(Math.Clamp(layer.Opacity, 0, 1) * 255)) };
        canvas.SaveLayer(opacity);
        try
        {
        var value = ResolveValue(layer.Binding, sensors, power, media);
        layer = AlarmStyle(layer, value);
        if (DrawExtended(canvas, layer, rect, value, theme.Manifest)) return;
        switch (layer.Type.ToLowerInvariant())
        {
            case "rect": DrawRectangle(canvas, layer, rect); break;
            case "text": DrawText(canvas, layer, rect, layer.Format ?? string.Empty, theme.Manifest.FontSizeOffset); break;
            case "metric": DrawText(canvas, layer, rect, FormatValue(ScaleValue(value, StyleFloat(layer, "valueScale", 1)), layer.Format), theme.Manifest.FontSizeOffset); break;
            case "clock": DrawText(canvas, layer, rect, SafeClock(layer.Format), theme.Manifest.FontSizeOffset); break;
            case "bar": DrawBar(canvas, layer, rect, value is double barValue ? barValue : null); break;
            case "ring": DrawRing(canvas, layer, rect, value is double ringValue ? ringValue : null); break;
            case "sparkline": DrawSparkline(canvas, layer, rect, value is double historyValue ? historyValue : null); break;
            case "image":
                if (layer.Asset is not null) DrawCover(canvas, LoadBitmap(ResolveAsset(theme, layer.Asset)), rect);
                break;
            case "media-cover":
                if (media?.ArtworkPng is { Length: > 0 })
                    using (var bitmap = SKBitmap.Decode(media.ArtworkPng)) DrawCover(canvas, bitmap, rect);
                break;
        }
        } finally { canvas.Restore(); }
        } finally { canvas.Restore(); }
    }

    private static void DrawRectangle(SKCanvas canvas, ThemeLayer layer, SKRect rect)
    {
        var radius = StyleFloat(layer, "radius", 0);
        using var fill = new SKPaint { Color = StyleColor(layer, "color", SKColors.Transparent), IsAntialias = true };
        canvas.DrawRoundRect(rect, radius, radius, fill);

        var borderWidth = StyleFloat(layer, "borderWidth", 0);
        if (borderWidth <= 0) return;
        using var border = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = borderWidth,
            Color = StyleColor(layer, "borderColor", new SKColor(90, 98, 145)),
            IsAntialias = true
        };
        var inset = borderWidth / 2;
        canvas.DrawRoundRect(new SKRect(rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset), radius, radius, border);
    }

    private void DrawText(SKCanvas canvas, ThemeLayer layer, SKRect rect, string text, float fontSizeOffset = 0)
    {
        if (layer.Style.ContainsKey("align") || layer.Style.ContainsKey("fontAsset") || StyleFloat(layer, "wrap", 0) != 0)
        { DrawEditorText(canvas, layer, rect, text, fontSizeOffset); return; }
        using var paint = new SKPaint { IsAntialias = true, Color = StyleColor(layer, "color", SKColors.White) };
        var weight = string.Equals(StyleString(layer, "fontWeight", "normal"), "bold", StringComparison.OrdinalIgnoreCase)
            ? SKFontStyle.Bold : SKFontStyle.Normal;
        var family=StyleString(layer,"fontFamily","Microsoft YaHei UI");var fontKey=family+"|"+StyleString(layer,"fontWeight","normal");
        if(!_fonts.TryGetValue(fontKey,out var typeface))_fonts[fontKey]=typeface=SKTypeface.FromFamilyName(family,weight);
        using var font = new SKFont(typeface, StyleFloat(layer, "fontSize", Math.Max(12, rect.Height * .42f)) + fontSizeOffset + StyleFloat(layer, "fontSizeAdjust", 0));
        paint.Color = StyleColor(layer, "color", SKColors.White);
        var metrics = font.Metrics;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lineHeight = font.Size * 1.15f;
        var firstBaseline = rect.MidY - (lines.Length - 1) * lineHeight / 2 - (metrics.Ascent + metrics.Descent) / 2;
        for (var i = 0; i < lines.Length; i++)
            canvas.DrawText(lines[i], rect.Left, firstBaseline + i * lineHeight, SKTextAlign.Left, font, paint);
    }

    private static void DrawBar(SKCanvas canvas, ThemeLayer layer, SKRect rect, double? value)
    {
        var min = StyleFloat(layer, "min", 0); var max = StyleFloat(layer, "max", 100);
        var t = Math.Clamp(((float)(value ?? min) - min) / Math.Max(0.001f, max - min), 0, 1);
        using var background = new SKPaint { Color = StyleColor(layer, "trackColor", new SKColor(255, 255, 255, 35)), IsAntialias = true };
        using var foreground = new SKPaint { Color = StyleColor(layer, "color", new SKColor(64, 210, 190)), IsAntialias = true };
        var radius = StyleFloat(layer, "radius", Math.Min(rect.Width, rect.Height) * .12f);
        canvas.DrawRoundRect(rect, radius, radius, background);
        var fill = new SKRect(rect.Left, rect.Top, rect.Left + rect.Width * t, rect.Bottom);
        canvas.DrawRoundRect(fill, radius, radius, foreground);
    }

    private static void DrawRing(SKCanvas canvas, ThemeLayer layer, SKRect rect, double? value)
    {
        var min = StyleFloat(layer, "min", 0); var max = StyleFloat(layer, "max", 100);
        var t = Math.Clamp(((float)(value ?? min) - min) / Math.Max(0.001f, max - min), 0, 1);
        var stroke = StyleFloat(layer, "strokeWidth", 10);
        var oval = new SKRect(rect.Left + stroke, rect.Top + stroke, rect.Right - stroke, rect.Bottom - stroke);
        using var track = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round, Color = StyleColor(layer, "trackColor", new SKColor(255, 255, 255, 35)), IsAntialias = true };
        using var active = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round, Color = StyleColor(layer, "color", new SKColor(96, 180, 255)), IsAntialias = true };
        canvas.DrawArc(oval, -90, 360, false, track);
        canvas.DrawArc(oval, -90, 360 * t, false, active);
    }

    private void DrawSparkline(SKCanvas canvas, ThemeLayer layer, SKRect rect, double? value)
    {
        if (!_history.TryGetValue(layer.Id, out var points)) _history[layer.Id] = points = new Queue<float>();
        if (value is null) return;
        if (!_historyTimes.TryGetValue(layer.Id, out var previous) || previous != _sampleTime)
        { points.Enqueue((float)value); _historyTimes[layer.Id] = _sampleTime; }
        while (points.Count > 120) points.Dequeue();
        if (points.Count < 2) return;
        var min = StyleFloat(layer, "min", points.Min()); var max = StyleFloat(layer, "max", points.Max());
        if (Math.Abs(max - min) < .001f) max = min + 1;
        using var path = new SKPath();
        var array = points.ToArray();
        for (var i = 0; i < array.Length; i++)
        {
            var x = rect.Left + i * rect.Width / (array.Length - 1);
            var y = rect.Bottom - Math.Clamp((array[i] - min) / (max - min), 0, 1) * rect.Height;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = StyleFloat(layer, "strokeWidth", 3), Color = StyleColor(layer, "color", new SKColor(64, 210, 190)), IsAntialias = true };
        canvas.DrawPath(path, paint);
    }

    private static object? ResolveValue(string? binding, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media) => binding switch
        { _ => ResolveBinding(binding,sensors,power,media) };
    public static object? ResolveBinding(string? binding, SensorSnapshot sensors, PowerSnapshot? power, MediaSnapshot? media) => binding switch
    {
        null => null,
        "media.title" => media?.Title,
        "media.artist" => media?.Artist,
        "media.album" => media?.Album,
        SensorKeys.SessionEnergy => power?.SessionWh,
        SensorKeys.DailyEnergy => power?.TodayWh,
        SensorKeys.MonthlyEnergy => power?.MonthWh,
        SensorKeys.TotalEnergy => power?.TotalWh,
        SensorKeys.EstimatedSystemPower => power?.Watts,
        _ => sensors.Get(binding)
    };

    private static string FormatValue(object? value, string? format)
    {
        if (value is null) return "--";
        if (value is string text) return text;
        if (value is double number)
        {
            if (string.IsNullOrWhiteSpace(format)) return number.ToString("0.#", CultureInfo.CurrentCulture);
            if (string.Equals(format, "rate", StringComparison.OrdinalIgnoreCase)) return FormatRate(number);
            if (string.Equals(format, "duration", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromSeconds(Math.Max(0, number)).ToString(@"dd\.hh\:mm\:ss", CultureInfo.InvariantCulture);
            try { return number.ToString(format, CultureInfo.CurrentCulture); }
            catch (FormatException) { return number.ToString("0.#", CultureInfo.CurrentCulture); }
        }
        return value.ToString() ?? "--";
    }

    private static object? ScaleValue(object? value, float scale) =>
        value is double number ? number * scale : value;

    private static string FormatRate(double bytesPerSecond)
    {
        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private SKBitmap LoadBitmap(string path) => _images.GetOrAdd(path, static p => SKBitmap.Decode(p) ?? throw new InvalidDataException($"无法解码图像 {p}"));
    private static string ResolveAsset(LoadedTheme theme, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(theme.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(theme.RootDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资源路径越界");
        return path;
    }

    private static void DrawCover(SKCanvas canvas, SKBitmap bitmap, SKRect destination)
    {
        var scale = Math.Max(destination.Width / bitmap.Width, destination.Height / bitmap.Height);
        var sourceWidth = destination.Width / scale; var sourceHeight = destination.Height / scale;
        var source = new SKRect((bitmap.Width - sourceWidth) / 2, (bitmap.Height - sourceHeight) / 2, (bitmap.Width + sourceWidth) / 2, (bitmap.Height + sourceHeight) / 2);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, source, destination, paint);
    }

    private static SKColor ParseColor(string? value, SKColor fallback) => value is not null && SKColor.TryParse(value, out var color) ? color : fallback;
    private static string StyleString(ThemeLayer layer, string key, string fallback) => layer.Style.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static float StyleFloat(ThemeLayer layer, string key, float fallback) => layer.Style.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetSingle(out var result) && float.IsFinite(result) ? result : fallback;
    private static SKColor StyleColor(ThemeLayer layer, string key, SKColor fallback) => ParseColor(StyleString(layer, key, null!), fallback);

    public void Dispose()
    {
        _video?.Dispose();
        _gif?.Dispose();
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        foreach (var font in _fonts.Values) font.Dispose(); _fonts.Clear();
    }
}

internal sealed class VideoBackground : IDisposable
{
    private readonly VideoCapture _capture;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private SKBitmap? _last;
    public VideoBackground(string path) { _capture = new VideoCapture(path); if (!_capture.IsOpened()) throw new InvalidDataException("无法解码动态背景，请使用其他视频格式。"); _clock.Start(); }

    public SKBitmap? GetFrame(bool paused = false,double? seconds=null)
    {
        if (paused) _clock.Stop(); else _clock.Start();
        if (paused && _last is not null) return _last.Copy();
        var fps = _capture.Get(VideoCaptureProperties.Fps); var count = _capture.Get(VideoCaptureProperties.FrameCount);
        if (fps > 0 && count > 1)
        {
            var target = (long)((seconds??_clock.Elapsed.TotalSeconds) * fps) % (long)count;
            var next = (long)_capture.Get(VideoCaptureProperties.PosFrames);
            if (_last is not null && target == (next - 1 + (long)count) % (long)count) return _last.Copy();
            if (Math.Abs(target - next) > 2 || seconds is not null&&target!=next) _capture.Set(VideoCaptureProperties.PosFrames, target);
        }
        using var frame = new Mat();
        if (!_capture.Read(frame) || frame.Empty())
        {
            _capture.Set(VideoCaptureProperties.PosFrames, 0);
            if (!_capture.Read(frame) || frame.Empty()) return null;
        }
        Cv2.ImEncode(".jpg", frame, out var bytes);
        _last?.Dispose(); _last = SKBitmap.Decode(bytes); return _last?.Copy();
    }

    public void Dispose() { _last?.Dispose(); _capture.Dispose(); }
}

