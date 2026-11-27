using Seein.Core;
using Seein.Core.Apps;
using Seein.Core.Storage;
using Seein.Core.Tools;

namespace Seein.Tests;

public sealed class ProcessNamingTests
{
    [Theory]
    [InlineData("https://example.com/path?a=1&b=2")]
    [InlineData("file:///C:/Temp/report.pdf")]
    public void ExternalTargetsAreHandedToTheShellWithoutCommandArguments(string target)
    {
        var start = ToolExternalLaunch.Create(target);
        Assert.True(start.UseShellExecute);
        Assert.Empty(start.Arguments);
        Assert.Empty(start.ArgumentList);
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("relative.txt")]
    public void TheExternalBrokerRejectsNonDocumentSchemes(string target) =>
        Assert.Throws<ArgumentException>(() => ToolExternalLaunch.Create(target));
    [Fact]
    public void EveryRoleHasAUniqueSafeExecutableAndAnAcyclicOwner()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, entry) in ProcessNames.Catalog)
        {
            Assert.False(Path.IsPathRooted(entry.Path));
            Assert.DoesNotContain("..", entry.Path);
            Assert.EndsWith(".exe", entry.Path);
            Assert.True(names.Add(ProcessNames.FileName(role)), role);
            if (role != "main") Assert.StartsWith("see", ProcessNames.FileName(role));
            var visited = new HashSet<string> { role };
            var owner = entry.Owner;
            while (ProcessNames.Catalog.TryGetValue(owner, out var parent))
            {
                Assert.True(visited.Add(owner), $"Owner cycle for {role}");
                owner = parent.Owner;
            }
            Assert.Contains(owner, new[] { "login", "services", "build" });
        }
    }

    [Theory]
    [InlineData(@"D:\Program Files\Seein\Plugins\capture\tools\snow\seeshot.exe", "seeshot")]
    [InlineData(@"D:\Program Files\Seein\Plugins\efficiency\tools\pane\seewin.exe", "seewin")]
    [InlineData(@"D:\Program Files\Seein\Plugins\devices\rgb-engine\seergb.exe", "seergb")]
    [InlineData(@"D:\Program Files\Seein\Plugins\display\tools\seefps.exe", "seefps")]
    [InlineData(@"D:\Program Files\Seein\App\tools\snow\snow_shot.exe", "seeshot")]
    [InlineData(@"D:\Program Files\Seein\App\tools\pane\Pane.App.exe", "seewin")]
    [InlineData(@"D:\Program Files\Seein\App\Seein.PetHost.exe", "seepet")]
    [InlineData(@"D:\Program Files\Seein\App\rgb-engine\seergb.exe", "seergb")]
    [InlineData(@"D:\Program Files\Seein\App\tools\snow\assets\ocr\runtimes\1.0\windows-x64\seeocr.exe", "seeocr")]
    public void HistoricalAndCurrentOwnedPathsUseTheSameBrand(string path, string expected)
    {
        Assert.True(ProcessNames.IsManagedRuntimePath(path));
        Assert.Equal(expected, AppNameCatalog.DisplayName(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Everything\Everything.exe")]
    [InlineData(@"C:\Game\crashpad_handler.exe")]
    [InlineData(@"C:\Intel\PresentMon.exe")]
    public void ExternalVendorToolsAreNotRebrandedOrClaimed(string path)
    {
        Assert.False(ProcessNames.IsManagedRuntimePath(path));
        Assert.Null(ProcessNames.DisplayNameForPath(path));
    }

    [Fact]
    public void ServiceHelpersMergeIntoSeeinWithoutTakingAnExternalTool()
    {
        const string helper = @"d:\program files\seein\app\tools\seefps.exe";
        const string external = @"c:\intel\presentmon.exe";
        var rows = new Dictionary<string, AppMetaRow>
        {
            [AppStatKeys.Self] = new(AppStatKeys.Self, null, "SEEIN", "auto"),
            [helper] = new(helper, helper, "seefps", "auto"),
            [external] = new(external, external, "PresentMon", "auto")
        };
        var matches = AppCompanionRules.Match(rows);
        Assert.Equal(AppStatKeys.Self, matches[helper]);
        Assert.False(matches.ContainsKey(external));
        rows[helper] = rows[helper] with { MergedInto = @"d:\program files\seein\app\dsh-pet\dsh-pet.exe" };
        Assert.Equal(AppStatKeys.Self, AppCompanionRules.Match(rows)[helper]);
        rows[helper] = rows[helper] with { MergeDetached = true };
        Assert.False(AppCompanionRules.Match(rows).ContainsKey(helper));
    }
}
