using System.Text.Json.Serialization;

namespace Seein.Core.Lighting;

public enum RgbProviderState
{
    NotConfigured,
    Connecting,
    Ready,
    Error
}

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromHex(string? hex, RgbColor fallback = default)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var text = hex.Trim().TrimStart('#');
        if (text.Length == 3)
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value))
            return fallback;
        return new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public static RgbColor Blend(RgbColor from,RgbColor to,double ratio)
    {
        var amount=Math.Clamp(ratio,0,1);
        return new((byte)Math.Round(from.R+(to.R-from.R)*amount),(byte)Math.Round(from.G+(to.G-from.G)*amount),(byte)Math.Round(from.B+(to.B-from.B)*amount));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Brightness scaling in percent (used by the effect engine).</summary>
    public RgbColor Scale(double percent)
    {
        var factor = Math.Clamp(percent, 0, 100) / 100d;
        return new RgbColor((byte)Math.Round(R * factor), (byte)Math.Round(G * factor), (byte)Math.Round(B * factor));
    }
}

public sealed record RgbDeviceInfo(
    int Index,
    string Name,
    string Type,
    string Vendor,
    IReadOnlyList<RgbZoneInfo> Zones,
    IReadOnlyList<RgbMode> Modes,
    string? ActiveMode,
    bool SupportsDirect,
    int LedCount,
    string Address = "",
    string DeviceKey = "",
    string TopologyKey = "",
    int ActiveModeIndex = -1)
{
    public IEnumerable<string> ModeNames => Modes.Select(mode => mode.Name);

    [JsonIgnore]
    public RgbMode? SolidColorMode => Modes.FirstOrDefault(mode =>
        string.Equals(mode.Name, "Static", StringComparison.OrdinalIgnoreCase) &&
        ((mode.PerLedColor && mode.ColorMode == 1 && LedCount > 0) ||
         (mode.HasModeColors && mode.ColorMode == 2 && mode.ColorsMin <= Math.Max(1, mode.Colors.Count) &&
          mode.ColorsMax >= Math.Max(1, mode.Colors.Count))));

    [JsonIgnore]
    public bool CanSetSolidColor => (SupportsDirect && LedCount > 0) || SolidColorMode is not null;
}

public sealed record RgbDeviceState(RgbDeviceInfo Info, IReadOnlyList<RgbColor> Colors);

public sealed record RgbStatus(
    RgbProviderState State,
    string? Message,
    IReadOnlyList<RgbDeviceState> Devices,
    bool Enabled = false,
    string? ActiveProfileId = null,
    string? LastError = null,
    string? GroupPlayback = null,
    double? MusicPeak = null);

/// <summary>A named light effect with its parameters; the engine renders it into per-LED colours.</summary>
public sealed record RgbEffectSettings(
    string EffectId,
    IReadOnlyList<RgbColor> Colors,
    double Speed = 50,
    double Brightness = 100,
    string Direction = "forward",
    int LowTemperature = 30,
    int HighTemperature = 95,
    bool UseRainbowPalette = false,
    bool UseZoneCalibration = true)
{
    public static RgbEffectSettings Default { get; } = new("static", [new RgbColor(0x00, 0x67, 0xC0)]);

    public static RgbEffectSettings Create(string effectId) => Default with { EffectId = effectId };
}

public sealed record RgbProfile(
    string Id,
    string Name,
    string? DeviceName,
    RgbEffectSettings Effect,
    string? HardwareMode = null,
    IReadOnlyList<RgbColor>? LedColors = null,
    string? DeviceKey = null,
    string? TopologyKey = null,
    IReadOnlyList<RgbEffectTarget>? Targets = null,
    bool WholeSystem = false);

public sealed record RgbEffectTarget(string DeviceKey, string TopologyKey, int? ZoneIndex, RgbEffectSettings Effect,
    string? HardwareMode = null, int? HardwareSpeed = null, int? HardwareBrightness = null);

