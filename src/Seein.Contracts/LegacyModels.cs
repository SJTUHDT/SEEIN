using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seein.Core;

public static class SensorKeys
{
    public const string CpuTemperature = "cpu.package.temp";
    public const string CpuLoad = "cpu.total.load";
    public const string CpuClock = "cpu.average.clock";
    public const string CpuVoltage = "cpu.core.voltage";
    public const string CpuPower = "cpu.package.power";
    public const string GpuTemperature = "gpu.core.temp";
    public const string GpuLoad = "gpu.core.load";
    public const string GpuClock = "gpu.core.clock";
    public const string GpuVoltage = "gpu.core.voltage";
    public const string GpuPower = "gpu.board.power";
    public const string GpuFan = "gpu.fan.rpm";
    public const string FanRadiator = "fan.radiator.rpm";
    public const string FanPump = "fan.pump.rpm";
    public const string FanCase = "fan.case.rpm";
    public const string MotherboardTemp = "motherboard.temp";
    public const string StorageTemp = "storage.temp";
    public const string GpuMemoryUsed = "gpu.memory.used.mb";
    public const string MemoryLoad = "memory.load";
    public const string NetworkUp = "network.up.bps";
    public const string NetworkDown = "network.down.bps";
    public const string DiskRead = "disk.read.bps";
    public const string DiskWrite = "disk.write.bps";
    public const string SystemDriveLoad = "disk.system.load";
    public const string SystemVolume = "system.volume";
    public const string Uptime = "system.uptime.seconds";
    public const string Fps = "game.fps";
    public const string EstimatedSystemPower = "power.system.estimated.w";
    public const string SessionEnergy = "power.session.wh";
    public const string DailyEnergy = "power.today.wh";
    public const string MonthlyEnergy = "power.month.wh";
    public const string TotalEnergy = "power.total.wh";
}

public sealed record SensorSnapshot(DateTimeOffset Timestamp, IReadOnlyDictionary<string, double?> Values)
{
    public static SensorSnapshot Empty { get; } = new(DateTimeOffset.MinValue, new Dictionary<string, double?>());
    public double? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
}

public sealed record PowerSnapshot(
    DateTimeOffset Timestamp,
    double Watts,
    bool IsEstimated,
    double SessionWh,
    double TodayWh,
    double MonthWh,
    double TotalWh,
    string SourceName,
    double HighestDayWh = 0,
    string? HighestDayDate = null);

public sealed record MediaSnapshot(
    DateTimeOffset Timestamp,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    byte[]? ArtworkPng);

public enum DeviceConnectionState { Disconnected, Connecting, Connected, Busy, Error }

public sealed record DeviceStatus(
    DeviceConnectionState State,
    string? PortName,
    string Message,
    DateTimeOffset Timestamp);

public sealed record ApplyThemeRequest(string PackagePath, bool Activate = true);
public sealed record ApplyThemeResult(bool Success, string? ThemeName, string? Error);
public sealed record DeleteThemeRequest(string ThemeId, string RequestId);
public sealed record DeleteThemeResult(bool Success, string ThemeId, string? Error = null, string? RequestId = null);
public sealed record EnergyHistoryRequest(string Month, string? RequestId = null);
public sealed record DailyEnergyEntry(string LocalDate, double WattHours, double CoveredSeconds = 0);
public sealed record EnergyMonthSummary(double TotalWh, DailyEnergyEntry? Highest, DailyEnergyEntry? Lowest, double? AverageWh, int CompletedDays);
public sealed record EnergyHistoryResult(string Month, IReadOnlyList<DailyEnergyEntry> Entries, string? FirstMonth, string? LastMonth,
    EnergyMonthSummary? Summary = null, string? RequestId = null, string? Error = null);
public sealed record EnergyDayRequest(string LocalDate, string RequestId);
public sealed record PowerHistoryPoint(long UnixSeconds, int DurationSeconds, double MinWatts, double AverageWatts,
    double MaxWatts, double? CoveredSeconds, int Quality = 0);
