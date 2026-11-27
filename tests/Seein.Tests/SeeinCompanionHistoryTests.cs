using Microsoft.Extensions.Logging.Abstractions;
using Seein.Core;
using Seein.Core.Apps;
using Seein.Core.Storage;
using Seein.Service;

namespace Seein.Tests;

public sealed class SeeinCompanionHistoryTests
{
    private const string Project = @"c:\work\seein";
    private const string Main = @"d:\program files\seein\app\seein.tray.exe";
    private const string Helper = @"d:\program files\seein\plugins\efficiency\tools\pane\seewin.exe";

    private static Dictionary<string, AppMetaRow> Metadata(params string[] paths)
    {
        var rows = paths.ToDictionary(path => path, path => new AppMetaRow(path, path, Path.GetFileNameWithoutExtension(path), "auto"));
        rows[AppStatKeys.Self] = new(AppStatKeys.Self, Main, "SEEIN", "auto");
        rows[Project] = new(Project, null, "SEEIN 开发工具", "manual");
        rows[Project + @"\src\seein.service"] = new(Project + @"\src\seein.service", @"c:\sdk\dotnet.exe", "service", "auto");
        return rows;
    }

    [Fact]
    public void SelfMetadataWithAnExecutablePathNeverCreatesAnAliasCycle()
    {
        var aliases = AppCompanionRules.Match(Metadata(Helper));
        Assert.DoesNotContain(AppStatKeys.Self, aliases.Keys);
        Assert.Equal(AppStatKeys.Self, aliases[Helper]);
    }

    [Theory]
    [InlineData(@"c:\work\seein\artifacts\releases\v2.1.0\Seein-win-x64-2.1.0-PersonalFull\Plugins\devices\rgb-engine\seergb.exe")]
    [InlineData(@"c:\work\seein\artifacts\releases\v2.1.0\Seein-win-x64-2.1.0-Core\App\Seein.PreviewCore210.exe")]
    [InlineData(@"c:\work\seein\.build\smart-home-preview\App\Seein.DevicePreview.exe")]
    [InlineData(@"c:\work\seein\.build\modular210\publish-123\packager\Seein.PackageTool.exe")]
    [InlineData(@"c:\work\seein\.build\process-index-qa\SeeinEverythingQa.exe")]
    [InlineData(@"c:\work\seein\tools\diagnostics\SeeinEverythingQa\bin\Release\net8.0\SeeinEverythingQa.exe")]
    [InlineData(@"c:\users\dev\appdata\local\temp\seeinrgbuiqa\SeeinRgbUiQa.exe")]
    public void ConfirmedPreviewAndQaHistoryBelongsToSeein(string path) =>
        Assert.Equal(AppStatKeys.Self, AppCompanionRules.Match(Metadata(path))[path]);

    [Theory]
    [InlineData(@"c:\other\seewin.exe")]
    [InlineData(@"c:\work\seein-other\.build\Seein.DevicePreview.exe")]
    [InlineData(@"c:\work\seein\.build\dotnet\dotnet.exe")]
    [InlineData(@"c:\work\seein\.build\external\Everything.exe")]
    public void ANameAloneCannotClaimAnUnrelatedRuntime(string path) =>
        Assert.False(AppCompanionRules.Match(Metadata(path)).TryGetValue(path, out var owner) && owner == AppStatKeys.Self);

    [Fact]
    public void ExplicitDetachAndSystemClassificationArePreserved()
    {
        var rows = Metadata(Helper); rows[Helper] = rows[Helper] with { MergeDetached = true };
        Assert.DoesNotContain(Helper, AppCompanionRules.Match(rows).Keys);
        rows[Helper] = rows[Helper] with { MergeDetached = false, SystemOverride = 1 };
        Assert.DoesNotContain(Helper, AppCompanionRules.Match(rows).Keys);
    }

    [Theory]
    [InlineData(@"c:\work\seein\tests\seein.tests")]
    [InlineData(@"c:\work\seein\.build\dotnet\src\seein.core")]
    [InlineData(@"c:\work\seein\.build\dotnet\tools\diagnostics\seeinprocessqa")]
    [InlineData(@"c:\users\dev\appdata\local\temp\seeinrgbuiqa")]
    public void ConfirmedSeeinRuntimeHostHistoryAlsoFollowsTheOwner(string key)
    {
        var rows = Metadata();
        rows[key] = new(key, @"c:\sdk\dotnet.exe", "SEEIN test", "auto", MergedInto: AppStatKeys.Testing);
        Assert.Equal(AppStatKeys.Self, AppCompanionRules.Match(rows)[key]);
        Assert.Equal(AppStatKeys.Testing, rows[key].MergedInto);
    }

    [Fact]
    public void OlderBundledSdkTestHistoryUsesTheSameOwner()
    {
        const string key = @"c:\users\dev\appdata\local\JonsboPanelSdk\tests\seein.tests";
        var rows = Metadata();
        rows[key] = new(key, @"c:\users\dev\appdata\local\JonsboPanelSdk\dotnet.exe", "test", "auto");
        Assert.Equal(AppStatKeys.Self, AppCompanionRules.Match(rows)[key]);
    }

    [Fact]
    public async Task ANewDatabaseShowsInstalledAndPreviewHelpersUnderOneSeeinRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinHistoryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(root, "apps.db"));
            await repository.InitializeAsync();
            const string preview = Project + @"\.build\smart-home-preview\Seein.DevicePreview.exe";
            foreach (var row in Metadata(Helper, preview).Values)
                await repository.UpsertMetaAsync(row.AppKey, row.ExePath, row.DisplayName);
            await using var service = new AppUsageService(NullLogger<AppUsageService>.Instance, new PanelState(), repository);
            var result = await service.QueryCatalogAsync(new(), default);
            var seein = Assert.Single(result.Rows, row => row.Key == AppStatKeys.Self);
            Assert.Contains(seein.Members!, member => member.Key == Helper && member.Automatic);
            Assert.Contains(seein.Members!, member => member.Key == preview && member.Automatic);
            Assert.DoesNotContain(result.Rows, row => row.Key == Helper || row.Key == preview);
            Assert.Null(Assert.Single(await repository.GetMetaAsync(), row => row.AppKey == Helper).MergedInto);
        }
        finally { Directory.Delete(root, true); }
    }
}
