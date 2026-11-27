using Seein.Core.Storage;
using System.Text.RegularExpressions;

namespace Seein.Core.Apps;

public static partial class AppCompanionRules
{
    // Historical preview files may already have been moved away. Use the recorded, confirmed
    // source root, not File.Exists or a blanket "see*" executable-name rule.
    private static bool IsSeeinBuildHistory(AppMetaRow row, string? project)
    {
        var key = row.AppKey.Replace('/', '\\');
        var executable = (row.ExePath ?? row.AppKey).Replace('/', '\\');
        var name = Path.GetFileName(executable);
        var dotnet = name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        var branded = ProcessNames.IsSelfExecutable(name) || ProcessNames.Catalog.Values.Any(entry =>
            entry.Legacy.Any(alias => executable.EndsWith("\\" + Path.Combine(
                Path.GetDirectoryName(entry.Path.Replace('/', '\\')) ?? "", alias), StringComparison.OrdinalIgnoreCase))) ||
            SeeinDevelopmentExecutable().IsMatch(name);
        if (!branded && !dotnet) return false;

        if (project is not null)
        {
            var prefix = project.TrimEnd('\\') + "\\";
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var relative = key[prefix.Length..];
                if (dotnet)
                {
                    if (relative.StartsWith(@".build\dotnet\", StringComparison.OrdinalIgnoreCase)) relative = relative[14..];
                    return IsSeeinProjectDirectory(relative) ||
                        relative.StartsWith(@"tools\diagnostics\", StringComparison.OrdinalIgnoreCase) &&
                        SeeinDevelopmentExecutable().IsMatch(Path.GetFileName(relative) + ".exe");
                }
                if (relative.StartsWith(@".build\", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(@"artifacts\releases\", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(@"src\seein.", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(@"src\modules\", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(@"tools\diagnostics\", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(@"tests\seein.tests\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            var sdk = Path.GetDirectoryName(executable);
            if (dotnet && sdk is not null && Path.GetFileName(sdk).Equals("JonsboPanelSdk", StringComparison.OrdinalIgnoreCase) &&
                key.StartsWith(sdk + "\\", StringComparison.OrdinalIgnoreCase) && IsSeeinProjectDirectory(key[(sdk.Length + 1)..])) return true;
        }

        // The dedicated QA folder plus the branded executable identifies an old standalone
        // test host; unrelated copies of Everything, Node and other vendor tools stay separate.
        return SeeinQaDirectory().IsMatch(key) && (dotnet || SeeinDevelopmentExecutable().IsMatch(name));
    }

    [GeneratedRegex(@"\Aseein[.a-z0-9_-]*(?:preview|qa|probe|packagetool|pethost|themetool)[.a-z0-9_-]*\.exe\z", RegexOptions.IgnoreCase)]
    private static partial Regex SeeinDevelopmentExecutable();
}
