namespace Seein.Core.Thermal;

/// <summary>How one fan channel is driven: leave it to the firmware, a fixed PWM, or a temperature curve.</summary>
public enum FanChannelMode
{
    Auto,
    Manual,
    Curve
}

public enum FanTemperatureSource
{
    CpuPackage,
    GpuCore,
    Motherboard,
    Maximum,
    CpuAndGpu
}

public sealed record FanCurvePoint(double TemperatureC, double Percent);

/// <summary>Curve plus the smoothing rules that keep a fan from audibly jumping between steps.</summary>
public sealed record FanCurveSettings(
    IReadOnlyList<FanCurvePoint> Points,
    double HysteresisC = 3,
    double MaxStepPercent = 20,
    bool SpinUp = false,
    double SpinUpSeconds = 3);

public sealed record FanChannelSettings(
    string Id,
    FanChannelMode Mode = FanChannelMode.Auto,
    double ManualPercent = 50,
    double FloorPercent = -1,
    double CeilingPercent = 100,
    FanCurveSettings? Curve = null,
    FanTemperatureSource? Source = null,
    FanCurveSettings? GpuCurve = null);

public sealed record FanProfile(
    string Id,
    string Name,
    FanTemperatureSource Source,
    IReadOnlyList<FanChannelSettings> Channels);

public sealed record FanSettings(
    bool Enabled = false,
    string ActiveProfileId = "balanced",
    IReadOnlyList<FanProfile>? Profiles = null,
    bool StopMsiFanServices = false,
    int PolicyVersion = 0);

/// <summary>Live view of one fan channel, sent to the console.</summary>
public sealed record FanChannelState(
    string Id,
    string Label,
    string RawName,
    int Rpm,
    double? Pwm,
    bool Writable,
    double FloorPercent,
    double CeilingPercent,
    string Mode,
    double? TargetPercent,
    double? TemperatureC,
    string? Error = null,
    string Owner = "firmware",
    bool RpmAvailable = false,
    FanTemperatureSource? Source = null,
    double? SecondaryTemperatureC = null,
    IReadOnlyList<FanTrendSample>? Trend = null);

public sealed record FanTrendSample(DateTimeOffset At, double? TemperatureC, double? TargetPercent, int? Rpm,
    double? SecondaryTemperatureC = null);

public sealed record FanStatus(
    bool ControllerAvailable,
    bool Enabled,
    string ActiveProfileId,
    FanTemperatureSource Source,
    IReadOnlyList<FanChannelState> Channels,
    bool MsiFanServiceRunning = false,
    string? Message = null,
    string? LastError = null,
    DateTimeOffset? LastWriteAt = null,
    GpuFanStatus? Gpu = null);

public sealed record FanStateRequest(string? RequestId = null);

public sealed record FanStateResult(FanStatus Status, FanSettings Settings, string? RequestId = null);

public sealed record FanConfigRequest(FanSettings Settings, string? Command = null, string? RequestId = null, GpuFanCommand? Gpu = null);

public sealed record FanConfigResult(bool Success, FanStatus? Status = null, string? Error = null, string? RequestId = null, FanSettings? Settings = null);

public sealed record FanRestoreRequest(string? RequestId = null);

public sealed record FanRestoreResult(bool Success, FanStatus? Status = null, string? Error = null, string? RequestId = null, FanSettings? Settings = null);

public sealed record FanStateSnapshot(FanStatus Status, DateTimeOffset Timestamp);

/// <summary>Channel metadata discovered from the SuperIO, before any write is attempted.</summary>
public sealed record FanChannelDescriptor(
    string Id,
    string RawName,
    string Label,
    bool ControlAvailable,
    double FloorPercent,
    double CeilingPercent);

/// <summary>Hardware side of fan control; implemented by the LibreHardwareMonitor collector.</summary>
public interface IFanControlDevice
{
    /// <summary>Every controllable or readable fan channel the board exposes.</summary>
    IReadOnlyList<FanChannelDescriptor> DescribeFanChannels();

    /// <summary>Writes a software PWM value in percent. Returns false (and an error) when unsupported.</summary>
    bool TrySetFanPwm(string channelId, double percent, out string? error);

    /// <summary>Hands a channel back to the firmware curve.</summary>
    bool TryRestoreFan(string channelId, out string? error);

    /// <summary>Hands every channel back to the firmware curve (used on shutdown).</summary>
    void RestoreAllFans();
}

/// <summary>Built-in profiles and the per-channel safety floors confirmed on this board.</summary>
public static class FanDefaults
{
    public const string SilentId = "silent";
    public const string BalancedId = "balanced";
    public const string PerformanceId = "performance";
    public const string CustomId = "custom";
    private const string RecommendedPrefix = "recommended-";

    public static string LabelFor(string channelId) => channelId switch
    {
        "cpu-fan" => "冷排风扇",
        "pump-fan" => "水冷泵",
        "pump-fan-1" or "pump" => "水冷泵",
        "system-fan-4" => "机箱风扇",
        "gpu-fan" => "显卡风扇",
        _ => channelId.Replace('-', ' ')
    };

    /// <summary>The pump must never be starved; the radiator may idle lower; case fans may stop.</summary>
    public static double FloorFor(string channelId) => channelId switch
    {
        "pump-fan" or "pump-fan-1" or "pump" => 55,
        "cpu-fan" => 25,
        _ => 0
    };

    public static double CeilingFor(string channelId) => 100;

