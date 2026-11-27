using Seein.Core.Apps;
using Seein.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Seein.Core;
using Seein.Service;

namespace Seein.Tests;

public sealed class AppUsageActiveAndAliasTests
{
    [Fact]
    public void ActiveSecondsOnlyAccrueWhileTheUserIsPresent()
    {
        var tracker = new AppUsageTracker();
        tracker.BeginTick();
        tracker.Observe("wechat", 1000, "2026-11-16", 5, 1, 0, 0, 0, 10, isActive: true);
        tracker.Observe("wechat", 1005, "2026-11-16", 5, 1, 0, 0, 0, 10, isActive: false);
        tracker.Observe("wechat", 1010, "2026-11-16", 5, 1, 0, 0, 0, 10, isActive: true);
        tracker.EndTick();

        var day = Assert.Single(tracker.DrainDays());
        Assert.Equal(15, day.Seconds, 6);
        Assert.Equal(10, day.ActiveSeconds, 6);
    }

    [Fact]
    public void CategoriesMatchByProductName()
    {
        var rules = CategoryCatalog.Default;
        Assert.Equal("社交", CategoryCatalog.Match(rules, @"c:\program files\tencent\weixin.exe", "微信"));
        Assert.Equal("游戏", CategoryCatalog.Match(rules, @"d:\program files (x86)\wegame\wegame.exe", "WeGame"));
        Assert.Equal("编程", CategoryCatalog.Match(rules, @"c:\program files\microsoft vs code\code.exe", "Visual Studio Code"));
        Assert.Equal(CategoryCatalog.Fallback, CategoryCatalog.Match(rules, @"c:\tools\betterleaks.exe", "betterleaks"));
    }

    [Fact]
    public void UnrealSuffixesAreStrippedBeforeMatching()
    {
        Assert.Equal("三角洲行动", AppNameCatalog.DisplayName("DeltaForceClient-Win64-Shipping.exe"));
        Assert.Equal("三角洲行动", AppNameCatalog.DisplayName(@"D:\Games\DeltaForce\DeltaForceClient-Win64-Shipping.exe"));
        Assert.Equal("刺客信条：奥德赛", AppNameCatalog.DisplayName("ACOdyssey.exe"));
    }

    [Fact]
    public void PaletteColoursAreDistinctAndStable()
    {
        var colours = Enumerable.Range(0, 12).Select(AppPalette.ColorAt).ToArray();
        Assert.Equal(colours.Length, colours.Distinct().Count());
        Assert.Equal(AppPalette.ColorAt(3), AppPalette.ColorAt(3));
        Assert.NotEqual(AppPalette.ColorAt(3), AppPalette.ColorAt(4));
    }

    [Fact]
    public void CustomRuleWinsByOrder()
    {
        var rules = new List<CategoryRule>
        {
            new("专注", "betterleaks"),
            new("游戏", "leaks")
        };
        Assert.Equal("专注", CategoryCatalog.Match(rules, @"c:\tools\betterleaks.exe", "betterleaks"));
    }

    [Fact]
    public async Task RenameHideAndMergeRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            await repository.UpsertMetaAsync("wechat", @"C:\Weixin.exe", "Weixin");

            await repository.SetDisplayNameAsync("wechat", "我的微信");
            await repository.SetHiddenAsync("wechat", true);
            await repository.SetMergedIntoAsync("wechat", "other");

            var meta = (await repository.GetMetaAsync()).ToDictionary(row => row.AppKey, StringComparer.Ordinal);
            Assert.Equal("我的微信", meta["wechat"].DisplayName);
            Assert.Equal("manual", meta["wechat"].Source);
            Assert.True(meta["wechat"].Hidden);
            Assert.Equal("other", meta["wechat"].MergedInto);
            Assert.False(meta["wechat"].MergeDetached);