public sealed record EnergyDayResult(string LocalDate, string RequestId, double TotalWh,
    double? CpuWh, double? GpuWh, double? OtherWh, double? LossWh, double UnclassifiedWh,
    double CoveredSeconds, double ClassifiedSeconds, IReadOnlyList<PowerHistoryPoint> Points,
    string Resolution, bool IsLegacy, string? Error = null,
    double CpuCoveredSeconds = 0, double GpuCoveredSeconds = 0);

public sealed record EnergyDelta(double TotalWh, double? CpuWh, double? GpuWh, double OtherWh, double LossWh,
    double UnclassifiedWh, double CoveredSeconds, double CpuCoveredSeconds, double GpuCoveredSeconds,
    double ClassifiedSeconds);

// ---- Application usage / application power statistics ----

public static class AppStatKeys
{
    public const string System = "__system__";
    public const string Background = "__background__";
    public const string Testing = "__testing__";
    public const string Self = "__self__";
    public const string Unknown = "__unknown__";
    public const string Baseline = "__baseline__";
}

public sealed record AppSpan(long StartUtc, long EndUtc);

public sealed record AppStatsRequest(string Kind, string LocalDate, bool IncludeHidden = false, string? RequestId = null);

public sealed record AppTimelineRow(string Key, string Name, string? ExePath, double Seconds, double Wh,
    IReadOnlyList<AppSpan> Spans, double ActiveSeconds = 0, string? Category = null, uint Color = 0,
    string? IconPath = null);

public sealed record AppPowerRow(string Key, string Name, string? ExePath, double Seconds, double Wh,
    double CpuWh, double GpuWh, double Share, double ActiveSeconds = 0, string? Category = null, uint Color = 0,
    string? IconPath = null);

public sealed record AppStatsSummary(int AppCount, double TotalSeconds, double TotalWh, double WindowSeconds,
    double SystemSeconds, double SystemWh, double BaselineWh, double ActiveSeconds = 0);

public sealed record AppDayPoint(string LocalDate, double Seconds, double Wh,
    double SystemWh = 0, double BaselineWh = 0);

public sealed record AppGap(long StartUtc, long EndUtc, string Reason);

public sealed record AppStatsResult(string Kind, string LocalDate, AppStatsSummary Summary,
    IReadOnlyList<AppTimelineRow> Timeline, IReadOnlyList<AppPowerRow> Power,
    IReadOnlyList<AppDayPoint> WeekSeries, IReadOnlyList<AppGap> Gaps,
    long? SystemStartUtc = null, string? RequestId = null, string? Error = null, double PreviousWh = 0);

public static class PipeMessageType
{
    public const string Hello="hello";
    public const string SensorSnapshot="sensorSnapshot";
    public const string PowerSnapshot="powerSnapshot";
    public const string MediaSnapshot="mediaSnapshot";
    public const string DeviceStatus="deviceStatus";
    public const string ThemeManifest="themeManifest";
    public const string ApplyThemeRequest="applyThemeRequest";
    public const string ApplyThemeResult="applyThemeResult";
    public const string DeleteThemeRequest="deleteThemeRequest";
    public const string DeleteThemeResult="deleteThemeResult";
    public const string EnergyHistoryRequest="energyHistoryRequest";
    public const string EnergyHistoryResult="energyHistoryResult";
    public const string SettingsRequest="settingsRequest";
    public const string SettingsResult="settingsResult";
    public const string EnergyDayRequest="energyDayRequest";
    public const string EnergyDayResult="energyDayResult";
    public const string AppStatsRequest="appStatsRequest";
    public const string AppStatsResult="appStatsResult";
    public const string AfkSample="afkSample";
    public const string AppMetaRequest="appMetaRequest";
    public const string AppMetaResult="appMetaResult";
    public const string AppSummaryRequest="appSummaryRequest";
    public const string AppSummaryResult="appSummaryResult";
    public const string AppCatalogRequest="appCatalogRequest";
    public const string AppCatalogResult="appCatalogResult";
    public const string AppLoadRequest="appLoadRequest";
    public const string AppLoadResult="appLoadResult";
    public const string FanStateRequest="fanStateRequest";
    public const string FanStateResult="fanStateResult";
    public const string FanConfigRequest="fanConfigRequest";
    public const string FanConfigResult="fanConfigResult";
    public const string FanRestoreRequest="fanRestoreRequest";
    public const string FanRestoreResult="fanRestoreResult";
    public const string FanStateSnapshot="fanStateSnapshot";
    public const string RgbDevicesRequest="rgbDevicesRequest";
    public const string RgbDevicesResult="rgbDevicesResult";
    public const string RgbCommandRequest="rgbCommandRequest";
    public const string RgbCommandResult="rgbCommandResult";
    public const string RgbStateSnapshot="rgbStateSnapshot";
}