public sealed record RgbZoneSize(string DeviceKey, int ZoneIndex, string ZoneName, int Size);

public sealed record RgbZoneCalibration(string DeviceKey, int ZoneIndex, string ZoneName, RgbColorCalibration Colors);

public sealed record RgbSettings(
    bool Enabled = false,
    string ProviderId = "openrgb",
    int Fps = 25,
    string ActiveProfileId = "static",
    bool AutoStartEngine = true,
    IReadOnlyList<RgbProfile>? Profiles = null,
    IReadOnlyList<RgbEffectTarget>? Targets = null,
    int SchemaVersion = 1,
    IReadOnlyList<RgbZoneSize>? ZoneSizes = null,
    RgbEffectSettings? GroupEffect = null,
    IReadOnlyDictionary<string, bool>? ExternalControlPreferences = null,
    IReadOnlyList<RgbZoneCalibration>? ZoneCalibrations = null);

public sealed record RgbDevicesRequest(string? RequestId = null);

public sealed record RgbDevicesResult(RgbStatus Status, RgbSettings Settings, string? RequestId = null);

/// <summary>One console command: switch profile, or push an ad-hoc effect.</summary>
public sealed record RgbCommandRequest(
    string Command,
    string? DeviceName = null,
    string? ZoneName = null,
    int? ZoneIndex = null,
    int? LedIndex = null,
    int? ModeValue = null,
    string? ModeName = null,
    int? ResizeSize = null,
    IReadOnlyList<RgbColor>? Colors = null,
    string? EffectId = null,
    double? Speed = null,
    double? Brightness = null,
    string? Direction = null,
    string? ProfileId = null,
    string? RequestId = null,
    string? DeviceAddress = null,
    RgbEffectSettings? GroupEffect = null,
    string? ProfileName = null,
    RgbColorCalibration? Calibration = null,
    bool BypassCalibration = false,
    IReadOnlyList<int>? PaintedLedIndices = null,
    bool UseRainbowPalette = false);

public enum RgbWriteOutcome { Rejected, Sent, Confirmed, ReadBack, Saved }

public sealed record RgbWriteResult(RgbWriteOutcome Outcome, string? Error = null)
{
    public bool Success => Outcome != RgbWriteOutcome.Rejected;
    public static RgbWriteResult Reject(string error) => new(RgbWriteOutcome.Rejected, error);
    public static RgbWriteResult Sent { get; } = new(RgbWriteOutcome.Sent);
    public static RgbWriteResult Confirmed { get; } = new(RgbWriteOutcome.Confirmed);
    public static RgbWriteResult ReadBack { get; } = new(RgbWriteOutcome.ReadBack);
    public static RgbWriteResult Saved { get; } = new(RgbWriteOutcome.Saved);
}

public sealed record RgbCommandResult(bool Success, RgbStatus? Status = null, string? Error = null, string? RequestId = null, RgbSettings? Settings = null,
    RgbWriteOutcome Outcome = RgbWriteOutcome.Rejected);

public sealed record RgbStateSnapshot(RgbStatus Status, DateTimeOffset Timestamp);

public sealed record RgbEffectOption(string Id, string Name, string Icon, int ColorCount = 0,
    bool Speed = false, bool Direction = false, bool TemperatureRange = false)
{
    public bool Brightness => Id is not (RgbEffects.Off or RgbEffects.BoardDefault);
    public bool SupportsRainbowPalette => Id is RgbEffects.Wave or RgbEffects.ColorRing or RgbEffects.Meteor;
}

public static class RgbEffects
{
    public const string ExternalControl = "External Control";
    public const string Static = "static";
    public const string Breathing = "breathing";
    public const string Rainbow = "rainbow";
    public const string ColorCycle = "colorCycle";
    public const string ColorRing = "colorRing";
    public const string Music = "music";
    public const string Meteor = "meteor";
    public const string BoardDefault = "boardDefault";
    public const string Wave = "wave";
    public const string Marquee = "marquee";
    public const string Lightning = "lightning";
    public const string Temperature = "temperature";
    public const string Off = "off";
    public const string Fire = "fire";

