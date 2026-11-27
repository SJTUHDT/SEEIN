using Microsoft.Data.Sqlite;

namespace Seein.Core.Storage;

public static class EnergyDatabaseMigrator
{
    private const string MergeMarkerKey = "legacy_merge_identity";

    public static async Task<bool> MigrateLegacyDatabaseIfNeededAsync(
        string legacyPath,
        string currentPath,
        CancellationToken token = default)
    {
        legacyPath = Path.GetFullPath(legacyPath);
        currentPath = Path.GetFullPath(currentPath);
        if (string.Equals(legacyPath, currentPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Legacy and current database paths must differ.", nameof(currentPath));
        if (!File.Exists(legacyPath)) return false;
        if (File.Exists(currentPath))
            return await MergeLegacyIntoCurrentAsync(legacyPath, currentPath, token).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(currentPath)!;
        Directory.CreateDirectory(directory);
        var stagingBase = currentPath + "." + Guid.NewGuid().ToString("N");
        var migratedPath = stagingBase + ".migrating";
        var readyPath = stagingBase + ".ready";

        await BackupDatabaseAsync(legacyPath, migratedPath, token).ConfigureAwait(false);
        var originalSnapshot = await ReadSnapshotAsync(migratedPath, token).ConfigureAwait(false);

        await using (var repository = new EnergyRepository(migratedPath))
            await repository.InitializeAsync(token).ConfigureAwait(false);

        var migratedSnapshot = await ReadSnapshotAsync(migratedPath, token).ConfigureAwait(false);
        EnsurePreserved(originalSnapshot, migratedSnapshot, migratedPath);
        if (migratedSnapshot.SchemaVersion < 1)
            throw new InvalidDataException("The migrated energy database schema was not upgraded.");

        await BackupDatabaseAsync(migratedPath, readyPath, token).ConfigureAwait(false);
        var readySnapshot = await ReadSnapshotAsync(readyPath, token).ConfigureAwait(false);
        EnsurePreserved(migratedSnapshot, readySnapshot, readyPath);
        await SetMergeMarkerAsync(readyPath, await GetLegacyFingerprintAsync(legacyPath, token).ConfigureAwait(false), token).ConfigureAwait(false);
        _ = await ReadSnapshotAsync(readyPath, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        try
        {
            File.Move(readyPath, currentPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(currentPath))
        {
            DeleteGeneratedDatabaseFiles(readyPath);
            DeleteGeneratedDatabaseFiles(migratedPath);
            return false;
        }

        DeleteGeneratedDatabaseFiles(migratedPath);
        return true;
    }

    private static async Task<bool> MergeLegacyIntoCurrentAsync(string legacyPath, string currentPath, CancellationToken token)
    {
        var fingerprint = await GetLegacyFingerprintAsync(legacyPath, token).ConfigureAwait(false);
        if (await HasMergeMarkerAsync(currentPath, fingerprint, token).ConfigureAwait(false)) return false;

        var stagingBase = currentPath + "." + Guid.NewGuid().ToString("N");
        var mergedPath = stagingBase + ".merging";
        var incomingPath = stagingBase + ".legacy";
        var readyPath = stagingBase + ".ready";
        try
        {
            await BackupDatabaseAsync(currentPath, mergedPath, token).ConfigureAwait(false);
            var currentBefore = await ReadSnapshotAsync(mergedPath, token).ConfigureAwait(false);
            await using (var repository = new EnergyRepository(mergedPath))
                await repository.InitializeAsync(token).ConfigureAwait(false);
            var currentMigrated = await ReadSnapshotAsync(mergedPath, token).ConfigureAwait(false);
            EnsurePreserved(currentBefore, currentMigrated, mergedPath);

            await BackupDatabaseAsync(legacyPath, incomingPath, token).ConfigureAwait(false);
            var legacyBefore = await ReadSnapshotAsync(incomingPath, token).ConfigureAwait(false);
            await using (var repository = new EnergyRepository(incomingPath))
                await repository.InitializeAsync(token).ConfigureAwait(false);
            var legacyMigrated = await ReadSnapshotAsync(incomingPath, token).ConfigureAwait(false);
            EnsurePreserved(legacyBefore, legacyMigrated, incomingPath);

            await MergeMissingRowsAsync(mergedPath, incomingPath, fingerprint, token).ConfigureAwait(false);
            var mergedSnapshot = await ReadSnapshotAsync(mergedPath, token).ConfigureAwait(false);
            EnsureMergePreserved(currentMigrated, mergedSnapshot, mergedPath);
            await BackupDatabaseAsync(mergedPath, readyPath, token).ConfigureAwait(false);
            var readySnapshot = await ReadSnapshotAsync(readyPath, token).ConfigureAwait(false);
            EnsurePreserved(mergedSnapshot, readySnapshot, readyPath);
            token.ThrowIfCancellationRequested();
            File.Move(readyPath, currentPath, overwrite: true);
            return true;
        }
        finally
        {
            DeleteGeneratedDatabaseFiles(readyPath);
            DeleteGeneratedDatabaseFiles(incomingPath);
            DeleteGeneratedDatabaseFiles(mergedPath);
        }
    }

    private static async Task<string> GetLegacyFingerprintAsync(string path, CancellationToken token)
    {
        var snapshot = await ReadSnapshotAsync(path, token).ConfigureAwait(false);
        return string.Join("|", snapshot.SchemaVersion, snapshot.DailyCount,
            snapshot.TotalWh.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            snapshot.CurveCount, snapshot.CurveSampleCount.ToString("R", System.Globalization.CultureInfo.InvariantCulture), snapshot.StateCount);
    }

    private static async Task SetMergeMarkerAsync(string path, string fingerprint, CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Cache=Private;Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", MergeMarkerKey);
        command.Parameters.AddWithValue("$value", fingerprint);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<bool> HasMergeMarkerAsync(string path, string fingerprint, CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM state WHERE key=$key";
        command.Parameters.AddWithValue("$key", MergeMarkerKey);
        try
        {
            return string.Equals(Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false)),
                fingerprint, StringComparison.Ordinal);
        }
        catch (SqliteException) { return false; }
    }

    private static async Task MergeMissingRowsAsync(string currentPath, string legacyPath, string fingerprint, CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={currentPath};Cache=Private;Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);
        var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $path AS legacy";
        attach.Parameters.AddWithValue("$path", legacyPath);
        await attach.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        var merge = connection.CreateCommand();
        merge.Transaction = (SqliteTransaction)transaction;
        merge.CommandText = "INSERT INTO main.daily_energy(local_date,local_month,watt_hours,cpu_wh,gpu_wh,other_wh,loss_wh,unclassified_wh,covered_seconds,classified_seconds,cpu_covered_seconds,gpu_covered_seconds) SELECT local_date,local_month,watt_hours,cpu_wh,gpu_wh,other_wh,loss_wh,unclassified_wh,covered_seconds,classified_seconds,cpu_covered_seconds,gpu_covered_seconds FROM legacy.daily_energy WHERE 1 ON CONFLICT(local_date) DO NOTHING; INSERT INTO main.minute_power(minute_utc,min_w,avg_w,max_w,sample_count) SELECT minute_utc,min_w,avg_w,max_w,sample_count FROM legacy.minute_power WHERE 1 ON CONFLICT(minute_utc) DO NOTHING; INSERT INTO main.state(key,value) SELECT key,value FROM legacy.state WHERE 1 ON CONFLICT(key) DO NOTHING; INSERT INTO main.state(key,value) VALUES($marker,$fingerprint) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        merge.Parameters.AddWithValue("$marker", MergeMarkerKey);
        merge.Parameters.AddWithValue("$fingerprint", fingerprint);
        await merge.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static void EnsureMergePreserved(DatabaseSnapshot before, DatabaseSnapshot after, string path)
    {
        if (after.DailyCount < before.DailyCount || after.TotalWh + 1e-7 < before.TotalWh ||
            after.CurveCount < before.CurveCount || after.CurveSampleCount < before.CurveSampleCount ||
            after.StateCount < before.StateCount)
            throw new InvalidDataException($"Legacy merge reduced current data in '{path}'.");
    }

    private static async Task BackupDatabaseAsync(string sourcePath, string destinationPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Cache=Private;Pooling=False");
        await using var destination = new SqliteConnection($"Data Source={destinationPath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
        await source.OpenAsync(token).ConfigureAwait(false);
        await destination.OpenAsync(token).ConfigureAwait(false);
        source.BackupDatabase(destination);
        token.ThrowIfCancellationRequested();
    }

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(string path, CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);

        var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        var integrityResult = Convert.ToString(await integrity.ExecuteScalarAsync(token).ConfigureAwait(false));
        if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite integrity check failed for '{path}': {integrityResult}");

        var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(await version.ExecuteScalarAsync(token).ConfigureAwait(false));

        var daily = await ReadTableSnapshotAsync(connection, "daily_energy", "watt_hours", token).ConfigureAwait(false);
        var curve = await ReadTableSnapshotAsync(connection, "minute_power", "sample_count", token).ConfigureAwait(false);
        var state = await ReadTableSnapshotAsync(connection, "state", null, token).ConfigureAwait(false);
        return new DatabaseSnapshot(schemaVersion, daily.Count, daily.Sum, curve.Count, curve.Sum, state.Count);
    }

    private static async Task<(long Count, double Sum)> ReadTableSnapshotAsync(
        SqliteConnection connection,
        string table,
        string? sumColumn,
        CancellationToken token)
    {
        var exists = connection.CreateCommand();
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        exists.Parameters.AddWithValue("$name", table);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(token).ConfigureAwait(false)) == 0) return (0, 0);

        var command = connection.CreateCommand();
        command.CommandText = sumColumn is null
            ? $"SELECT COUNT(*),0 FROM {table}"
            : $"SELECT COUNT(*),COALESCE(SUM({sumColumn}),0) FROM {table}";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await reader.ReadAsync(token).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetDouble(1));
    }

    private static void EnsurePreserved(DatabaseSnapshot before, DatabaseSnapshot after, string path)
    {
        if (before.DailyCount != after.DailyCount || !NearlyEqual(before.TotalWh, after.TotalWh) ||
            before.CurveCount != after.CurveCount || !NearlyEqual(before.CurveSampleCount, after.CurveSampleCount) ||
            before.StateCount != after.StateCount)
        {
            throw new InvalidDataException(
                $"Energy data verification failed for '{path}'. Daily {before.DailyCount}/{before.TotalWh} -> " +
                $"{after.DailyCount}/{after.TotalWh}; curve {before.CurveCount}/{before.CurveSampleCount} -> " +
                $"{after.CurveCount}/{after.CurveSampleCount}; state {before.StateCount} -> {after.StateCount}.");
        }
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= Math.Max(1e-7, Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-10);

    private static void DeleteGeneratedDatabaseFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private sealed record DatabaseSnapshot(
        int SchemaVersion,
        long DailyCount,
        double TotalWh,
        long CurveCount,
        double CurveSampleCount,
        long StateCount);
}
