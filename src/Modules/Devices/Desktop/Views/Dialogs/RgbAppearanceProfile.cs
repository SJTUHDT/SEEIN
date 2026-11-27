using System.IO;
using System.Text.Json;
using Seein.Core;
using Seein.Core.Lighting;

namespace Seein.Tray;

internal sealed record RgbAppearanceSample(RgbColor? Target = null, RgbColor? CaseOutput = null, RgbColor? Case = null);

internal sealed record RgbAppearanceProfile(string BoardKey, IReadOnlyList<RgbAppearanceSample> Samples)
{
    public static IReadOnlyDictionary<RgbColor, RgbColor> ReadCase(IReadOnlyList<RgbDeviceInfo> devices)
    {
        var path = Path.Combine(AppPaths.SystemDefault.DataRoot, "rgb-appearance.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 256 * 1024) return Empty();
            var profile = JsonSerializer.Deserialize<RgbAppearanceProfile>(File.ReadAllText(path), JsonDefaults.Options);
            if (profile?.Samples is null) return Empty();
            if (devices.Count(device => device.DeviceKey == profile.BoardKey &&
                    device.Zones.Any(zone => zone.Name == "JARGB 1" && zone.LedCount > 0)) != 1)
                return Empty();
            var observations = new Dictionary<RgbColor, RgbColor>();
            foreach (var row in profile.Samples)
            {
                if (row?.Target is { } target && row.CaseOutput == target && row.Case is { } appearance)
                    observations[target] = appearance;
            }
            return observations;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty();
        }

        static Dictionary<RgbColor, RgbColor> Empty() => [];
    }
}