/// <summary>One row of the application management list (includes hidden and merged entries).</summary>
/// <summary>One process key that belongs to a catalogue entry (same display name or merged into it).</summary>
public sealed record AppCatalogMember(string Key, string? ExePath, double TotalSeconds, bool Merged, bool Hidden = false,
    string? Name = null, IReadOnlyList<AppCatalogMember>? Children = null, bool Automatic = false);

public sealed record AppCatalogRow(string Key, string Name, string? ExePath, string Category, uint Color,
    bool Hidden, string? MergedInto, string? IconPath, double SecondsToday, double WhToday,
    double TotalSeconds = 0, double TotalWh = 0, bool System = false,
    IReadOnlyList<AppCatalogMember>? Members = null, int? SystemOverride = null, bool Background = false);

public sealed record AppCatalogRequest(string? RequestId = null);

public sealed record AppCatalogResult(IReadOnlyList<AppCatalogRow> Rows, string? RequestId = null,
    double BackgroundSeconds = 0, double SystemSeconds = 0,
    double BackgroundTodaySeconds = 0, double SystemTodaySeconds = 0, string? Error = null);

/// <summary>Per-application resource usage (CPU/GPU share and network bytes) for one day.</summary>
public sealed record AppLoadRequest(string? LocalDate = null, string? RequestId = null);

public sealed record AppLoadEntry(string Key, string Name, string? ExePath, double Seconds,
    double CpuPercent, double GpuPercent, long NetUpBytes, long NetDownBytes,
    string Category, uint Color, string? IconPath, bool System = false, bool Background = false,
    double CpuPeak = 0, double GpuPeak = 0, double CpuMin = 0, double GpuMin = 0,
    double CpuShare = 0, double GpuShare = 0);

public sealed record AppLoadMachineRow(double Seconds, double CpuPercent, double GpuPercent,
    long NetUpBytes, long NetDownBytes, double CpuPeak = 0, double GpuPeak = 0,
    double CpuMin = 0, double GpuMin = 0);

public sealed record AppLoadResult(string LocalDate, AppLoadMachineRow Machine,
    IReadOnlyList<AppLoadEntry> Apps, string? RequestId = null);

/// <summary>Idle time reported by the tray (session 1); the service cannot read it itself.</summary>
public sealed record AfkSample(long TimestampUtc, int IdleSeconds);

public sealed record AppMetaRequest(string Kind, string AppKey, string? Value, string? RequestId = null);

public sealed record AppMetaResult(bool Success, string? Error = null, string? RequestId = null);

public sealed record AppSummaryRequest(string Kind, string LocalDate, string? RequestId = null);

public sealed record AppSummaryResult(bool Success, string? Path, string? Error = null, string? RequestId = null);

public sealed record PipeEnvelope(string Type, JsonElement Payload)
{
    public static PipeEnvelope Create<T>(string type, T payload) =>
        new(type, JsonSerializer.SerializeToElement(payload, JsonDefaults.Options));

    public T? Read<T>() => Payload.Deserialize<T>(JsonDefaults.Options);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

