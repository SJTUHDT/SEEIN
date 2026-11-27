namespace Seein.Core.Apps;

/// <summary>
/// Groups processes into applications.  The rules are ordered deliberately: a shell process such as
/// explorer.exe launches every user app, so merging by "root ancestor" would collapse the whole
/// desktop into one entry.  Runtime hosts (electron, node, TinyDL, ...) are not applications either;
/// their real identity comes from the command line, the executable folder or the parent process.
/// </summary>
public sealed class AppIdentityResolver
{
    private static readonly HashSet<string> SelfExeNames = new[] { "main", "service" }
        .SelectMany(role => ProcessNames.Catalog[role].Legacy.Append(ProcessNames.FileName(role)))
        .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "services", "svchost", "wininit", "smss", "csrss", "winlogon",
        "taskhostw", "dwm", "conhost", "cmd", "powershell", "pwsh"
    };

    private static readonly HashSet<string> SystemExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "lsass", "fontdrvhost", "spoolsv",
        "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "WmiPrvSE", "dllhost",
        "RuntimeBroker", "backgroundTaskHost", "taskhostw", "sihost", "ApplicationFrameHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ctfmon", "csrss",
        "winlogon", "smss", "AggregatorHost", "audiodg", "WidgetService", "WidgetBoard",
        "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv", "explorer", "dwm",
        "sppsvc", "WUDFHost", "GamingServices", "GamingServicesNet"
    };

    // Windows ships a few user facing tools in system32; they are applications, not background services.
    private static readonly HashSet<string> UserToolsInWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "mspaint", "calc", "mstsc", "SnippingTool", "snippingtool", "Taskmgr",
        "powershell", "pwsh", "cmd", "WindowsTerminal", "wt", "mmc", "regedit"
    };

    // Executables that are a runtime or a vendor component rather than an application of their own.
    private static readonly HashSet<string> RuntimeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "electron", "node", "dotnet", "python", "pythonw", "java", "javaw",
        "TinyDL", "TinyDL64", "tiny_dl", "conhost"
    };

    // Folder names that belong to the plumbing of an application, not to the product itself.
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtime", "bin", "dist", "node_modules", "app", "resources", "lib", "src", "build",
        "electron-helper", "release", "win64", "x64", "x86", "binaries", "electron", "content",
        "win-unpacked", "out", "sdk", "roslyn", "bincore"
    };

    // Vendor install folders: anything running from inside one of these belongs to that product.
    private static readonly HashSet<string> VendorRoots = new(StringComparer.OrdinalIgnoreCase) { "wegame" };

    // Value = allowed parent executable names (empty means any user application).
    private static readonly Dictionary<string, string[]> ScopedHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crashpad_handler"] = [],
        ["msedgewebview2"] = [],
        ["df_helper_main"] = [],
        ["val_helper_main"] = ["wegame"],
        ["steamwebhelper"] = ["steam"],
        ["WeChatAppEx"] = ["Weixin", "WeChat"],
        ["UplayWebCore"] = ["upc"],
        ["sandbox-center"] = ["WorkBuddyAI"],
        ["rail"] = ["wegame"],
        ["browser"] = ["wegame"],
        ["wegame_env"] = ["wegame"],
        ["GameViewerHealthd"] = ["GameViewer"]
    };

    private readonly Dictionary<string, string?> _folderKeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys that used to be recorded under a runtime host name and should be folded into the real app.</summary>
    public Dictionary<string, string> PendingRemaps { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<int, string> Resolve(IReadOnlyList<ProcessSample> processes,
        Func<ProcessSample, string?>? commandLineProvider = null)
    {
        var byPid = new Dictionary<int, ProcessSample>(processes.Count);
        foreach (var process in processes) byPid[process.Pid] = process;

        var result = new Dictionary<int, string>(processes.Count);
        var unresolvedHosts = new List<ProcessSample>();
        var productScoped = new HashSet<int>();

        foreach (var process in processes)
        {
            var stem = ExeStem(process.ExeName);
            if (SelfExeNames.Contains(stem)) { result[process.Pid] = AppStatKeys.Self; continue; }
            if (ProcessNames.IsManagedRuntimePath(process.ExePath)) { result[process.Pid] = KeyOf(process); continue; }
            if (AppCompanionRules.HasProductScopedPath(process.ExePath))
            {
                // Instances can lose their parent PID; keep a key the catalog can resolve by path.
                productScoped.Add(process.Pid);
                result[process.Pid] = IsSystem(process, stem) ? AppStatKeys.System : KeyOf(process);
                continue;
            }
            if (IsSystem(process, stem)) { result[process.Pid] = AppStatKeys.System; continue; }

            if (RuntimeHosts.Contains(stem))
            {
                var explicitKey = HostKeyFromCommandLine(process, commandLineProvider?.Invoke(process));
                if (explicitKey is not null) result[process.Pid] = explicitKey;
                else unresolvedHosts.Add(process);
                continue;
            }

            result[process.Pid] = ClassifyApplication(process, byPid);
        }

        // Runtime hosts without a usable command line inherit the key of the process that launched
        // them (Electron renderer and GPU helpers), then fall back to their own folder.
        foreach (var process in unresolvedHosts)
        {
            var key = InheritedKey(process, byPid, result) ?? FolderKeyOf(process.ExePath) ?? KeyOf(process);
            result[process.Pid] = key;
        }

        // Anything that was historically recorded under its own executable but now belongs to a
        // runtime host or a vendor folder gets its history folded into the resolved application.
        foreach (var process in processes)
        {
            // A session-zero instance may share the executable with the user's companion.
            if (productScoped.Contains(process.Pid)) continue;
            var naive = KeyOf(process);
            var resolved = result[process.Pid];
            if (string.Equals(naive, resolved, StringComparison.Ordinal)) continue;
            if (resolved == AppStatKeys.Self) continue;
            PendingRemaps[naive] = resolved;
        }

        return result;
    }

    public static string KeyOf(ProcessSample process) =>
        (process.ExePath ?? "exe:" + process.ExeName).ToLowerInvariant();

    public static string ExeStem(string exeName) => Path.GetFileNameWithoutExtension(exeName);

    /// <summary>Windows own binaries and packaged (WindowsApps) components count as system processes.</summary>
    public static bool IsSystemPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 &&
            exePath.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        return exePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyApplication(ProcessSample process, IReadOnlyDictionary<int, ProcessSample> byPid)
    {
        var stem = ExeStem(process.ExeName);
        if (ScopedHelpers.TryGetValue(stem, out var allowedParents))
        {
            var ancestor = FindUserAppAncestor(process, byPid);
            if (ancestor is not null)
            {
                var ancestorStem = ExeStem(ancestor.ExeName);
                if (allowedParents.Length == 0 || allowedParents.Contains(ancestorStem, StringComparer.OrdinalIgnoreCase))
                    return KeyOf(ancestor);
            }
        }
        return VendorRootKey(process) ?? KeyOf(process);
    }

    private static string? VendorRootKey(ProcessSample process) =>
        process.ExePath is null ? null : VendorFolderKey(process.ExePath);

    /// <summary>
    /// Resolves a runtime host from its command line: an explicit <c>parent="..."</c> argument (vendor
    /// launchers), an Electron <c>--app-path=</c> argument, or the first path argument (Electron
    /// main.js, node entry script).
    /// </summary>
    private string? HostKeyFromCommandLine(ProcessSample process, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var parentPath = QuotedValue(commandLine, "parent=");
        if (parentPath is not null && PathKeyOf(parentPath) is { } parentKey) return parentKey;

        var appPath = QuotedValue(commandLine, "--app-path=");
        if (appPath is not null && PathKeyOf(appPath) is { } appKey) return appKey;

        var argument = FirstPathArgument(commandLine, process.ExePath);
        return argument is null ? null : PathKeyOf(argument);
    }

    private string? InheritedKey(ProcessSample process, IReadOnlyDictionary<int, ProcessSample> byPid,
        IReadOnlyDictionary<int, string> resolved)
    {
        var current = process;
        for (var depth = 0; depth < 6; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent)) return null;
            if (resolved.TryGetValue(parent.Pid, out var key) && key is not (AppStatKeys.System or AppStatKeys.Self))
                return key;
            current = parent;
        }
        return null;
    }

    /// <summary>An explicit executable path keeps the usual exe based key, so that
    /// <c>parent="...\wegame.exe"</c> lands on exactly the same key as wegame.exe itself.</summary>
    private string? PathKeyOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim('"'));
            // A path inside a vendor folder always resolves to that product, so "...\WeGame\wegame.exe"
            // and "...\WeGame\TinyDL64.exe" end up on exactly the same key.
            if (VendorFolderKey(full) is { } vendorKey) return vendorKey;
            if (full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return full.ToLowerInvariant();
        }
        catch { return null; }
        return FolderKeyOf(path);
    }

    private static string? VendorFolderKey(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = parts.Length - 2; index >= 1; index--)
        {
            if (!VendorRoots.Contains(parts[index])) continue;
            return string.Join(Path.DirectorySeparatorChar, parts.Take(index + 1)).ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Walks up from a file or folder until it finds a folder that names a product.</summary>
    private string? FolderKeyOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_folderKeyCache.TryGetValue(path, out var cached)) return cached;

        string? key = null;
        try
        {
            var full = Path.GetFullPath(path.Trim('"'));
            var directory = Path.HasExtension(full) || !Directory.Exists(full) ? Path.GetDirectoryName(full) : full;
            for (var depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
            {
                var name = Path.GetFileName(directory);
                // Version-like folders (8.0.426, 1.2.3.4) are plumbing, not product names.
                var versionLike = name.Length > 0 && name.All(c => char.IsDigit(c) || c == '.');
                if (!string.IsNullOrEmpty(name) && !versionLike && !GenericFolders.Contains(name))
                {
                    key = directory.ToLowerInvariant();
                    break;
                }
                directory = Path.GetDirectoryName(directory);
            }
        }
        catch { key = null; }

        _folderKeyCache[path] = key;
        return key;
    }

    private static string? QuotedValue(string commandLine, string prefix)
    {
        var index = commandLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var start = index + prefix.Length;
        if (start >= commandLine.Length) return null;
        if (commandLine[start] == '"')
        {
            var endQuote = commandLine.IndexOf('"', start + 1);
            return endQuote > start ? commandLine[(start + 1)..endQuote] : null;
        }
        var end = commandLine.IndexOf(' ', start);
        return end < 0 ? commandLine[start..] : commandLine[start..end];
    }

    private static string? FirstPathArgument(string commandLine, string? exePath)
    {
        var parts = SplitArguments(commandLine);
        for (var index = 1; index < parts.Count; index++)
        {
            var argument = parts[index];
            if (argument.StartsWith('-') || argument.StartsWith('/')) continue;
            if (argument.Contains('=')) continue;
            if (!argument.Contains('\\') && !argument.Contains('/') && !Path.HasExtension(argument)) continue;
            try
            {
                if (Path.IsPathRooted(argument)) return argument;
                var baseDirectory = exePath is null ? null : Path.GetDirectoryName(exePath);
                return baseDirectory is null ? null : Path.Combine(baseDirectory, argument);
            }
            catch { return null; }
        }
        return null;
    }

    private static List<string> SplitArguments(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (character == ' ' && !quoted)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static ProcessSample? FindUserAppAncestor(ProcessSample process, IReadOnlyDictionary<int, ProcessSample> byPid)
    {
        var current = process;
        for (var depth = 0; depth < 6; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent)) return null;
            var stem = ExeStem(parent.ExeName);
            if (SelfExeNames.Contains(stem) || IsSystem(parent, stem)) return null;
            if (ShellHosts.Contains(stem) || RuntimeHosts.Contains(stem)) { current = parent; continue; }
            if (!ScopedHelpers.ContainsKey(stem)) return parent;
            current = parent;
        }
        return null;
    }

    private static bool IsSystem(ProcessSample process, string stem)
    {
        if (process.SessionId == 0) return true;
        if (SystemExeNames.Contains(stem)) return true;
        if (UserToolsInWindows.Contains(stem)) return false;
        if (process.ExePath is null) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 &&
               process.ExePath.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
