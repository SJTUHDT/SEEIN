using Seein.Core.Storage;
using Seein.Core;
using Microsoft.Data.Sqlite;

namespace Seein.Tests;

public sealed class EnergyRepositoryTests
{
    [Fact]
    public async Task TotalsSplitAcrossDayAndMonth()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var repository = new EnergyRepository(Path.Combine(directory, "energy.db"));
            await repository.InitializeAsync();
            var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 14)));
            await repository.AddEnergyAsync(now.AddMonths(-1), 10);
            await repository.AddEnergyAsync(now.AddDays(-1), 20);
            await repository.AddEnergyAsync(now, 30);
            var totals = await repository.GetTotalsAsync(now);
            Assert.Equal(30, totals.Today, 6);
            Assert.Equal(50, totals.Month, 6);
            Assert.Equal(60, totals.Total, 6);
            await repository.SaveSessionStateAsync("boot-1", 12.34);
            var session = await repository.GetSessionStateAsync();
            Assert.Equal("boot-1", session.BootId);
            Assert.Equal(12.34, session.SessionWh, 6);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task VersionedMigrationPreservesOldTotalAndMarksItUnclassified()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "energy.db");
        try
        {
            var now = DateTimeOffset.Now;
            var date = DateOnly.FromDateTime(now.LocalDateTime);
            await using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await old.OpenAsync();
                var command = old.CreateCommand();
                command.CommandText = "CREATE TABLE daily_energy(local_date TEXT PRIMARY KEY,local_month TEXT NOT NULL,watt_hours REAL NOT NULL); INSERT INTO daily_energy VALUES($date,$month,120);";
                command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("$month", date.ToString("yyyy-MM"));
                await command.ExecuteNonQueryAsync();
            }

            await using (var repository = new EnergyRepository(path))
            {
                await repository.InitializeAsync();
                var oldDay = await repository.GetEnergyDayAsync(date.ToString("yyyy-MM-dd"), "old", now);
                Assert.Equal(120, oldDay.TotalWh);
                Assert.Equal(120, oldDay.UnclassifiedWh);
                Assert.True(oldDay.IsLegacy);
                Assert.Null(oldDay.CpuWh);

                await repository.AddEnergyAsync(date, new EnergyDelta(.6, .1, .2, .2, .1, 0, 2, 2, 2, 2));
                var updated = await repository.GetEnergyDayAsync(date.ToString("yyyy-MM-dd"), "new", now);
                Assert.Equal(120.6, updated.TotalWh, 6);
                Assert.Equal(120, updated.UnclassifiedWh, 6);
                Assert.Equal(.1, updated.CpuWh!.Value, 6);
                Assert.Equal(.2, updated.GpuWh!.Value, 6);
                Assert.True(updated.IsLegacy);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FileMigrationPreservesWalHistoryAndLeavesLegacyDatabaseUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var legacyPath = Path.Combine(directory, "energy.db");
        var currentPath = Path.Combine(directory, "energy-v2.db");
        var date = DateOnly.FromDateTime(DateTime.Now);
        var localNoon = date.ToDateTime(new TimeOnly(12, 0));
        var minuteUtc = new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon)).ToUnixTimeSeconds() / 60 * 60;
        try
        {
            await using (var legacy = new SqliteConnection($"Data Source={legacyPath};Mode=ReadWriteCreate;Pooling=False"))
            {
                await legacy.OpenAsync();
                var schema = legacy.CreateCommand();
                schema.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE daily_energy(local_date TEXT PRIMARY KEY,local_month TEXT NOT NULL,watt_hours REAL NOT NULL);
                    CREATE TABLE minute_power(minute_utc INTEGER PRIMARY KEY,min_w REAL NOT NULL,avg_w REAL NOT NULL,max_w REAL NOT NULL,sample_count INTEGER NOT NULL);
                    CREATE TABLE state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                    INSERT INTO daily_energy VALUES('2026-10-01','2026-10',125.5);
                    INSERT INTO daily_energy VALUES($date,$month,42.25);
                    INSERT INTO state VALUES('boot_id','preserved-boot');
                    """;
                schema.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
                schema.Parameters.AddWithValue("$month", date.ToString("yyyy-MM"));
                await schema.ExecuteNonQueryAsync();

                var committed = legacy.CreateCommand();
                committed.CommandText = "INSERT INTO minute_power VALUES($minute,12,25,48,37); INSERT INTO minute_power VALUES($minute + 60,9,19,40,21)";
                committed.Parameters.AddWithValue("$minute", minuteUtc);
                await committed.ExecuteNonQueryAsync();
                Assert.True(File.Exists(legacyPath + "-wal"));

                Assert.True(await EnergyDatabaseMigrator.MigrateLegacyDatabaseIfNeededAsync(legacyPath, currentPath));

                var originalCheck = legacy.CreateCommand();
                originalCheck.CommandText = "SELECT SUM(watt_hours) FROM daily_energy";
                Assert.Equal(167.75, Convert.ToDouble(await originalCheck.ExecuteScalarAsync()), 6);
            }

            await using (var migrated = new EnergyRepository(currentPath))
            {
                await migrated.InitializeAsync();
                var oldDay = await migrated.GetEnergyDayAsync("2026-10-01", "legacy", DateTimeOffset.Now);
                Assert.Equal(125.5, oldDay.TotalWh, 6);
                Assert.Equal(125.5, oldDay.UnclassifiedWh, 6);
                Assert.Null(oldDay.CpuWh);

                var currentDay = await migrated.GetEnergyDayAsync(date.ToString("yyyy-MM-dd"), "today", DateTimeOffset.Now);
                Assert.Equal(42.25, currentDay.TotalWh, 6);
                Assert.Equal(2, currentDay.Points.Count);
                Assert.Equal(12, currentDay.Points[0].MinWatts);
                Assert.Equal(25, currentDay.Points[0].AverageWatts);
                Assert.Equal(48, currentDay.Points[0].MaxWatts);
                Assert.Equal(9, currentDay.Points[1].MinWatts);
                Assert.Equal(19, currentDay.Points[1].AverageWatts);
                Assert.Equal(40, currentDay.Points[1].MaxWatts);
            }

            Assert.True(File.Exists(legacyPath));
            Assert.False(await EnergyDatabaseMigrator.MigrateLegacyDatabaseIfNeededAsync(legacyPath, currentPath));
            await using var verifyLegacy = new SqliteConnection($"Data Source={legacyPath};Mode=ReadOnly;Pooling=False");
            await verifyLegacy.OpenAsync();
            var stillOldSchema = verifyLegacy.CreateCommand();
            stillOldSchema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('daily_energy') WHERE name='unclassified_wh'";
            Assert.Equal(0L, Convert.ToInt64(await stillOldSchema.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileMigrationMergesMissingRowsAndKeepsCurrentConflicts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var legacyPath = Path.Combine(directory, "energy.db");
        var currentPath = Path.Combine(directory, "energy-v2.db");
        var overlap = new DateOnly(2026, 10, 1);
        var legacyOnly = overlap.AddDays(1);
        var currentOnly = overlap.AddDays(2);
        var localMinute = overlap.ToDateTime(new TimeOnly(12, 0));
        var minute = new DateTimeOffset(localMinute, TimeZoneInfo.Local.GetUtcOffset(localMinute));
        try
        {
            await using (var current = new EnergyRepository(currentPath))
            {
                await current.InitializeAsync();
                await current.AddLegacyEnergyAsync(overlap, 200);
                await current.AddLegacyEnergyAsync(currentOnly, 30);
                await current.UpsertMinutePowerAsync(minute, 12, 25, 48, 37);
                await current.SaveSessionStateAsync("current-boot", 55);
            }
            await using (var legacy = new SqliteConnection($"Data Source={legacyPath};Pooling=False"))
            {
                await legacy.OpenAsync();
                var command = legacy.CreateCommand();
                command.CommandText = "CREATE TABLE daily_energy(local_date TEXT PRIMARY KEY,local_month TEXT NOT NULL,watt_hours REAL NOT NULL); CREATE TABLE minute_power(minute_utc INTEGER PRIMARY KEY,min_w REAL NOT NULL,avg_w REAL NOT NULL,max_w REAL NOT NULL,sample_count INTEGER NOT NULL); CREATE TABLE state(key TEXT PRIMARY KEY,value TEXT NOT NULL); INSERT INTO daily_energy VALUES('2026-10-01','2026-10',100); INSERT INTO daily_energy VALUES('2026-10-02','2026-10',20); INSERT INTO minute_power VALUES($minute,1,99,120,2); INSERT INTO minute_power VALUES($minute2,3,19,40,4); INSERT INTO state VALUES('boot_id','legacy-boot'); INSERT INTO state VALUES('legacy_only','kept');";
                command.Parameters.AddWithValue("$minute", minute.ToUnixTimeSeconds() / 60 * 60);
                command.Parameters.AddWithValue("$minute2", minute.ToUnixTimeSeconds() / 60 * 60 + 60);
                await command.ExecuteNonQueryAsync();
            }

            Assert.True(await EnergyDatabaseMigrator.MigrateLegacyDatabaseIfNeededAsync(legacyPath, currentPath));
            await using (var merged = new EnergyRepository(currentPath))
            {
                await merged.InitializeAsync();
                var overlapDay = await merged.GetEnergyDayAsync("2026-10-01", "overlap", DateTimeOffset.Now);
                var legacyDay = await merged.GetEnergyDayAsync("2026-10-02", "legacy", DateTimeOffset.Now);
                var currentDay = await merged.GetEnergyDayAsync("2026-10-03", "current", DateTimeOffset.Now);
                Assert.Equal(200, overlapDay.TotalWh);
                Assert.Equal(20, legacyDay.TotalWh);
                Assert.Equal(30, currentDay.TotalWh);
                Assert.Equal(2, overlapDay.Points.Count);
                Assert.Equal(25, overlapDay.Points[0].AverageWatts);
                Assert.Equal(19, overlapDay.Points[1].AverageWatts);
                var session = await merged.GetSessionStateAsync();
                Assert.Equal("current-boot", session.BootId);
            }

            Assert.True(File.Exists(legacyPath));
            Assert.False(await EnergyDatabaseMigrator.MigrateLegacyDatabaseIfNeededAsync(legacyPath, currentPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FailedFileMigrationLeavesLegacyDatabaseInPlace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var legacyPath = Path.Combine(directory, "energy.db");
        var currentPath = Path.Combine(directory, "energy-v2.db");
        try
        {
            await File.WriteAllTextAsync(legacyPath, "not a SQLite database");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                EnergyDatabaseMigrator.MigrateLegacyDatabaseIfNeededAsync(legacyPath, currentPath));
            Assert.True(File.Exists(legacyPath));
            Assert.False(File.Exists(currentPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DayCurveUsesLocalDateAndMinuteHistoryExpiresAfter365Days()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "energy.db");
        try
        {
            var now = DateTimeOffset.Now;
            var date = DateOnly.FromDateTime(now.LocalDateTime);
            await using (var repository = new EnergyRepository(path))
            {
                await repository.InitializeAsync();
                var (start, _) = EnergyRepository.GetLocalDayUtcRange(date, TimeZoneInfo.Local);
                await repository.UpsertMinutePowerAsync(start.AddHours(2), 20, 30, 45, 40);
                var oldPoint = now.AddDays(-366);
                await repository.UpsertMinutePowerAsync(oldPoint, 10, 20, 30, 40);
                var result = await repository.GetEnergyDayAsync(date.ToString("yyyy-MM-dd"), "curve", now);
                Assert.Single(result.Points);
                Assert.Equal(30, result.Points[0].AverageWatts);
                await repository.PruneMinuteHistoryAsync(now);
            }

            await using (var verify = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await verify.OpenAsync();
                var count = await new SqliteCommand("SELECT COUNT(*) FROM minute_power", verify).ExecuteScalarAsync();
                Assert.Equal(1L, Convert.ToInt64(count));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LocalDayRangeHandlesSpringAndFallDstBoundaries()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var spring = EnergyRepository.GetLocalDayUtcRange(new DateOnly(2026, 3, 8), pacific);
        var fall = EnergyRepository.GetLocalDayUtcRange(new DateOnly(2026, 11, 1), pacific);
        Assert.Equal(TimeSpan.FromHours(23), spring.EndUtc - spring.StartUtc);
        Assert.Equal(TimeSpan.FromHours(25), fall.EndUtc - fall.StartUtc);
        Assert.Equal(new DateOnly(2026, 3, 8), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(spring.StartUtc, pacific).DateTime));
        Assert.Equal(new DateOnly(2026, 11, 2), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fall.EndUtc, pacific).DateTime));
    }
}
