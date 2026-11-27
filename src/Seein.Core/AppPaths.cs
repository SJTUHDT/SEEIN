using System.Text.Json;
namespace Seein.Core;

public sealed record AppPaths(string DataRoot)
{
    public string Themes => Path.Combine(DataRoot, "themes");
    public string Settings => Path.Combine(DataRoot, "settings.json");
    public string LegacyDatabase => Path.Combine(DataRoot, "energy.db");
    public string Database => Path.Combine(DataRoot, "energy-v2.db");
    public string AppDatabase => Path.Combine(DataRoot, "app-usage.db");
    public string Icons => Path.Combine(DataRoot, "icons");
    public string Logs => Path.Combine(DataRoot, "logs");


    // A non-empty SEEIN_INSTANCE suffix lets a development build run side by side
    // with an installed copy: separate data folder, pipe name and single-instance mutex.
    public static string InstanceSuffix { get; } = ReadInstanceSuffix();
    public static string FolderName { get; } = InstanceSuffix.Length == 0 ? "Seein" : "Seein" + InstanceSuffix;

    // The product used to be called JonsboPanel; carry the existing data over on first start.
    static AppPaths() => TryMigrateLegacyFolders();

    private static void TryMigrateLegacyFolders()
    {
        if (InstanceSuffix.Length != 0) return;
        try
        {
            Migrate(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            Migrate(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        catch { }

        static void Migrate(string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            var target = Path.Combine(root, "Seein");
            var legacy = Path.Combine(root, "JonsboPanel");
            if (!Directory.Exists(legacy)) return;
            Directory.CreateDirectory(target);
            var pending = new Stack<string>();
            pending.Push(legacy);
            while (pending.TryPop(out var sourceDirectory))
            {
                var relative = Path.GetRelativePath(legacy, sourceDirectory);
                var destinationDirectory = relative == "." ? target : Path.Combine(target, relative);
                Directory.CreateDirectory(destinationDirectory);
                foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
                {
                    var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
                    if (!File.Exists(destinationFile))
                    {
                        try { File.Copy(sourceFile, destinationFile); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(sourceDirectory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
            }
        }
    }

    public static AppPaths SystemDefault => new(PreviewRoot("SEEIN_PREVIEW_SYSTEM_DATA",Environment.SpecialFolder.CommonApplicationData));
    public static AppPaths UserDefault => new(PreviewRoot("SEEIN_PREVIEW_USER_DATA",Environment.SpecialFolder.LocalApplicationData));
    private static string PreviewRoot(string variable,Environment.SpecialFolder standard)
    {
        var path=InstanceSuffix.Length!=0?Environment.GetEnvironmentVariable(variable):null;
        return path is not null&&Path.IsPathFullyQualified(path)?Path.GetFullPath(path):Path.Combine(Environment.GetFolderPath(standard),FolderName);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Themes);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Icons);
    }

    private static string ReadInstanceSuffix()
    {
        var raw = Environment.GetEnvironmentVariable("SEEIN_INSTANCE");
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var builder = new System.Text.StringBuilder();
        foreach (var value in raw.Trim())
            if (char.IsAsciiLetterOrDigit(value) || value is '-' or '_') builder.Append(value);
        return builder.Length == 0 ? string.Empty : "-" + builder.ToString().ToLowerInvariant();
    }
}

public sealed record PanelSettings
{
    public string ActiveThemeId { get; init; } = "white-basic";
    public double PowerBaselineWatts { get; init; } = 74;
    public double PowerEfficiency { get; init; } = .90;
    public bool EnablePresentMon { get; init; } = true;

    // App usage / app power statistics.
    public bool AppStatsEnabled { get; init; } = true;
    public int AppStatsSampleSeconds { get; init; } = 5;
    public double AppStatsMinCpuSharePercent { get; init; } = .1;
    public bool AppStatsShowAll { get; init; } = false;
    public string[] AppStatsAlwaysShow { get; init; } = [];

    // Hardware tooling (1.3.0): fan curves and lighting. Missing sections fall back to defaults,
    // which keeps settings.json written by older builds valid.
    public Thermal.FanSettings? Fans { get; init; }
    public Lighting.RgbSettings? Rgb { get; init; }

    // Plugin-owned settings retain their existing JSON field names.
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string,System.Text.Json.JsonElement>? AdditionalSettings { get; init; }
    public T? GetSection<T>(string name) where T:class => AdditionalSettings is not null && AdditionalSettings.FirstOrDefault(p=>p.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value is var value && value.ValueKind!=System.Text.Json.JsonValueKind.Undefined ? value.Deserialize<T>(JsonDefaults.Options) : null;
    public PanelSettings WithSection<T>(string name,T value) => this with { AdditionalSettings = new(AdditionalSettings ?? [],StringComparer.OrdinalIgnoreCase) { [name] = System.Text.Json.JsonSerializer.SerializeToElement(value,JsonDefaults.Options) } };
}
