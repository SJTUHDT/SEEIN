namespace Seein.Core.Lighting;

public enum RgbZoneType { Single, Linear, Matrix }

public sealed record RgbMode(
    string Name, int Value, uint Flags, uint SpeedMin, uint SpeedMax, uint BrightnessMin, uint BrightnessMax,
    uint ColorsMin, uint ColorsMax, uint Speed, uint Brightness, uint Direction, uint ColorMode,
    IReadOnlyList<RgbColor> Colors, int Index)
{
    public bool PerLedColor => (Flags & (1u << 5)) != 0;
    public bool IsDirect => string.Equals(Name, "Direct", StringComparison.OrdinalIgnoreCase) && PerLedColor;
    public bool HasSpeed => (Flags & 1) != 0;
    public bool HasBrightness => (Flags & (1u << 4)) != 0;
    public bool HasModeColors => (Flags & (1u << 6)) != 0;
}

public sealed record RgbSegment(string Name, RgbZoneType Type, uint Start, uint Count,
    IReadOnlyList<uint>? Matrix = null, uint Flags = 0);

public sealed record RgbZoneInfo(
    int Index, string Name, RgbZoneType Type, int LedCount, int LedsMin, int LedsMax,
    int MatrixWidth, int MatrixHeight, IReadOnlyList<uint>? Matrix = null,
    IReadOnlyList<RgbSegment>? Segments = null, uint Flags = 0,
    int ActiveModeIndex = -1, IReadOnlyList<RgbMode>? Modes = null, string? DisplayName = null);

public sealed record RgbLedInfo(int Index, string Name, uint Value);

public sealed record RgbDeviceLayout(
    int Index, int Type, string Name, string Vendor, string Description, string Version,
    string Serial, string Location, IReadOnlyList<RgbMode> Modes, IReadOnlyList<RgbZoneInfo> Zones,
    IReadOnlyList<RgbLedInfo> Leds, IReadOnlyList<RgbColor> Colors, int ActiveModeIndex,
    IReadOnlyList<string> LedAliases, uint Flags, string? DisplayName)
{
    public int LedCount => Leds.Count;
    public bool SupportsDirect => Modes.Any(mode => mode.IsDirect);
    public RgbMode? ActiveMode => Modes.ElementAtOrDefault(ActiveModeIndex);
}

