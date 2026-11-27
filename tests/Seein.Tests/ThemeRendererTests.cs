using Seein.Core;
using Seein.Core.Rendering;
using Seein.Core.Themes;
using SkiaSharp;

namespace Seein.Tests;

public sealed class ThemeRendererTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SeeinRenderTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("landscape", 1920, 462)]
    [InlineData("portraitClockwise", 462, 1920)]
    [InlineData("portraitCounterClockwise", 462, 1920)]
    public void EveryOrientationProducesDeviceNativeJpegSize(string orientation, int width, int height)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "manifest.json"), $$"""
            {"schemaVersion":"1.0","id":"rotation","name":"Rotation","canvas":{"width":{{width}},"height":{{height}}},"orientation":"{{orientation}}","frameRate":6,"jpegQuality":88,"backgroundColor":"#112233","layers":[{"id":"title","type":"text","x":0,"y":0,"width":100,"height":50,"format":"TEST"}]}
            """);
        var theme = ThemePackage.LoadDirectory(_root);
        using var renderer = new ThemeRenderer();
        var jpeg = renderer.RenderJpeg(theme, SensorSnapshot.Empty, null, null);
        using var bitmap = SKBitmap.Decode(jpeg);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
