using System.Text.Json;

namespace Seein.Core;

/// <summary>Single executable-name contract shared with packaging and installation.</summary>
public static class ProcessNames
{
    private static readonly Lazy<string?> LegacyIndexPath = new(() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\SEEIN\Dependencies", "LegacyEverythingExecutable", null) as string);
    public sealed record Entry(string Path, string Owner, string[] Legacy);
    public static IReadOnlyDictionary<string, Entry> Catalog { get; } = Load();
    private static IReadOnlyDictionary<string, Entry> Load()
    {
        using var stream = typeof(ProcessNames).Assembly.GetManifestResourceStream("Seein.ProcessNames.json")
            ?? throw new InvalidOperationException("SEEIN process catalog is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Entry>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    public static string FileName(string role) => System.IO.Path.GetFileName(Catalog[role].Path);
    public static string ModuleDirectory(string id,string? appRoot=null)
    {
        var app=Path.GetFullPath(appRoot??AppContext.BaseDirectory);
        var root=Directory.GetParent(app.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var candidate=Path.Combine(root,"Plugins",id);
        return Directory.Exists(candidate)?candidate:app;
    }
    private static string? OwnerModule(string role)=>role switch
    {
        "rgb"=>"devices","webPet"=>"companion","capture" or "ocr" or "crash" or "updater"=>"capture",
        "efficiency" or "index"=>"efficiency","fps"=>"display",_=>null
    };
    public static string Executable(string role,string? appRoot=null)
    {
        var app=appRoot??AppContext.BaseDirectory;var module=OwnerModule(role);
        return Path.GetFullPath(Path.Combine(module is null?app:ModuleDirectory(module,app),Catalog[role].Path));
    }
    public static bool IsSelfExecutable(string fileName) => Catalog.Values.Any(entry =>
        string.Equals(System.IO.Path.GetFileName(entry.Path), fileName, StringComparison.OrdinalIgnoreCase));
    public static bool IsManagedRuntimePath(string? path)
    {
        if(path is null)return false;var normalized=path.Replace('/','\\');
        if(string.Equals(normalized,LegacyIndexPath.Value,StringComparison.OrdinalIgnoreCase))return true;
        if(AppPaths.InstanceSuffix.Length!=0&&string.Equals(path,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))return true;
        const string appMarker="\\Seein\\App\\",pluginMarker="\\Seein\\Plugins\\";
        var index=normalized.LastIndexOf(appMarker,StringComparison.OrdinalIgnoreCase);
        if(index>=0)return Matches(normalized[(index+appMarker.Length)..],null);
        index=normalized.LastIndexOf(pluginMarker,StringComparison.OrdinalIgnoreCase);
        if(index>=0){var relative=normalized[(index+pluginMarker.Length)..];var slash=relative.IndexOf('\\');return slash>0&&Matches(relative[(slash+1)..],relative[..slash]);}
        if(AppPaths.InstanceSuffix.Length!=0)
        {
            var install=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..")).TrimEnd('\\')+"\\";
            if(normalized.StartsWith(install,StringComparison.OrdinalIgnoreCase))
            {
                var relative=normalized[install.Length..];if(relative.StartsWith("App\\",StringComparison.OrdinalIgnoreCase))return Matches(relative[4..],null);
                if(relative.StartsWith("Plugins\\",StringComparison.OrdinalIgnoreCase)){relative=relative[8..];var slash=relative.IndexOf('\\');return slash>0&&Matches(relative[(slash+1)..],relative[..slash]);}
            }
        }
        return false;
        static bool Matches(string relative,string? module)
        {
            if((module is null||module.Equals("capture",StringComparison.OrdinalIgnoreCase))&&relative.StartsWith(@"tools\snow\assets\ocr\runtimes\",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(relative).Equals(FileName("ocr"),StringComparison.OrdinalIgnoreCase))return true;
            return Catalog.Any(pair=>(module is null||string.Equals(OwnerModule(pair.Key),module,StringComparison.OrdinalIgnoreCase))&&
                (relative.Equals(pair.Value.Path.Replace('/','\\'),StringComparison.OrdinalIgnoreCase)||pair.Value.Legacy.Any(alias=>relative.Equals(Path.Combine(Path.GetDirectoryName(pair.Value.Path)??"",alias).Replace('/','\\'),StringComparison.OrdinalIgnoreCase))));
        }
    }
    public static string? DisplayNameForPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (string.Equals(normalized, LegacyIndexPath.Value, StringComparison.OrdinalIgnoreCase)) return "seeeverything";
        foreach (var (role, entry) in Catalog)
        {
            var leaf = System.IO.Path.GetFileName(entry.Path);
            if (string.Equals(System.IO.Path.GetFileName(normalized), leaf, StringComparison.OrdinalIgnoreCase))
                return role == "main" ? "SEEIN" : System.IO.Path.GetFileNameWithoutExtension(leaf);
            if(IsManagedRuntimePath(path)&&entry.Legacy.Any(alias=>Path.GetFileName(normalized).Equals(alias,StringComparison.OrdinalIgnoreCase)))return Path.GetFileNameWithoutExtension(leaf);
            // Historical aliases are scoped to SEEIN's install tree, never to
            // unrelated copies of Everything, PresentMon or crashpad elsewhere.
            var folder = System.IO.Path.GetDirectoryName(entry.Path.Replace('/', '\\'));
            foreach (var alias in entry.Legacy)
                if (normalized.EndsWith("\\Seein\\App\\" + (string.IsNullOrEmpty(folder) ? "" : folder + "\\") + alias, StringComparison.OrdinalIgnoreCase))
                    return System.IO.Path.GetFileNameWithoutExtension(leaf);
        }
        return null;
    }
}
