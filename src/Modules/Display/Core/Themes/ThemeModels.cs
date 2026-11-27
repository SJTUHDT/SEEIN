using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seein.Core.Themes;

public enum ThemeOrientation { Landscape, PortraitClockwise, PortraitCounterClockwise }

public sealed record ThemeCanvas(int Width = 1920, int Height = 462);

public sealed record ThemeManifest
{
    public string SchemaVersion { get; init; } = "1.0";
    public string Id { get; init; } = "theme";
    public string Name { get; init; } = "未命名主题";
    public string Author { get; init; } = "Unknown";
    public ThemeCanvas Canvas { get; init; } = new();
    public ThemeOrientation Orientation { get; init; } = ThemeOrientation.Landscape;
    public int FrameRate { get; init; } = 6;
    public int JpegQuality { get; init; } = 88;
    public float FontSizeOffset { get; init; }
    public string? Background { get; init; }
    public string BackgroundColor { get; init; } = "#10131A";
    public Dictionary<string, JsonElement> BackgroundStyle { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ThemeLayer> Layers { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record ThemeLayer
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Name { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public int Z { get; init; }
    public float Rotation { get; init; }
    public float Opacity { get; init; } = 1;
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
    public string? GroupId { get; init; }
    public string? Binding { get; init; }
    public string? Format { get; init; }
    public string? Asset { get; init; }
    public Dictionary<string, JsonElement> Style { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record LoadedTheme(ThemeManifest Manifest, string RootDirectory);

public sealed record ThemeValidationResult(bool IsValid, IReadOnlyList<string> Errors, ThemeManifest? Manifest = null)
{
    public static ThemeValidationResult Invalid(params string[] errors) => new(false, errors);
}

