using System.Text.Json;

namespace Seein.Devices.CapturePlayback;

public sealed record CaptureMode(int Width, int Height, int Numerator, int Denominator, string PixelFormat)
{
    public double FramesPerSecond => Denominator > 0 ? (double)Numerator / Denominator : 0;
    public string Key => $"{Width}x{Height}:{Numerator}/{Denominator}:{PixelFormat}";
    public string Label => $"{Width} × {Height} · {FramesPerSecond:0.##} fps · {PixelFormat}";
}

public sealed record CaptureEndpoint(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record CaptureProfile
{
    public string VideoId { get; init; } = "";
    public string AudioId { get; init; } = "";
    public bool AudioSelectionSaved { get; init; }
    public string OutputId { get; init; } = "";
    public string ModeKey { get; init; } = "";
    // Retained for compatibility with earlier profiles; playback now uses system volume.
    public int Volume { get; init; } = 80;
    public bool Muted { get; init; }
    public double WindowWidth { get; init; } = 1100;
    public double WindowHeight { get; init; } = 720;
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
}

public static class CapturePolicy
{
    // A manual choice must never silently select another mode.
    public static CaptureMode? SelectMode(IEnumerable<CaptureMode> available, string key)
    {
        var modes = available.Where(m => m.Width > 0 && m.Height > 0 && m.FramesPerSecond > 0).ToArray();
        if (!string.IsNullOrEmpty(key)) return modes.FirstOrDefault(m => m.Key == key);
        foreach (var preference in new[] { (2560, 1440, 120), (2560, 1440, 60), (1920, 1080, 60) })
        {
            var match = modes.Where(m => m.Width == preference.Item1 && m.Height == preference.Item2 &&
                Math.Abs(m.FramesPerSecond - preference.Item3) < 0.15)
                .OrderByDescending(m => m.PixelFormat.Contains("JPEG", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (match != null) return match;
        }
        return modes.OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.FramesPerSecond).FirstOrDefault();
    }

    public static CaptureEndpoint? MatchAudio(string videoName, IEnumerable<CaptureEndpoint> endpoints)
    {
        // Never pick the first microphone as a fallback. Ambiguity requires an explicit choice.
        var matches = endpoints.Where(e => videoName.Length > 0 &&
            e.Name.Contains(videoName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static TimeSpan ReconnectDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(15, 2 * Math.Pow(2, Math.Clamp(attempt, 0, 4))));

    public static CaptureProfile Normalize(CaptureProfile profile) => profile with
    {
        VideoId = profile.VideoId ?? "", AudioId = profile.AudioId ?? "",
        OutputId = profile.OutputId ?? "", ModeKey = profile.ModeKey ?? "",
        Volume = Math.Clamp(profile.Volume, 0, 100),
        WindowWidth = double.IsFinite(profile.WindowWidth) ? Math.Clamp(profile.WindowWidth, 640, 3840) : 1100,
        WindowHeight = double.IsFinite(profile.WindowHeight) ? Math.Clamp(profile.WindowHeight, 400, 2160) : 720,
        WindowLeft = profile.WindowLeft is { } left && double.IsFinite(left) ? left : null,
        WindowTop = profile.WindowTop is { } top && double.IsFinite(top) ? top : null
    };
}

public sealed class CaptureProfileStore(string path)
{
    public CaptureProfile Load()
    {
        try { return CapturePolicy.Normalize(JsonSerializer.Deserialize<CaptureProfile>(File.ReadAllText(path)) ?? new()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(CaptureProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(CapturePolicy.Normalize(profile)));
        File.Move(temporary, path, true);
    }
}
