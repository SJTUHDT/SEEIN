using Seein.Core.Themes;
using SkiaSharp;
using System.Globalization;
using System.Text.Json;

namespace Seein.Core.Rendering;

public sealed record ThemeRenderOptions(bool PauseAnimation = false, DateTimeOffset? Clock = null)
{ public double? AnimationSeconds { get; init; } }
public sealed record ThemePreviewFrame(int Width,int Height,int Stride,byte[] Pixels);

public sealed partial class ThemeRenderer
{
    private ThemeRenderOptions _options = new();
    private DateTimeOffset _sampleTime;
    private string _assetRoot = "";
    private readonly Dictionary<string, DateTimeOffset> _historyTimes = [];
    private readonly Dictionary<string, SKTypeface> _fonts = [];
    private AnimatedGifBackground? _gif;
    private string? _gifPath;
    internal int HistoryCount(string id) => _history.GetValueOrDefault(id)?.Count ?? 0;
    private string SafeClock(string? format)
    {
        try { return (_options.Clock ?? DateTimeOffset.Now).ToString(format ?? "HH:mm", CultureInfo.CurrentCulture); }
        catch (FormatException) { return "时间格式无效"; }
    }
    private static ThemeLayer AlarmStyle(ThemeLayer layer, object? value)
    {
        if (value is not double number) return layer;
        var color = number >= StyleFloat(layer, "critical", float.MaxValue) ? StyleString(layer, "criticalColor", "#FF6868") :
            number >= StyleFloat(layer, "warning", float.MaxValue) ? StyleString(layer, "warningColor", "#FFC857") : null;
        if (color is null) return layer;
        var style = new Dictionary<string, JsonElement>(layer.Style, StringComparer.OrdinalIgnoreCase) { ["color"] = JsonSerializer.SerializeToElement(color) };
        return layer with { Style = style };
    }
    private void DrawBackgroundImage(SKCanvas canvas, SKBitmap bitmap, ThemeManifest manifest)
    {
        var layer = new ThemeLayer { Id = "background", Type = "image", Style = manifest.BackgroundStyle };
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(Math.Clamp(StyleFloat(layer, "opacity", 1), 0, 1) * 255)), IsAntialias = true };
        canvas.SaveLayer(paint);
        DrawFitted(canvas, bitmap, new(0, 0, manifest.Canvas.Width, manifest.Canvas.Height), layer);
        canvas.Restore();
    }
    private static void DrawFitted(SKCanvas canvas, SKBitmap bitmap, SKRect rect, ThemeLayer layer)
    {
        var mode = StyleString(layer, "fit", "cover");
        if (mode == "stretch") { canvas.DrawBitmap(bitmap, rect); return; }
        if (mode == "contain")
        {
            var scale = Math.Min(rect.Width / bitmap.Width, rect.Height / bitmap.Height);
            var w = bitmap.Width * scale; var h = bitmap.Height * scale;
            canvas.DrawBitmap(bitmap, new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2)); return;
        }
        var cover = Math.Max(rect.Width / bitmap.Width, rect.Height / bitmap.Height);
        var sw = rect.Width / cover; var sh = rect.Height / cover;
        var x = (bitmap.Width - sw) * Math.Clamp(StyleFloat(layer, "cropX", .5f), 0, 1);
        var y = (bitmap.Height - sh) * Math.Clamp(StyleFloat(layer, "cropY", .5f), 0, 1);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, new SKRect(x, y, x + sw, y + sh), rect, paint);
    }
    private bool DrawExtended(SKCanvas canvas, ThemeLayer layer, SKRect rect, object? value, ThemeManifest manifest)
    {
        var type = layer.Type.ToLowerInvariant();
        if (type == "image" && layer.Asset is not null) { DrawFitted(canvas, LoadBitmap(ResolveAsset(new(manifest, _assetRoot), layer.Asset)), rect, layer); return true; }
        if (type is "circle" or "line")
        {
            using var paint = new SKPaint { IsAntialias = true, Color = StyleColor(layer, "color", SKColors.White), StrokeWidth = StyleFloat(layer, "strokeWidth", 3) };
            if (type == "circle") canvas.DrawOval(rect, paint); else canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, paint);
            return true;
        }
        if (manifest.SchemaVersion != "1.1") return false;
        if (type == "metric")
        {
            DrawEditorText(canvas, layer, rect, FormatValue(ScaleValue(value, StyleFloat(layer, "valueScale", 1)), layer.Format) + StyleString(layer, "unit", ""), manifest.FontSizeOffset); return true;
        }
        if (type is "bar" or "ring" or "gauge")
        {
            var number = value is double v && double.IsFinite(v) ? (double?)v : null;
            var min = StyleFloat(layer, "min", 0); var max = Math.Max(min + .001f, StyleFloat(layer, "max", 100));
            var t = (float)Math.Clamp(((number ?? min) - min) / (max - min), 0, 1);
            var color = StyleColor(layer, "color", SKColor.Parse("#60B4FF"));
            using var track = new SKPaint { Color = StyleColor(layer, "trackColor", new SKColor(255, 255, 255, 35)), IsAntialias = true };
            using var active = new SKPaint { Color = color, IsAntialias = true };
            if (type == "bar")
            {
                var radius = StyleFloat(layer, "radius", 6); canvas.DrawRoundRect(rect, radius, radius, track);
                if (number is not null)
                {
                    var vertical = StyleString(layer, "direction", "horizontal") == "vertical";
                    var segments = Math.Clamp((int)StyleFloat(layer, "segments", 1), 1, 100);
                    for (var i = 0; i < segments; i++)
                    {
                        var a = (float)i / segments; var b = Math.Min(t, (float)(i + 1) / segments); if (b <= a) break;
                        var gap = segments > 1 ? 2 : 0;
                        var fill = vertical ? new SKRect(rect.Left, rect.Bottom - rect.Height * b + gap, rect.Right, rect.Bottom - rect.Height * a) : new SKRect(rect.Left + rect.Width * a, rect.Top, rect.Left + rect.Width * b - gap, rect.Bottom);
                        if (fill.Width > 0 && fill.Height > 0) canvas.DrawRoundRect(fill, radius, radius, active);
                    }
                }
            }
            else
            {
                var stroke = Math.Clamp(StyleFloat(layer, "strokeWidth", 10), 1, Math.Max(1,Math.Min(rect.Width, rect.Height) / 3));
                var oval = new SKRect(rect.Left + stroke, rect.Top + stroke, rect.Right - stroke, rect.Bottom - stroke);
                var sweep = type == "gauge" ? 270 : StyleFloat(layer, "sweep", 360);
                var start = type == "gauge" ? 135 : -90;
                track.Style = active.Style = SKPaintStyle.Stroke; track.StrokeWidth = active.StrokeWidth = stroke;
                track.StrokeCap = active.StrokeCap = SKStrokeCap.Round;
                canvas.DrawArc(oval, start, sweep, false, track);
                if (number is not null) canvas.DrawArc(oval, start, sweep * t, false, active);
                if (type == "gauge")
                {
                    active.StrokeWidth = Math.Max(1, stroke / 5);
                    for (int i = 0; i <= 10; i++)
                    {
                        var angle = (start + sweep * i / 10) * MathF.PI / 180;
                        var r = Math.Min(oval.Width, oval.Height) / 2;
                        canvas.DrawLine(rect.MidX + MathF.Cos(angle) * (r - stroke), rect.MidY + MathF.Sin(angle) * (r - stroke), rect.MidX + MathF.Cos(angle) * r, rect.MidY + MathF.Sin(angle) * r, active);
                    }
                    if (number is not null)
                    {
                        var angle = (start + sweep * t) * MathF.PI / 180; var r = Math.Min(oval.Width, oval.Height) * .35f;
                        active.StrokeWidth = 3; canvas.DrawLine(rect.MidX, rect.MidY, rect.MidX + MathF.Cos(angle) * r, rect.MidY + MathF.Sin(angle) * r, active);
                    }
                }
            }
            if (number is null || StyleFloat(layer, "showValue", 0) != 0)
            {
                var text = number is null ? "--" : FormatValue(ScaleValue(number, StyleFloat(layer, "valueScale", 1)), layer.Format) + StyleString(layer, "unit", "");
                var centered = layer with { Style = new(layer.Style) { ["align"] = JsonSerializer.SerializeToElement("center") } };
                DrawEditorText(canvas, centered, rect, text, 0);
            }
            return true;
        }
        if (type == "sparkline")
        { DrawTimedGraph(canvas, layer, rect, value as double?);if(value is not double)DrawEditorText(canvas,layer,rect,"--",0);return true; }
        return false;
    }
    private readonly Dictionary<string, Queue<(DateTimeOffset At, double Value)>> _timedGraphs = [];
    private readonly Dictionary<string,string?> _graphBindings = [];
    internal int TimedHistoryCount(string id) => _timedGraphs.GetValueOrDefault(id)?.Count ?? 0;
    private void DrawTimedGraph(SKCanvas canvas, ThemeLayer layer, SKRect rect, double? value)
    {
        if (!_timedGraphs.TryGetValue(layer.Id, out var points) || _graphBindings.GetValueOrDefault(layer.Id)!=layer.Binding)
        { _timedGraphs[layer.Id] = points = new(); _graphBindings[layer.Id]=layer.Binding; }
        // A source reconnect or demo/live switch may move the sample clock backwards.
        if (points.Count > 0 && _sampleTime < points.Last().At) points.Clear();
        if (value is double v && double.IsFinite(v) && (points.Count == 0 || points.Last().At < _sampleTime)) points.Enqueue((_sampleTime, v));
        var seconds = Math.Clamp(StyleFloat(layer, "historySeconds", 120), 5, 3600);
        while (points.Count > 0 && (points.Peek().At < _sampleTime.AddSeconds(-seconds) || points.Count > 4096)) points.Dequeue();
        if (points.Count == 0) return;
        var scale = StyleFloat(layer, "valueScale", 1);
        var min = StyleFloat(layer, "min", (float)points.Min(p => p.Value * scale)); var max = Math.Max(min + .001f, StyleFloat(layer, "max", (float)points.Max(p => p.Value * scale)));
        var stroke = Math.Clamp(StyleFloat(layer, "strokeWidth", 2), 1, Math.Max(1, Math.Min(rect.Width, rect.Height) / 2));
        var plot = rect; plot.Inflate(-stroke / 2, -stroke / 2);
        // Show the collected interval during warm-up, then keep a rolling time window.
        // No invented samples: the first real sample is a visible point.
        var span = Math.Min(seconds, Math.Max(1, (_sampleTime - points.Peek().At).TotalSeconds));
        using var path = new SKPath(); var first = true;
        float firstX = plot.Right, lastX = plot.Right, lastY = plot.Bottom;
        foreach (var p in points)
        {
            var x = plot.Right - (float)((_sampleTime - p.At).TotalSeconds / span) * plot.Width;
            var y = plot.Bottom - (float)Math.Clamp((p.Value * scale - min) / (max - min), 0, 1) * plot.Height;
            if (first) { firstX = x; path.MoveTo(x, y); first = false; } else path.LineTo(x, y);
            lastX = x; lastY = y;
        }
        using var paint = new SKPaint { IsAntialias = true, Color = StyleColor(layer, "color", SKColors.Cyan), Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        canvas.Save(); canvas.ClipRect(rect);
        try
        {
        if (StyleFloat(layer, "area", 0) != 0)
        {
            using var area = new SKPath(path); area.LineTo(lastX, plot.Bottom); area.LineTo(firstX, plot.Bottom); area.Close();
            using var fill = new SKPaint { IsAntialias = true, Color = paint.Color.WithAlpha(50) }; canvas.DrawPath(area, fill);
        }
        canvas.DrawPath(path, paint);
        if (points.Count == 1) { paint.Style = SKPaintStyle.Fill; canvas.DrawCircle(lastX, lastY, stroke / 2, paint); }
        } finally { canvas.Restore(); }
    }
    private void DrawEditorText(SKCanvas canvas, ThemeLayer layer, SKRect rect, string text, float offset)
    {
        var asset = StyleString(layer, "fontAsset", "");
        var key = asset.Length > 0 ? ResolveAsset(new(new(), _assetRoot), asset) : StyleString(layer, "fontFamily", "Microsoft YaHei UI") + "|" + StyleString(layer, "fontWeight", "normal");
        if (!_fonts.TryGetValue(key, out var face))
        {
            face = asset.Length > 0 ? SKTypeface.FromFile(key) : SKTypeface.FromFamilyName(StyleString(layer, "fontFamily", "Microsoft YaHei UI"), StyleString(layer, "fontWeight", "normal") == "bold" ? SKFontStyle.Bold : SKFontStyle.Normal);
            _fonts[key] = face ??= SKTypeface.FromFamilyName("Microsoft YaHei UI");
        }
        using var font = new SKFont(face, Math.Clamp(StyleFloat(layer, "fontSize", Math.Max(12,rect.Height*.42f)) + offset + StyleFloat(layer,"fontSizeAdjust",0), 1, 1000));
        using var paint = new SKPaint { IsAntialias = true, Color = StyleColor(layer, "color", SKColors.White) };
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (StyleFloat(layer, "wrap", 0) == 0) { lines.Add(raw); continue; }
            var line = ""; foreach (var ch in raw) { if (line.Length > 0 && font.MeasureText(line + ch) > rect.Width) { lines.Add(line); line = ""; } line += ch; } lines.Add(line);
        }
        var align = StyleString(layer, "align", "left");
        var x = align == "center" ? rect.MidX : align == "right" ? rect.Right : rect.Left;
        var anchor = align == "center" ? SKTextAlign.Center : align == "right" ? SKTextAlign.Right : SKTextAlign.Left;
        var y = rect.MidY - (lines.Count - 1) * font.Size * 1.15f / 2 - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        canvas.Save(); canvas.ClipRect(rect);
        for (int i = 0; i < lines.Count; i++) canvas.DrawText(lines[i], x, y + i * font.Size * 1.15f, anchor, font, paint);
        canvas.Restore();
    }
}
