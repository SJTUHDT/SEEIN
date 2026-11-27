using System.Drawing;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Seein.Tray;

/// <summary>
/// Extracts and caches the icon of an executable.  Extraction is a one time cost per application
/// (about 4 ms) and the result is cached in memory for the lifetime of the tray process.
/// </summary>
public static class AppIconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static readonly Lazy<ImageSource?> DefaultIcon = new(LoadFallback);

    /// <summary>Custom icon (when the user picked one) wins over the executable icon.</summary>
    public static ImageSource? Get(string? iconPath, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            if (GetOrLoad("icon:" + iconPath, () => LoadFile(iconPath)) is { } custom) return custom;
        }
        return Get(exePath);
    }

    private static ImageSource? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Load(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return Trim(image);
        }
        catch { return null; }
    }

    public static ImageSource? Get(string? exePath)
    {
        return string.IsNullOrWhiteSpace(exePath)
            ? DefaultIcon.Value : GetOrLoad(exePath, () => Load(exePath)) ?? DefaultIcon.Value;
    }

    private static ImageSource? GetOrLoad(string key, Func<ImageSource?> load)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        var image = load();
        lock (Gate)
        {
            if (Cache.TryAdd(key, image)) return image;
            return Cache[key];
        }
    }

    private static ImageSource? Load(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return null;
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return Trim(image);
        }
        catch { return null; }
    }

    /// <summary>
    /// Icons (and the images the user picks) usually carry a transparent margin, which makes the
    /// glyph look small inside its box.  Crop to the opaque bounding box so the artwork fills the row.
    /// </summary>
    private static ImageSource Trim(ImageSource source)
    {
        try
        {
            if (source is not BitmapSource bitmapSource) return source;
            var converted = new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            // Transparent padding first; if the artwork sits on an opaque (usually white) plate the
            // alpha pass finds nothing, so also trim pixels that match the corner colour.
            var corners = new[]
            {
                (X: 0, Y: 0),
                (X: converted.PixelWidth - 1, Y: 0),
                (X: 0, Y: converted.PixelHeight - 1),
                (X: converted.PixelWidth - 1, Y: converted.PixelHeight - 1),
            };
            var cornerAlpha = 255;
            var cr = 0; var cg = 0; var cb = 0;
            foreach (var corner in corners)
            {
                var index = corner.Y * stride + corner.X * 4;
                cr += pixels[index + 2]; cg += pixels[index + 1]; cb += pixels[index];
                cornerAlpha = Math.Min(cornerAlpha, pixels[index + 3]);
            }
            cr /= corners.Length; cg /= corners.Length; cb /= corners.Length;
            var plate = cornerAlpha > 200 && cr > 220 && cg > 220 && cb > 220;

            const byte alphaThreshold = 16;
            const int colourThreshold = 22;
            int left = converted.PixelWidth, top = converted.PixelHeight, right = -1, bottom = -1;
            for (var y = 0; y < converted.PixelHeight; y++)
            {
                var row = y * stride;
                for (var x = 0; x < converted.PixelWidth; x++)
                {
                    var index = row + x * 4;
                    var alpha = pixels[index + 3];
                    if (alpha <= alphaThreshold) continue;
                    if (plate)
                    {
                        var db = Math.Abs(pixels[index] - cb);
                        var dg = Math.Abs(pixels[index + 1] - cg);
                        var dr = Math.Abs(pixels[index + 2] - cr);
                        if (db < colourThreshold && dg < colourThreshold && dr < colourThreshold) continue;
                    }
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
            if (right < left || bottom < top) return source;

            var width = right - left + 1;
            var height = bottom - top + 1;
            if (width == converted.PixelWidth && height == converted.PixelHeight) return source;
            var cropped = new CroppedBitmap(bitmapSource, new System.Windows.Int32Rect(left, top, width, height));
            cropped.Freeze();
            return cropped;
        }
        catch { return source; }
    }


    private static ImageSource? LoadFallback()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SystemIcons.Application;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return Trim(image);
        }
        catch { }
        return null;
    }
}
