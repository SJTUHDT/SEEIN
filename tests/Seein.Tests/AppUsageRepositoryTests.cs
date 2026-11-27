using Seein.Core;
using Seein.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Seein.Tests;

public sealed class AppUsageRepositoryTests
{
    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task SpansAreUpsertedAndQueriedByRange()
    {
        var directory = CreateDirectory();
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();

            await repository.UpsertSpanAsync("wechat", 1000, 1060, 1.5, 0);
            await repository.UpsertSpanAsync("wechat", 1000, 1120, 2.5, 0);
            await repository.UpsertSpanAsync("qq", 2000, 2100, 0.5, 0);

            var day = await repository.GetEventsAsync(1000, 1200);
            var span = Assert.Single(day);
            Assert.Equal("wechat", span.AppKey);
            Assert.Equal(1120, span.EndUtc);
            Assert.Equal(2.5, span.CpuSeconds, 6);

            var all = await repository.GetEventsAsync(0, 3000);
            Assert.Equal(2, all.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HourAndDayBucketsAccumulateAcrossFlushes()
    {
        var directory = CreateDirectory();
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();

            await repository.AddHourAsync(new AppHourRow("wechat", 3600, 30, 0.4, 0, 0.1));
            await repository.AddHourAsync(new AppHourRow("wechat", 3600, 30, 0.6, 0, 0.2));
            await repository.AddDayAsync(new AppDayRow("wechat", "2026-11-16", 30, 0.4, 0.1, 0.1, 0));
            await repository.AddDayAsync(new AppDayRow("wechat", "2026-11-16", 30, 0.6, 0.2, 0.2, 0));

            var days = await repository.GetDaysAsync("2026-11-16", "2026-11-16");
            var day = Assert.Single(days);
            Assert.Equal(60, day.Seconds, 6);
            Assert.Equal(0.3, day.Wh, 6);
            Assert.Equal(0.3, day.CpuWh, 6);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GapIsRecordedAndReturnedForTheDay()
    {
        var directory = CreateDirectory();
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            await repository.RecordGapAsync(1000, 1600, "sleep");

            var gaps = await repository.GetGapsAsync(900, 2000);
            var gap = Assert.Single(gaps);
            Assert.Equal("sleep", gap.Reason);
            Assert.Equal(1000, gap.StartUtc);
            Assert.Equal(1600, gap.EndUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MetadataUpsertKeepsTheFirstPathAndName()
    {
        var directory = CreateDirectory();
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            await repository.UpsertMetaAsync("wechat", @"C:\Program Files\Tencent\Weixin.exe", "微信");
            await repository.UpsertMetaAsync("wechat", null, "微信");

            var meta = await repository.GetMetaAsync();
            var row = Assert.Single(meta);
            Assert.Equal("微信", row.DisplayName);
            Assert.Equal(@"C:\Program Files\Tencent\Weixin.exe", row.ExePath);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PruneRemovesIntervalsOlderThanNinetyDays()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "app-usage.db");
            await using (var repository = new AppUsageRepository(path))
            {
                await repository.InitializeAsync();
                var now = DateTimeOffset.UtcNow;
                var old = now.AddDays(-120).ToUnixTimeSeconds();
                var recent = now.AddDays(-1).ToUnixTimeSeconds();
                await repository.UpsertSpanAsync("old", old, old + 60, 0, 0);
                await repository.UpsertSpanAsync("recent", recent, recent + 60, 0, 0);
                await repository.PruneAsync(now);
            }

            await using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
            await verify.OpenAsync();
            var count = await new SqliteCommand("SELECT COUNT(*) FROM app_event", verify).ExecuteScalarAsync();
            Assert.Equal(1L, Convert.ToInt64(count));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LastEndIsReportedSoOfflineGapsCanBeReconstructed()
    {
        var directory = CreateDirectory();
        try
        {
            await using var repository = new AppUsageRepository(Path.Combine(directory, "app-usage.db"));
            await repository.InitializeAsync();
            Assert.Null(await repository.GetLastEndUtcAsync());

            await repository.UpsertSpanAsync("qq", 500, 900, 0, 0);
            Assert.Equal(900, await repository.GetLastEndUtcAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void DateFormattingIsInvariant()
    {
        Assert.Equal("2026-11-06", AppUsageRepository.FormatDate(new DateOnly(2026, 11, 6)));
    }
}