    public static FanCurveSettings CurveFor(string channelId, string profileId)
    {
        // Temperature to PWM pairs (°C → %). The pump is kept flat and high; the radiator ramps late.
        if (channelId is "pump-fan" or "pump-fan-1" or "pump")
            return new FanCurveSettings(
            [
                new FanCurvePoint(30, 60),
                new FanCurvePoint(50, 70),
                new FanCurvePoint(70, 90),
                new FanCurvePoint(85, 100)
            ]);

        FanCurvePoint[] points = BuiltInId(profileId) switch
        {
            SilentId =>
            [
                new FanCurvePoint(30, 30),
                new FanCurvePoint(45, 35),
                new FanCurvePoint(60, 50),
                new FanCurvePoint(75, 70),
                new FanCurvePoint(90, 100)
            ],
            PerformanceId =>
            [
                new FanCurvePoint(30, 45),
                new FanCurvePoint(45, 60),
                new FanCurvePoint(60, 80),
                new FanCurvePoint(75, 100),
                new FanCurvePoint(90, 100)
            ],
            _ =>
            [
                new FanCurvePoint(30, 35),
                new FanCurvePoint(45, 45),
                new FanCurvePoint(60, 60),
                new FanCurvePoint(75, 85),
                new FanCurvePoint(90, 100)
            ]
        };
        return new FanCurveSettings(points);
    }

    public static IReadOnlyList<FanProfile> DefaultProfiles()
    {
        static FanChannelSettings Channel(string id, string profileId) => new(
            id,
            FanChannelMode.Curve,
            FloorPercent: FloorFor(id),
            CeilingPercent: CeilingFor(id),
            Curve: CurveFor(id, profileId),
            Source: id == "system-fan-4" ? FanTemperatureSource.CpuAndGpu : FanTemperatureSource.CpuPackage,
            GpuCurve: id == "system-fan-4" ? CaseGpuCurve(profileId) : null);

        static FanChannelSettings Pump(string profileId) => Channel("pump-fan", profileId) with
        {
            Mode = FanChannelMode.Manual,
            ManualPercent = profileId switch { SilentId => 60, PerformanceId => 75, _ => 65 }
        };

        return
        [
            new FanProfile(SilentId, "静音", FanTemperatureSource.CpuPackage,
                [Channel("cpu-fan", SilentId), Pump(SilentId), Channel("system-fan-4", SilentId)]),
            new FanProfile(BalancedId, "均衡", FanTemperatureSource.CpuPackage,
                [Channel("cpu-fan", BalancedId), Pump(BalancedId), Channel("system-fan-4", BalancedId)]),
            new FanProfile(PerformanceId, "游戏", FanTemperatureSource.CpuPackage,
                [Channel("cpu-fan", PerformanceId), Pump(PerformanceId), Channel("system-fan-4", PerformanceId)])
        ];
    }

    public static FanCurveSettings CaseGpuCurve(string profileId)
    {
        var offset = BuiltInId(profileId) switch { SilentId => -5, PerformanceId => 10, _ => 0 };
        return new FanCurveSettings(
        [
            new(35, 30 + offset), new(50, 40 + offset), new(60, 55 + offset),
            new(72, 75 + offset), new(82, 100)
        ]);
    }

    public static FanChannelSettings? FindChannel(FanProfile profile, string id) =>
        profile.Channels.FirstOrDefault(channel => string.Equals(channel.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? (FloorFor(id) == 55 ? profile.Channels.FirstOrDefault(channel => FloorFor(channel.Id) == 55) : null);

    private static string BuiltInId(string profileId) => profileId.StartsWith(RecommendedPrefix, StringComparison.Ordinal)
        ? profileId[RecommendedPrefix.Length..] : profileId;

    public static FanSettings DefaultSettings() => new(false, BalancedId, DefaultProfiles(), PolicyVersion: 1);

    /// <summary>Merges stored settings with the defaults so new channels keep sensible floors and curves.</summary>
    public static FanSettings Normalize(FanSettings? stored)
    {
        if (stored is null) return DefaultSettings();
        var profiles = stored.Profiles is { Count: > 0 } ? stored.Profiles : DefaultProfiles();
        var normalizedProfiles = new List<FanProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            var channels = new List<FanChannelSettings>();
            foreach (var channel in profile.Channels ?? [])
            {
                channels.Add(channel with
                {
                    FloorPercent = Math.Max(FloorFor(channel.Id), FanCurveMath.ClampPercent(channel.FloorPercent)),
                    Source = channel.Source ?? profile.Source,
                    GpuCurve = channel.GpuCurve ?? (channel.Source == FanTemperatureSource.CpuAndGpu
                        ? CaseGpuCurve(profile.Id) : null),
                    CeilingPercent = channel.CeilingPercent <= 0 ? CeilingFor(channel.Id) : channel.CeilingPercent,
                    Curve = channel.Curve ?? CurveFor(channel.Id, profile.Id)
                });
            }
            normalizedProfiles.Add(profile with { Channels = channels });
        }
        if (stored.PolicyVersion < 1 && stored.Profiles is { Count: > 0 } &&
            stored.Profiles.Any(profile => profile.Id is SilentId or BalancedId or PerformanceId))
        {
            foreach (var preset in DefaultProfiles())
            {
                var id = RecommendedPrefix + preset.Id;
                if (normalizedProfiles.All(profile => profile.Id != id))
                    normalizedProfiles.Add(preset with { Id = id, Name = $"{preset.Name}（推荐）" });
            }
        }
        var activeId = normalizedProfiles.Any(profile => profile.Id == stored.ActiveProfileId)
            ? stored.ActiveProfileId
            : normalizedProfiles[0].Id;
        return stored with { Profiles = normalizedProfiles, ActiveProfileId = activeId, PolicyVersion = Math.Max(1, stored.PolicyVersion) };
    }
}
