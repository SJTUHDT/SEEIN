using System.Text.RegularExpressions;
using Seein.Core.Storage;

namespace Seein.Core.Apps;

/// <summary>Read-time aliases for companion processes. The underlying usage keys remain independent.</summary>
public static partial class AppCompanionRules
{
    private sealed record Family(string Directory, string[] Owners, string[] Helpers);

    private static readonly Family[] Families =
    [
        new(@"\amd\", ["radeonsoftware.exe"], ["amdow.exe", "presentmon-x64.exe", "auepmaster.exe"]),
        new(@"\qqpcmgr\", ["qmlauncher.exe", "qqpctray.exe"], ["qqpcsoftcmd.exe", "qqpcmgrupdate.exe", "wechatocr.exe"]),
        new(@"\msi center\", ["msi.terminalserver.exe", "msicenter.exe"], ["msi.toastserver.exe"]),
        new(@"\heyboxchat\", ["heyboxchat.exe"], ["p2p-tcp-tunnel.exe"]),
        new(@"\gameviewer\", ["gameviewer.exe"], ["crashpad_handler.exe", "gameviewerhealthd.exe"]),
        new(@"\atk hub\", ["atk hub.exe", "atk-hub.exe"], ["atk-hub-service.exe"]),
        new(@"\weixin\", ["weixin.exe"], ["wechatocr.exe", "wechatappex.exe"]),
        new(@"\dingding\", ["dingtalk.exe"], ["assistprocess.exe", "tblive.exe", "kashost.exe", "dingtalkupdater.exe"])
    ];

    private static readonly HashSet<string> SharedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "electron.exe", "python.exe", "pythonw.exe", "dotnet.exe", "java.exe", "javaw.exe",
        "msedgewebview2.exe", "crashpad_handler.exe", "codex.exe", "runtimebroker.exe", "svchost.exe",
        "update.exe", "launcher.exe", "setup.exe"
    };

    private static readonly HashSet<string> SharedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "Common Files", "ProgramData", "Windows", "WindowsApps",
        "System32", "SysWOW64", "SystemApps",
        "Users", "AppData", "Local", "LocalLow", "Roaming", "Programs", "Desktop", "Downloads", "Temp",
        "Documents", "Microsoft Shared", "steamapps", "common"
    };

    private static readonly HashSet<string> SupportDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "bin", "binaries", "resources", "lib", "plugins", "runtime", "runtimes", "node_modules",
        "tools", "native", "dist", "x86", "x64", "win32", "win64", "win-x64"
    };

    public static Dictionary<string, string> Match(IReadOnlyDictionary<string, AppMetaRow> meta)
    {
        var known = meta.Keys.Where(IsExecutablePath).ToArray();
        var byName = known.ToLookup(key => Path.GetFileName(key)!, StringComparer.OrdinalIgnoreCase);
        var byDirectory = known.Where(key => CanOwnCompanions(key, meta[key]))
            .ToLookup(key => Path.GetDirectoryName(key)!, StringComparer.OrdinalIgnoreCase);
        var confirmedOwners = meta.Values.Where(row => row.MergedInto is not null)
            .Select(row => row.MergedInto!).Where(IsExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var learned = meta.Values
            .Where(row => IsExecutablePath(row.AppKey) && row.MergedInto is { } target &&
                !row.MergeDetached && IsExecutablePath(target) && !SameKey(row.AppKey, target) && meta.ContainsKey(target))
            .ToLookup(row => VersionIndependentPath(row.AppKey), StringComparer.OrdinalIgnoreCase);
        var seeinProject = FindSeeinProject(meta.Keys);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, row) in meta)
        {
            if (key == AppStatKeys.Self || row.MergeDetached || row.SystemOverride.HasValue)
                continue;

            string? target = null;
            if (meta.ContainsKey(AppStatKeys.Self) &&
                (ProcessNames.IsManagedRuntimePath(row.ExePath ?? key) || IsSeeinBuildHistory(row, seeinProject)))
            {
                if (row.MergedInto != AppStatKeys.Self) result[key] = AppStatKeys.Self;
                continue;
            }
            if (!string.IsNullOrEmpty(row.MergedInto)) continue;
            if (IsExecutablePath(key))
            {
                var matches = learned[VersionIndependentPath(key)].Select(match => match.MergedInto!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                if (matches.Length > 1) continue;
                target = matches.SingleOrDefault() ?? MatchFamily(key, byName) ??
                    SeeinDevelopmentOwner(row, seeinProject) ?? MatchAncestor(key, byDirectory, confirmedOwners);
            }
            else if (key.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
            {
                var exe = key[4..];
                if (SharedExecutables.Contains(exe)) continue;
                var candidates = byName[exe].Take(2).ToArray();
                if (candidates.Length == 1) target = candidates[0];
            }
            else
            {
                target = SeeinDevelopmentOwner(row, seeinProject);
            }

            if (target is not null && !SameKey(key, target)) result[key] = target;
        }
        return result;
    }

    private static string? FindSeeinProject(IEnumerable<string> keys)
    {
        var paths = keys.Where(Path.IsPathFullyQualified).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = paths.Where(path => SameKey(Path.GetFileName(path), "seein") &&
            (paths.Contains(Path.Combine(path, "src", "seein.service")) ||
             paths.Contains(Path.Combine(path, "tests", "seein.tests")))).Take(2).ToArray();
        return roots.Length == 1 ? roots[0] : null;
    }

    private static string? SeeinDevelopmentOwner(AppMetaRow row, string? project)
    {
        if (project is null || !Path.IsPathFullyQualified(row.AppKey)) return null;
        var executable = Path.GetFileName(row.ExePath ?? row.AppKey);
        if (IsExecutablePath(row.AppKey))
        {
            var name = Path.GetFileName(row.AppKey);
            return row.AppKey.StartsWith(project + @"\", StringComparison.OrdinalIgnoreCase) &&
                (SameKey(name, "Seein.ThemeTool.exe") ||
                 SameKey(name, "testhost.exe") && row.AppKey.StartsWith(project + @"\tests\seein.tests\", StringComparison.OrdinalIgnoreCase))
                ? project : null;
        }
        if (!SameKey(executable, "dotnet.exe")) return null;

        var projectPrefix = project + @"\";
        if (row.AppKey.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase) &&
            IsSeeinProjectDirectory(row.AppKey[projectPrefix.Length..])) return project;

        const string sdkFolder = @"\AppData\Local\JonsboPanelSdk\";
        var sdk = row.AppKey.IndexOf(sdkFolder, StringComparison.OrdinalIgnoreCase);
        if (sdk >= 0 && IsSeeinProjectDirectory(row.AppKey[(sdk + sdkFolder.Length)..])) return project;

        return SeeinQaDirectory().IsMatch(row.AppKey) ? project : null;
    }

    private static bool IsSeeinProjectDirectory(string relative) =>
        relative.StartsWith(@"src\seein.", StringComparison.OrdinalIgnoreCase) ||
        SameKey(relative, @"tests\seein.tests") ||
        relative.StartsWith(@"tests\seein.tests\", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\\AppData\\Local\\Temp\\seein[a-z0-9]*qa(?:-[0-9a-f]{32})?(?:\\|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SeeinQaDirectory();

    private static string? MatchAncestor(string path, ILookup<string, string> byDirectory,
        IReadOnlySet<string> confirmedOwners)
    {
        var start = Path.GetDirectoryName(path);
        for (var directory = start; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name) || SharedDirectories.Contains(name)) break;

            var entries = byDirectory[directory];
            var primary = entries.Where(key => confirmedOwners.Contains(key) ||
                LooksLikeProductExecutable(key, directory)).Take(2).ToArray();
            if (primary.Length > 0)
                return primary.Length == 1 && !SameKey(primary[0], path) ? primary[0] : null;

            // A sibling or a generic tooling folder alone is not evidence of a parent application.
            if (SameKey(directory, start!) || SupportDirectories.Contains(name)) continue;
            var candidates = entries.Take(2).ToArray();
            if (candidates.Length > 0) return candidates.Length == 1 ? candidates[0] : null;
        }
        return null;
    }

    private static bool CanOwnCompanions(string key, AppMetaRow row)
    {
        var name = Path.GetFileName(key);
        if (row.SystemOverride is 1 || row.MergedInto is { } target && IsExecutablePath(target) ||
            SharedExecutables.Contains(name)) return false;
        return !HasProductScopedPath(key);
    }

    internal static bool HasProductScopedPath(string? path)
    {
        if (path is null || !IsExecutablePath(path)) return false;
        var name = Path.GetFileName(path);
        return Families.Any(family => family.Helpers.Contains(name, StringComparer.OrdinalIgnoreCase) &&
            ProductRoot(path, family.Directory) is not null);
    }

    private static bool LooksLikeProductExecutable(string key, string directory)
    {
        var name = Path.GetFileName(directory);
        if (SupportDirectories.Contains(name)) name = Path.GetFileName(Path.GetDirectoryName(directory));
        if (string.IsNullOrEmpty(name)) return false;
        static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
        return SameKey(Normalize(Path.GetFileNameWithoutExtension(key)), Normalize(name));
    }

    private static string? MatchFamily(string path, ILookup<string, string> byName)
    {
        var executable = Path.GetFileName(path);
        foreach (var family in Families)
        {
            if (!family.Helpers.Contains(executable, StringComparer.OrdinalIgnoreCase) ||
                ProductRoot(path, family.Directory) is not { } root) continue;
            foreach (var owner in family.Owners)
            {
                var candidates = byName[owner].Where(key => SameKey(ProductRoot(key, family.Directory), root))
                    .Take(2).ToArray();
                if (candidates.Length > 0) return candidates.Length == 1 ? candidates[0] : null;
            }
        }
        return null;
    }

    private static string? ProductRoot(string path, string directory)
    {
        var index = path.IndexOf(directory, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : path[..(index + directory.Length)];
    }

    private static bool SameKey(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutablePath(string key) =>
        key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(key);

    private static string VersionIndependentPath(string path) => PathVersion().Replace(path, "#version");

    // Preserve the installation root, relative location, and full package name/publisher.
    // Only standalone numeric directory names and a Windows package's version field may vary.
    [GeneratedRegex(@"(?<=\\)\d+(?:\.\d+){1,4}(?=\\)|(?<=_)\d+(?:\.\d+){3}(?=_(?:x64|x86|arm64|neutral)_)", RegexOptions.IgnoreCase)]
    private static partial Regex PathVersion();
}