            await repository.SetMergedIntoAsync("wechat", null);
            meta = (await repository.GetMetaAsync()).ToDictionary(row => row.AppKey, StringComparer.Ordinal);
            Assert.Null(meta["wechat"].MergedInto);
            Assert.True(meta["wechat"].MergeDetached);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UnmergingKeepsSameNameProcessAsIndependentCatalogRow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            await repository.UpsertMetaAsync("parent.exe", @"C:\Apps\parent.exe", "parent");
            await repository.UpsertMetaAsync("child.exe", @"C:\Apps\child.exe", "child");
            await repository.SetDisplayNameAsync("parent.exe", "共享应用");
            await repository.SetDisplayNameAsync("child.exe", "共享应用");
            await repository.SetMergedIntoAsync("child.exe", "parent.exe");
            await using var service = new AppUsageService(NullLogger<AppUsageService>.Instance, new PanelState(), repository);

            var merged = await service.QueryCatalogAsync(new AppCatalogRequest(), CancellationToken.None);
            var mergedRow = Assert.Single(merged.Rows);
            Assert.Contains(mergedRow.Members!, member => member.Key == "child.exe" && member.Merged);

            await service.ApplyMetaAsync(new AppMetaRequest("unmerge", "child.exe", null), CancellationToken.None);
            var separated = await service.QueryCatalogAsync(new AppCatalogRequest(), CancellationToken.None);
            Assert.Equal(2, separated.Rows.Count);
            Assert.Contains(separated.Rows, row => row.Key == "child.exe" && row.Name == "共享应用");