    public static IReadOnlyList<RgbEffectOption> Options { get; } =
    [
        new(Off, "关闭", "⊘"),
        new(Wave, "水波", "≋", 4, true, true),
        new(Static, "常亮", "●", 1),
        new(Fire, "火焰", "♨", 2, true),
        new(Breathing, "呼吸", "∿", 4, true),
        new(Temperature, "CPU 温度", "↟", 3, TemperatureRange: true),
        new(ColorRing, "色环", "◉", 4, true, true),
        new(Lightning, "闪电", "ϟ", 1, true),
        new(Music, "音乐律动", "♫", 1),
        new(Meteor, "流星", "☄", 4, true, true),
        new(BoardDefault, "主板默认", "◇"),
        new(ColorCycle, "双色渐变", "◑", 2, true),
        new(Rainbow, "彩虹", "◒", 0, true, true),
        new(Marquee, "跑马灯", "⇢", 1, true, true)
    ];

    public static IReadOnlyList<(string Id, string Name)> All { get; } = Options
        .Select(option => (option.Id, option.Name)).ToArray();

    public static RgbEffectSettings DefaultsFor(string id) => RgbEffectSettings.Create(id) with
    {
        Speed = id == BoardDefault ? 0 : 50,
        Colors = id switch
        {
            Off or Rainbow or BoardDefault => [],
            Breathing => [new(0x00, 0x67, 0xC0), new(0x00, 0xC8, 0xB4), new(0xB0, 0x61, 0xF7), new(0xFF, 0x63, 0x91)],
            Wave => [new(0x28, 0x92, 0xFF), new(0x26, 0xDB, 0xFF), new(0x36, 0xF5, 0xB4), new(0xBB, 0x72, 0xFF)],
            Fire => [new(0xB0, 0x0B, 0x00), new(0xFF, 0xC4, 0x46)],
            Temperature => [new(0x00, 0xC8, 0x6E), new(0xFF, 0xAA, 0x28), new(0xE8, 0x3A, 0x3A)],
            ColorRing or Meteor => [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 255)],
            ColorCycle => [new(0x00, 0x67, 0xC0), new(0xFF, 0x33, 0x66)],
            _ => RgbEffectSettings.Default.Colors
        }
    };

    public static RgbEffectSettings? NormalizeGroupEffect(RgbEffectSettings? effect)
    {
        if (effect is null || Options.FirstOrDefault(option => option.Id == effect.EffectId) is not { } option ||
            !double.IsFinite(effect.Speed) || !double.IsFinite(effect.Brightness) ||
            (option.TemperatureRange && (effect.LowTemperature < 0 || effect.HighTemperature > 120 ||
                effect.LowTemperature >= effect.HighTemperature))) return null;
        var defaults = DefaultsFor(effect.EffectId);
        var palette = effect.Colors ?? [];
        if (option.TemperatureRange && palette.Count == 2)
            palette = [palette[0], RgbColor.Blend(palette[0], palette[1], 0.5), palette[1]];
        return effect with
        {
            Colors = Enumerable.Range(0, option.ColorCount).Select(index =>
                palette.Count > 0 && option.ColorCount == 4 ? palette[index % Math.Min(palette.Count, 4)] :
                index < palette.Count ? palette[index] : defaults.Colors[index]).ToArray(),
            Speed = option.Speed ? Math.Clamp(effect.Speed, 0, 100) : defaults.Speed,
            Brightness = option.Brightness ? Math.Clamp(effect.Brightness, 0, 100) : 100,
            Direction = option.Direction && effect.Direction == "backward" ? "backward" : "forward",
            UseRainbowPalette = option.SupportsRainbowPalette && effect.UseRainbowPalette
        };
    }

    public static string NameOf(string id) =>
        Options.FirstOrDefault(entry => entry.Id == id)?.Name ?? id;

    public static RgbMode? NativeAnimation(RgbDeviceInfo device, string effectId)
    {
        if (device.SupportsDirect || RequiresGroupExternalInput(device) || !IsSapphire9070Xt(device)) return null;
        var name = effectId switch
        {
            Wave or Rainbow => "Rainbow Wave",
            Marquee => "Runway",
            ColorCycle => "Spectrum Cycle",
            Off => "Off",
            _ => null
        };
        return name is null ? null : device.Modes.FirstOrDefault(mode => mode.Name == name);
    }

    public static RgbMode? ExternalInput(RgbDeviceInfo device) =>
        device.Modes.FirstOrDefault(mode => IsExternalInput(mode.Name));

    public static bool IsExternalInput(string? mode) =>
        string.Equals(mode, ExternalControl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "External Source", StringComparison.OrdinalIgnoreCase);

    public static bool PrefersExternalInput(RgbDeviceInfo device, RgbSettings settings) =>
        ExternalInput(device) is not null &&
        (settings.ExternalControlPreferences?.TryGetValue(device.DeviceKey, out var selected) == true
            ? selected : IsExternalInput(device.ActiveMode));

    public static bool IsGpu(RgbDeviceInfo device) => device.Type is "显卡" or "GPU";

    private static bool IsSapphire9070Xt(RgbDeviceInfo device) =>
        device.Vendor.Equals("Sapphire", StringComparison.OrdinalIgnoreCase) &&
        device.Name.Contains("9070 XT", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresGroupExternalInput(RgbDeviceInfo device) => IsGpu(device) &&
        (ExternalInput(device) is not null ||
         IsSapphire9070Xt(device) && device.Name.Contains("Nitro+", StringComparison.OrdinalIgnoreCase));

    public static RgbMode? GroupExternalInput(RgbDeviceInfo device, RgbSettings settings) =>
        IsGpu(device) || PrefersExternalInput(device, settings) ? ExternalInput(device) : null;

    public static bool IsGroupProfile(RgbProfile profile) => profile.WholeSystem || profile is
    {
        DeviceName: null, DeviceKey: null, TopologyKey: null, Targets: null, HardwareMode: null, LedColors: null
    };

    public static RgbSettings DefaultSettings() => new(false, "openrgb", 25, Static, true,
    new RgbProfile[]
    {
        new RgbProfile(Static, "静态蓝", null, new RgbEffectSettings(Static, [new RgbColor(0x00, 0x67, 0xC0)])),
        new RgbProfile(Breathing, "呼吸青", null, new RgbEffectSettings(Breathing, [new RgbColor(0x00, 0xC8, 0xB4)], 40, 100)),
        new RgbProfile(Rainbow, "彩虹流动", null, new RgbEffectSettings(Rainbow, [], 50, 100)),
        new RgbProfile(Temperature, "温度联动", null, new RgbEffectSettings(Temperature, [new RgbColor(0x00, 0xC8, 0x6E), new RgbColor(0xE8, 0x3A, 0x3A)], 50, 100))
    });

    public static RgbSettings Normalize(RgbSettings? stored)
    {
        if (stored is null) return DefaultSettings();
        var profiles = stored.Profiles is { Count: > 0 } ? stored.Profiles : DefaultSettings().Profiles!;
        var activeId = profiles.Any(profile => profile.Id == stored.ActiveProfileId)
            ? stored.ActiveProfileId
            : profiles[0].Id;
        return stored with
        {
            Profiles = profiles,
            ActiveProfileId = activeId,
            GroupEffect = NormalizeGroupEffect(stored.GroupEffect),
            Fps = Math.Clamp(stored.Fps <= 0 ? 25 : stored.Fps, 5, 40)
        };
    }
}