            await service.ApplyMetaAsync(new AppMetaRequest("merge", "child.exe", "parent.exe"), CancellationToken.None);
            var remerged = await service.QueryCatalogAsync(new AppCatalogRequest(), CancellationToken.None);
            Assert.Single(remerged.Rows);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AutomaticCompanionAppearsUnderItsOwnerAndCanBeDetached()
    {
        const string owner = @"c:\program files\amd\cnext\cnext\radeonsoftware.exe";
        const string helper = @"c:\program files\amd\cnext\cnext\presentmon-x64.exe";
        const string otherHelper = @"d:\other\presentmon-x64.exe";
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            foreach (var path in new[] { owner, helper, otherHelper })
                await repository.UpsertMetaAsync(path, path, Path.GetFileName(path));
            await using var service = new AppUsageService(NullLogger<AppUsageService>.Instance, new PanelState(), repository);

            var catalog = await service.QueryCatalogAsync(new AppCatalogRequest(), CancellationToken.None);
            var parent = Assert.Single(catalog.Rows, row => row.Key == owner);
            Assert.Contains(parent.Members!, member => member.Key == helper && member.Merged && member.Automatic);
            Assert.Contains(catalog.Rows, row => row.Key == otherHelper);
            Assert.Null(Assert.Single(await repository.GetMetaAsync(), row => row.AppKey == helper).MergedInto);

            await service.ApplyMetaAsync(new AppMetaRequest("unmerge", helper, null), CancellationToken.None);
            var detached = await service.QueryCatalogAsync(new AppCatalogRequest(), CancellationToken.None);
            Assert.Contains(detached.Rows, row => row.Key == helper);
            Assert.DoesNotContain(detached.Rows.Single(row => row.Key == owner).Members!, member => member.Key == helper);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void CompanionRulesReuseManualChoiceOnlyWithinTheSameVersionedPath()
    {
        const string owner = @"c:\apps\product\product.exe";
        const string original = @"c:\apps\product\1.2.0\tools\worker.exe";
        const string upgraded = @"c:\apps\product\2.0.0\tools\worker.exe";
        const string explicitlyDetached = @"c:\apps\product\2.1.0\tools\worker.exe";
        const string otherProduct = @"c:\apps\other\2.0.0\tools\worker.exe";
        var rows = new[]
        {
            new AppMetaRow(owner, owner, "Product", "auto"),
            new AppMetaRow(original, original, "Worker", "auto", MergedInto: owner),
            new AppMetaRow(upgraded, upgraded, "Worker", "auto"),
            new AppMetaRow(explicitlyDetached, explicitlyDetached, "Worker", "auto", MergeDetached: true),
            new AppMetaRow(otherProduct, otherProduct, "Worker", "auto")
        };
        var match = Assert.Single(AppCompanionRules.Match(rows.ToDictionary(row => row.AppKey)));
        Assert.Equal(upgraded, match.Key);
        Assert.Equal(owner, match.Value);
    }

    [Fact]
    public void CompanionRulesAvoidSharedAndAmbiguousPathlessProcesses()
    {
        string[] keys = [@"c:\one\game.exe", "exe:game.exe", @"c:\one\worker.exe", @"c:\two\worker.exe",
            "exe:worker.exe", @"c:\shared\node.exe", "exe:node.exe"];
        var metadata = keys.ToDictionary(key => key, key => new AppMetaRow(key, key, key, "auto"));
        var match = Assert.Single(AppCompanionRules.Match(metadata));
        Assert.Equal("exe:game.exe", match.Key);
        Assert.Equal(@"c:\one\game.exe", match.Value);
    }

    [Fact]
    public void CompanionRulesWalkUpToTheNearestProduct()
    {
        const string outer = @"c:\apps\Studio\studio.exe";
        const string inner = @"c:\apps\Studio\Exporter\app\exporter.exe";
        const string helper = @"c:\apps\Studio\Exporter\app\resources\bin\x64\worker.exe";
        const string otherHelper = @"c:\apps\Studio\resources\plugins\native\renderer.exe";
        const string sameFolder = @"c:\apps\Studio\assist.exe";
        var keys = new[] { outer, inner, helper, otherHelper, sameFolder };
        var matches = AppCompanionRules.Match(keys.ToDictionary(key => key, key => new AppMetaRow(key, key, key, "auto")));
        Assert.Equal(inner, matches[helper]);
        Assert.Equal(outer, matches[otherHelper]);
        Assert.Equal(outer, matches[sameFolder]);
        Assert.DoesNotContain(inner, matches.Keys);
        Assert.DoesNotContain(outer, matches.Keys);
    }

    [Fact]
    public void CompanionRulesStopAtSharedOrAmbiguousAncestors()
    {
        const string ambiguous = @"c:\apps\workspace\bundle\resources\tools\worker.exe";
        const string shared = @"c:\program files\unknown\resources\other.exe";
        var keys = new[] { @"c:\apps\workspace\workspace.exe", @"c:\apps\workspace\bundle\first.exe",
            @"c:\apps\workspace\bundle\second.exe", ambiguous, @"c:\program files\unrelated.exe", shared };
        var matches = AppCompanionRules.Match(keys.ToDictionary(key => key, key => new AppMetaRow(key, key, key, "auto")));
        Assert.DoesNotContain(ambiguous, matches.Keys);
        Assert.DoesNotContain(shared, matches.Keys);
    }

    [Fact]
    public void CompanionRulesRecognizeSeeinDevelopmentAndTemporaryProjectKeys()
    {
        const string project = @"c:\work\seein";
        const string sdk = @"c:\users\dev\appdata\local\jonsbopanelsdk\dotnet.exe";
        string[] projectKeys =
        [
            @"c:\work\seein\src\seein.service",
            @"c:\work\seein\tests\seein.tests",
            @"c:\users\dev\appdata\local\jonsbopanelsdk\src\seein.tray",
            @"c:\users\dev\appdata\local\temp\seeinrgbuiqa",
            @"c:\users\dev\appdata\local\temp\seeinpetmenuqa-ea7bb0c2d7984c438a543bb8a7847d70"
        ];
        const string genericSdkDirectory = @"c:\users\dev\appdata\local\jonsbopanelsdk\sdk\8.0.426";
        const string unrelatedQa = @"c:\users\dev\appdata\local\temp\otherqa";
        const string bundledPet = @"c:\work\seein\artifacts\dsh-pet.exe";
        var entries = projectKeys.Append(project).Append(genericSdkDirectory).Append(unrelatedQa)
            .ToDictionary(key => key, key => new AppMetaRow(key, sdk, key, "auto"));
        entries[bundledPet] = new AppMetaRow(bundledPet, bundledPet, "DSH", "auto", MergedInto: AppStatKeys.Background);
        var matches = AppCompanionRules.Match(entries);
        Assert.Equal(projectKeys.Length, matches.Count);
        Assert.All(projectKeys, key => Assert.Equal(project, matches[key]));
    }

    [Fact]
    public async Task TestingBucketKeepsItsOwnRowAndCanBeRemoved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            await repository.SetDisplayNameAsync("production", "同名程序");
            await repository.SetDisplayNameAsync("probe", "同名程序");
            await using var service = new AppUsageService(NullLogger<AppUsageService>.Instance, new PanelState(), repository);
            var result = await service.ApplyMetaAsync(new AppMetaRequest("merge", "probe", AppStatKeys.Testing), CancellationToken.None);
            Assert.True(result.Success);
            Assert.False(await repository.RemapAppKeyAsync("probe", "production"));
            var grouped = await service.QueryCatalogAsync(new AppCatalogRequest(), CancellationToken.None);
            Assert.Equal(2, grouped.Rows.Count);
            Assert.Equal("测试进程", Assert.Single(grouped.Rows, row => row.MergedInto == AppStatKeys.Testing).Category);
            Assert.DoesNotContain(grouped.Rows, row => row.Key == AppStatKeys.Testing);

            await service.ApplyMetaAsync(new AppMetaRequest("unmerge", "probe", null), CancellationToken.None);
            var removed = await service.QueryCatalogAsync(new AppCatalogRequest(), CancellationToken.None);
            Assert.DoesNotContain(removed.Rows, row => row.MergedInto == AppStatKeys.Testing);
            Assert.Contains(removed.Rows, row => row.Key == "probe");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExistingDatabaseGainsTheActiveSecondsColumn()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "app-usage.db");
        try
        {
            await using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await old.OpenAsync();
                var command = old.CreateCommand();
                command.CommandText = """
                    CREATE TABLE app_hour(app_key TEXT NOT NULL,hour_utc INTEGER NOT NULL,seconds REAL NOT NULL DEFAULT 0,
                        cpu_seconds REAL NOT NULL DEFAULT 0,gpu_units REAL NOT NULL DEFAULT 0,wh REAL NOT NULL DEFAULT 0,
                        PRIMARY KEY(app_key,hour_utc));
                    CREATE TABLE app_day(app_key TEXT NOT NULL,local_date TEXT NOT NULL,seconds REAL NOT NULL DEFAULT 0,
                        cpu_seconds REAL NOT NULL DEFAULT 0,wh REAL NOT NULL DEFAULT 0,cpu_wh REAL NOT NULL DEFAULT 0,
                        gpu_wh REAL NOT NULL DEFAULT 0,PRIMARY KEY(app_key,local_date));
                    CREATE TABLE app_meta(app_key TEXT PRIMARY KEY,exe_path TEXT NULL,display_name TEXT NOT NULL,source TEXT NOT NULL,
                        first_seen_utc INTEGER NOT NULL DEFAULT 0,hidden INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO app_meta(app_key,display_name,source) VALUES('legacy.exe','Legacy','manual');
                    PRAGMA user_version=1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var repository = new AppUsageRepository(path))
            {
                await repository.InitializeAsync();
                await repository.AddDayAsync(new AppDayRow("wechat", "2026-11-16", 60, 1, 0.2, 0.2, 0, 45));
                var days = await repository.GetDaysAsync("2026-11-16", "2026-11-16");
                var day = Assert.Single(days);
                Assert.Equal(45, day.ActiveSeconds, 6);
                var legacyMeta = Assert.Single(await repository.GetMetaAsync());
                Assert.Equal("legacy.exe", legacyMeta.AppKey);
                Assert.False(legacyMeta.MergeDetached);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SystemOverrideRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            await repository.UpsertMetaAsync("svc", @"C:\Windows\System32\svc.exe", "svc");
            await repository.SetSystemOverrideAsync("svc", false);
            var meta = (await repository.GetMetaAsync()).Single(row => row.AppKey == "svc");
            Assert.Equal(0, meta.SystemOverride);

            await repository.SetSystemOverrideAsync("svc", true);
            meta = (await repository.GetMetaAsync()).Single(row => row.AppKey == "svc");
            Assert.Equal(1, meta.SystemOverride);

            await repository.SetSystemOverrideAsync("svc", null);
            meta = (await repository.GetMetaAsync()).Single(row => row.AppKey == "svc");
            Assert.Null(meta.SystemOverride);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
