using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Seein.Core.Storage;

public sealed record AppEventRow(string AppKey, long StartUtc, long EndUtc, double CpuSeconds, double GpuUnits);

public sealed record AppHourRow(string AppKey, long HourUtc, double Seconds, double CpuSeconds, double GpuUnits, double Wh, double ActiveSeconds = 0);

public sealed record AppDayRow(string AppKey, string LocalDate, double Seconds, double CpuSeconds, double Wh, double CpuWh, double GpuWh, double ActiveSeconds = 0,
    double GpuUnits = 0, long NetUpBytes = 0, long NetDownBytes = 0,
    double CpuPeak = 0, double GpuPeak = 0, double CpuMin = 0, double GpuMin = 0);

public sealed record AppMetaRow(string AppKey, string? ExePath, string DisplayName, string Source, bool Hidden = false,
    string? MergedInto = null, string? Color = null, string? IconPath = null, bool MergeDetached = false,
    int? SystemOverride = null);

public sealed record AppGapRow(long StartUtc, long EndUtc, string Reason);

/// <summary>
/// Storage for per-application runtime and power.  Deliberately kept in its own database so the
/// existing energy history file needs no schema migration.  The event table follows the same idea
/// as ActivityWatch: a run is an interval that gets extended while heartbeats keep arriving, so a
/// crash or restart never leaves a half-written session behind.
/// </summary>
public sealed class AppUsageRepository : IAsyncDisposable
{
    private const int SchemaVersion = 4;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppUsageRepository(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connection = new SqliteConnection($"Data Source={databasePath};Cache=Shared;Mode=ReadWriteCreate;Pooling=False");
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _connection.OpenAsync(token).ConfigureAwait(false);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS app_event (
                    app_key TEXT NOT NULL,
                    start_utc INTEGER NOT NULL,
                    end_utc INTEGER NOT NULL,
                    cpu_seconds REAL NOT NULL DEFAULT 0,
                    gpu_units REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY(app_key, start_utc)
                );
                CREATE INDEX IF NOT EXISTS ix_app_event_end ON app_event(end_utc);
                CREATE TABLE IF NOT EXISTS app_hour (
                    app_key TEXT NOT NULL,
                    hour_utc INTEGER NOT NULL,
                    seconds REAL NOT NULL DEFAULT 0,
                    cpu_seconds REAL NOT NULL DEFAULT 0,
                    gpu_units REAL NOT NULL DEFAULT 0,
                    wh REAL NOT NULL DEFAULT 0,
                    active_seconds REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY(app_key, hour_utc)
                );
                CREATE TABLE IF NOT EXISTS app_day (
                    app_key TEXT NOT NULL,
                    local_date TEXT NOT NULL,
                    seconds REAL NOT NULL DEFAULT 0,
                    cpu_seconds REAL NOT NULL DEFAULT 0,
                    wh REAL NOT NULL DEFAULT 0,
                    cpu_wh REAL NOT NULL DEFAULT 0,
                    gpu_wh REAL NOT NULL DEFAULT 0,
                    active_seconds REAL NOT NULL DEFAULT 0,
                    gpu_units REAL NOT NULL DEFAULT 0,
                    net_up_bytes INTEGER NOT NULL DEFAULT 0,
                    net_down_bytes INTEGER NOT NULL DEFAULT 0,
                    cpu_peak REAL NOT NULL DEFAULT 0,
                    gpu_peak REAL NOT NULL DEFAULT 0,
                    cpu_min REAL NOT NULL DEFAULT 0,
                    gpu_min REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY(app_key, local_date)
                );
                CREATE INDEX IF NOT EXISTS ix_app_day_date ON app_day(local_date);
                CREATE TABLE IF NOT EXISTS machine_day (
                    local_date TEXT PRIMARY KEY,
                    cpu_peak REAL NOT NULL DEFAULT 0,
                    gpu_peak REAL NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS app_meta (
                    app_key TEXT PRIMARY KEY,
                    exe_path TEXT NULL,
                    display_name TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'auto',
                    first_seen_utc INTEGER NOT NULL DEFAULT 0,
                    hidden INTEGER NOT NULL DEFAULT 0,
                    merged_into TEXT NULL,
                    color TEXT NULL,
                    icon_path TEXT NULL,
                    system_override INTEGER NULL,
                    merge_detached INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS coverage (
                    start_utc INTEGER NOT NULL,
                    end_utc INTEGER NOT NULL,
                    reason TEXT NOT NULL,
                    PRIMARY KEY(start_utc, reason)
                );
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await MigrateAsync(token).ConfigureAwait(false);
        }
        catch
        {
            try { await _connection.CloseAsync().ConfigureAwait(false); }
            catch { }
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task MigrateAsync(CancellationToken token)
    {
        using var versionCommand = _connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var transaction = await _connection.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (var (table, column, definition) in new[]
                 {
                     ("app_hour", "active_seconds", "REAL NOT NULL DEFAULT 0"),
                     ("app_day", "active_seconds", "REAL NOT NULL DEFAULT 0"),
                ("app_day", "gpu_units", "REAL NOT NULL DEFAULT 0"),
                ("app_day", "net_up_bytes", "INTEGER NOT NULL DEFAULT 0"),
                ("app_day", "net_down_bytes", "INTEGER NOT NULL DEFAULT 0"),
                ("app_day", "cpu_peak", "REAL NOT NULL DEFAULT 0"),
            ("app_day", "cpu_min", "REAL NOT NULL DEFAULT 0"),
            ("app_day", "gpu_min", "REAL NOT NULL DEFAULT 0"),
                ("app_day", "gpu_peak", "REAL NOT NULL DEFAULT 0"),
                     ("app_meta", "merged_into", "TEXT NULL"),
                     ("app_meta", "color", "TEXT NULL"),
                     ("app_meta", "icon_path", "TEXT NULL"),
                     ("app_meta", "system_override", "INTEGER NULL"),
                     ("app_meta", "merge_detached", "INTEGER NOT NULL DEFAULT 0")
                 })
        {
            using var columns = _connection.CreateCommand();
            columns.Transaction = (SqliteTransaction)transaction;
            columns.CommandText = $"PRAGMA table_info({table})";
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await columns.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) names.Add(reader.GetString(1));
            if (names.Contains(column)) continue;
            using var alter = _connection.CreateCommand();
            alter.Transaction = (SqliteTransaction)transaction;
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            await alter.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        // v3: early builds integrated application power over one second instead of the sample window,
        // so every application row is about five times too small (the tick interval).  Scale the history
        // once; the system and baseline buckets were always correct.
        if (version < 3)
        {
            using var scale = _connection.CreateCommand();
            scale.Transaction = (SqliteTransaction)transaction;
            scale.CommandText = """
                UPDATE app_day SET wh=wh*5, cpu_wh=cpu_wh*5, gpu_wh=gpu_wh*5
                    WHERE app_key NOT IN ('__system__','__baseline__');
                UPDATE app_hour SET wh=wh*5
                    WHERE app_key NOT IN ('__system__','__baseline__');
                """;
            await scale.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        using var stamp = _connection.CreateCommand();
        stamp.Transaction = (SqliteTransaction)transaction;
        stamp.CommandText = $"PRAGMA user_version={SchemaVersion}";
        await stamp.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task UpsertSpanAsync(string appKey, long startUtc, long endUtc, double cpuSeconds, double gpuUnits,
        CancellationToken token = default)
    {
        if (endUtc <= startUtc) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_event(app_key,start_utc,end_utc,cpu_seconds,gpu_units)
                VALUES($key,$start,$end,$cpu,$gpu)
                ON CONFLICT(app_key,start_utc) DO UPDATE SET
                    end_utc=MAX(end_utc,excluded.end_utc),
                    cpu_seconds=MAX(cpu_seconds,excluded.cpu_seconds),
                    gpu_units=MAX(gpu_units,excluded.gpu_units);
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$start", startUtc);
            command.Parameters.AddWithValue("$end", endUtc);
            command.Parameters.AddWithValue("$cpu", cpuSeconds);
            command.Parameters.AddWithValue("$gpu", gpuUnits);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AppEventRow>> GetEventsAsync(long fromUtc, long toUtc, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT app_key,start_utc,end_utc,cpu_seconds,gpu_units FROM app_event
                WHERE end_utc>$from AND start_utc<$to ORDER BY app_key,start_utc;
                """;
            command.Parameters.AddWithValue("$from", fromUtc);
            command.Parameters.AddWithValue("$to", toUtc);
            var rows = new List<AppEventRow>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                rows.Add(new AppEventRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetDouble(4)));
            return rows;
        }
        finally { _gate.Release(); }
    }

    public async Task AddHourAsync(AppHourRow row, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await AddHourCoreAsync(row, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task AddHoursAsync(IReadOnlyList<AppHourRow> rows, CancellationToken token = default)
    {
        if (rows.Count == 0) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(token).ConfigureAwait(false);
            foreach (var row in rows) await AddHourCoreAsync(row, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task AddHourCoreAsync(AppHourRow row, CancellationToken token)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_hour(app_key,hour_utc,seconds,cpu_seconds,gpu_units,wh,active_seconds)
            VALUES($key,$hour,$seconds,$cpu,$gpu,$wh,$active)
            ON CONFLICT(app_key,hour_utc) DO UPDATE SET
                seconds=app_hour.seconds+excluded.seconds,
                cpu_seconds=app_hour.cpu_seconds+excluded.cpu_seconds,
                gpu_units=app_hour.gpu_units+excluded.gpu_units,
                wh=app_hour.wh+excluded.wh,
                active_seconds=app_hour.active_seconds+excluded.active_seconds;
            """;
        command.Parameters.AddWithValue("$key", row.AppKey);
        command.Parameters.AddWithValue("$hour", row.HourUtc);
        command.Parameters.AddWithValue("$seconds", row.Seconds);
        command.Parameters.AddWithValue("$cpu", row.CpuSeconds);
        command.Parameters.AddWithValue("$gpu", row.GpuUnits);
        command.Parameters.AddWithValue("$wh", row.Wh);
        command.Parameters.AddWithValue("$active", row.ActiveSeconds);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async Task AddDayAsync(AppDayRow row, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await AddDayCoreAsync(row, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task AddDayCoreAsync(AppDayRow row, CancellationToken token)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_day(app_key,local_date,seconds,cpu_seconds,wh,cpu_wh,gpu_wh,active_seconds,gpu_units,net_up_bytes,net_down_bytes,cpu_peak,gpu_peak,cpu_min,gpu_min)
            VALUES($key,$date,$seconds,$cpu,$wh,$cpuWh,$gpuWh,$active,$gpuUnits,$netUp,$netDown,$cpuPeak,$gpuPeak,$cpuMin,$gpuMin)
            ON CONFLICT(app_key,local_date) DO UPDATE SET
                seconds=app_day.seconds+excluded.seconds,
                cpu_seconds=app_day.cpu_seconds+excluded.cpu_seconds,
                wh=app_day.wh+excluded.wh,
                cpu_wh=app_day.cpu_wh+excluded.cpu_wh,
                gpu_wh=app_day.gpu_wh+excluded.gpu_wh,
                active_seconds=app_day.active_seconds+excluded.active_seconds,
                gpu_units=app_day.gpu_units+excluded.gpu_units,
                net_up_bytes=app_day.net_up_bytes+excluded.net_up_bytes,
                net_down_bytes=app_day.net_down_bytes+excluded.net_down_bytes,
                cpu_peak=MAX(app_day.cpu_peak,excluded.cpu_peak),
                gpu_peak=MAX(app_day.gpu_peak,excluded.gpu_peak),
                cpu_min=CASE WHEN app_day.cpu_min<=0 THEN excluded.cpu_min
                    WHEN excluded.cpu_min<=0 THEN app_day.cpu_min
                    ELSE MIN(app_day.cpu_min,excluded.cpu_min) END,
                gpu_min=CASE WHEN app_day.gpu_min<=0 THEN excluded.gpu_min
                    WHEN excluded.gpu_min<=0 THEN app_day.gpu_min
                    ELSE MIN(app_day.gpu_min,excluded.gpu_min) END;
            """;
        command.Parameters.AddWithValue("$key", row.AppKey);
        command.Parameters.AddWithValue("$date", row.LocalDate);
        command.Parameters.AddWithValue("$seconds", row.Seconds);
        command.Parameters.AddWithValue("$cpu", row.CpuSeconds);
        command.Parameters.AddWithValue("$wh", row.Wh);
        command.Parameters.AddWithValue("$cpuWh", row.CpuWh);
        command.Parameters.AddWithValue("$gpuWh", row.GpuWh);
        command.Parameters.AddWithValue("$active", row.ActiveSeconds);
        command.Parameters.AddWithValue("$gpuUnits", row.GpuUnits);
        command.Parameters.AddWithValue("$netUp", row.NetUpBytes);
        command.Parameters.AddWithValue("$netDown", row.NetDownBytes);
        command.Parameters.AddWithValue("$cpuPeak", row.CpuPeak);
        command.Parameters.AddWithValue("$gpuPeak", row.GpuPeak);
        command.Parameters.AddWithValue("$cpuMin", row.CpuMin);
        command.Parameters.AddWithValue("$gpuMin", row.GpuMin);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>Highest single-sample utilization the whole machine reached on that day.</summary>
    public async Task UpsertMachinePeakAsync(string localDate, double cpuPeak, double gpuPeak, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO machine_day(local_date,cpu_peak,gpu_peak) VALUES($date,$cpu,$gpu)
                ON CONFLICT(local_date) DO UPDATE SET
                    cpu_peak=MAX(machine_day.cpu_peak,excluded.cpu_peak),
                    gpu_peak=MAX(machine_day.gpu_peak,excluded.gpu_peak);
                """;
            command.Parameters.AddWithValue("$date", localDate);
            command.Parameters.AddWithValue("$cpu", cpuPeak);
            command.Parameters.AddWithValue("$gpu", gpuPeak);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<(double CpuPeak, double GpuPeak)> GetMachinePeakAsync(string localDate, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT cpu_peak,gpu_peak FROM machine_day WHERE local_date=$date";
            command.Parameters.AddWithValue("$date", localDate);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                return (reader.GetDouble(0), reader.GetDouble(1));
            return (0, 0);
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<AppDayRow>> GetDaysAsync(string fromDate, string toDate, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT app_key,local_date,seconds,cpu_seconds,wh,cpu_wh,gpu_wh,active_seconds,gpu_units,net_up_bytes,net_down_bytes,cpu_peak,gpu_peak,cpu_min,gpu_min FROM app_day
                WHERE local_date>=$from AND local_date<=$to ORDER BY local_date,app_key;
                """;
            command.Parameters.AddWithValue("$from", fromDate);
            command.Parameters.AddWithValue("$to", toDate);
            var rows = new List<AppDayRow>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                rows.Add(new AppDayRow(reader.GetString(0), reader.GetString(1), reader.GetDouble(2),
                    reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6),
                    reader.GetDouble(7), reader.GetDouble(8), reader.GetInt64(9), reader.GetInt64(10),
                    reader.GetDouble(11), reader.GetDouble(12)));
            return rows;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Folds everything recorded under a runtime-host key (for example electron.exe or TinyDL64.exe)
    /// into the real application the processes belong to.
    /// </summary>
    public async Task<bool> RemapAppKeyAsync(string from, string to, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return false;
        if (string.Equals(from, to, StringComparison.Ordinal)) return false;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(token).ConfigureAwait(false);

            using var manual = _connection.CreateCommand();
            manual.Transaction = (SqliteTransaction)transaction;
            manual.CommandText = """
                SELECT 1 FROM app_meta
                WHERE (app_key=$from AND (merged_into IS NOT NULL OR merge_detached=1 OR system_override IS NOT NULL))
                   OR merged_into=$from
                LIMIT 1;
                """;
            manual.Parameters.AddWithValue("$from", from);
            if (await manual.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) return false;

            using var events = _connection.CreateCommand();
            events.Transaction = (SqliteTransaction)transaction;
            events.CommandText = """
                INSERT INTO app_event(app_key,start_utc,end_utc,cpu_seconds,gpu_units)
                SELECT $to,start_utc,end_utc,cpu_seconds,gpu_units FROM app_event WHERE app_key=$from
                ON CONFLICT(app_key,start_utc) DO UPDATE SET
                    end_utc=MAX(end_utc,excluded.end_utc),
                    cpu_seconds=MAX(cpu_seconds,excluded.cpu_seconds),
                    gpu_units=MAX(gpu_units,excluded.gpu_units);
                DELETE FROM app_event WHERE app_key=$from;
                """;
            events.Parameters.AddWithValue("$from", from);
            events.Parameters.AddWithValue("$to", to);
            await events.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            using var hours = _connection.CreateCommand();
            hours.Transaction = (SqliteTransaction)transaction;
            hours.CommandText = """
                INSERT INTO app_hour(app_key,hour_utc,seconds,cpu_seconds,gpu_units,wh)
                SELECT $to,hour_utc,seconds,cpu_seconds,gpu_units,wh FROM app_hour WHERE app_key=$from
                ON CONFLICT(app_key,hour_utc) DO UPDATE SET
                    seconds=app_hour.seconds+excluded.seconds,
                    cpu_seconds=app_hour.cpu_seconds+excluded.cpu_seconds,
                    gpu_units=app_hour.gpu_units+excluded.gpu_units,
                    wh=app_hour.wh+excluded.wh;
                DELETE FROM app_hour WHERE app_key=$from;
                """;
            hours.Parameters.AddWithValue("$from", from);
            hours.Parameters.AddWithValue("$to", to);
            await hours.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            using var days = _connection.CreateCommand();
            days.Transaction = (SqliteTransaction)transaction;
            days.CommandText = """
                INSERT INTO app_day(app_key,local_date,seconds,cpu_seconds,wh,cpu_wh,gpu_wh)
                SELECT $to,local_date,seconds,cpu_seconds,wh,cpu_wh,gpu_wh FROM app_day WHERE app_key=$from
                ON CONFLICT(app_key,local_date) DO UPDATE SET
                    seconds=app_day.seconds+excluded.seconds,
                    cpu_seconds=app_day.cpu_seconds+excluded.cpu_seconds,
                    wh=app_day.wh+excluded.wh,
                    cpu_wh=app_day.cpu_wh+excluded.cpu_wh,
                    gpu_wh=app_day.gpu_wh+excluded.gpu_wh;
                DELETE FROM app_day WHERE app_key=$from;
                DELETE FROM app_meta WHERE app_key=$from;
                """;
            days.Parameters.AddWithValue("$from", from);
            days.Parameters.AddWithValue("$to", to);
            await days.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<long?> GetLastEndUtcAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT MAX(end_utc) FROM app_event";
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        finally { _gate.Release(); }
    }

    public async Task RecordGapAsync(long startUtc, long endUtc, string reason, CancellationToken token = default)
    {
        if (endUtc <= startUtc) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO coverage(start_utc,end_utc,reason) VALUES($start,$end,$reason)";
            command.Parameters.AddWithValue("$start", startUtc);
            command.Parameters.AddWithValue("$end", endUtc);
            command.Parameters.AddWithValue("$reason", reason);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AppGapRow>> GetGapsAsync(long fromUtc, long toUtc, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT start_utc,end_utc,reason FROM coverage
                WHERE end_utc>$from AND start_utc<$to ORDER BY start_utc;
                """;
            command.Parameters.AddWithValue("$from", fromUtc);
            command.Parameters.AddWithValue("$to", toUtc);
            var rows = new List<AppGapRow>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                rows.Add(new AppGapRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
            return rows;
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertMetaAsync(string appKey, string? exePath, string displayName, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_meta(app_key,exe_path,display_name,source,first_seen_utc)
                VALUES($key,$exe,$name,'auto',$now)
                ON CONFLICT(app_key) DO UPDATE SET
                    display_name=CASE WHEN app_meta.source='manual' THEN app_meta.display_name ELSE excluded.display_name END,
                    exe_path=COALESCE(app_meta.exe_path,excluded.exe_path);
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$exe", (object?)exePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", displayName);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Manual rename.  The row is created on demand so an alias can be set for an app that
    /// has not been seen yet.</summary>
    public async Task SetDisplayNameAsync(string appKey, string displayName, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_meta(app_key,display_name,source,first_seen_utc)
                VALUES($key,$name,'manual',$now)
                ON CONFLICT(app_key) DO UPDATE SET display_name=$name, source='manual';
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$name", displayName);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetHiddenAsync(string appKey, bool hidden, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_meta(app_key,display_name,source,first_seen_utc,hidden)
                VALUES($key,$key,'manual',$now,$hidden)
                ON CONFLICT(app_key) DO UPDATE SET hidden=$hidden;
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$hidden", hidden ? 1 : 0);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Merges an application into another one; <paramref name="targetKey"/> null clears it.</summary>
    public async Task SetMergedIntoAsync(string appKey, string? targetKey, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_meta(app_key,display_name,source,first_seen_utc,merged_into,merge_detached)
                VALUES($key,$key,'manual',$now,$target,$detached)
                ON CONFLICT(app_key) DO UPDATE SET merged_into=$target,merge_detached=$detached;
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$target", (object?)targetKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$detached", targetKey is null ? 1 : 0);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    /// <summary>Per-application accent colour override, stored as "#RRGGBB".</summary>
    public async Task SetColorAsync(string appKey, string? color, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_meta(app_key,display_name,source,first_seen_utc,color)
                VALUES($key,$key,'manual',$now,$color)
                ON CONFLICT(app_key) DO UPDATE SET color=$color;
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Custom icon file (.ico/.png/.exe) for the application.</summary>
    public async Task SetIconPathAsync(string appKey, string? iconPath, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_meta(app_key,display_name,source,first_seen_utc,icon_path)
                VALUES($key,$key,'manual',$now,$icon)
                ON CONFLICT(app_key) DO UPDATE SET icon_path=$icon;
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$icon", (object?)iconPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    /// <summary>All time totals per application key, used to order the management list.</summary>
    public async Task<Dictionary<string, (double Seconds, double Wh)>> GetTotalsAsync(CancellationToken token = default)
    {
        var totals = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT app_key,SUM(seconds),SUM(wh) FROM app_day GROUP BY app_key";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                totals[reader.GetString(0)] = (reader.GetDouble(1), reader.GetDouble(2));
        }
        finally { _gate.Release(); }
        return totals;
    }
    /// <summary>Manual "this is / is not a system process" override; null restores the automatic rule.</summary>
    public async Task SetSystemOverrideAsync(string appKey, bool? asSystem, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_meta(app_key,display_name,source,first_seen_utc,system_override)
                VALUES($key,$key,'manual',$now,$value)
                ON CONFLICT(app_key) DO UPDATE SET system_override=$value;
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$value", asSystem is null ? DBNull.Value : (asSystem.Value ? 1 : 0));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<AppMetaRow>> GetMetaAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT app_key,exe_path,display_name,source,hidden,merged_into,color,icon_path,merge_detached,system_override FROM app_meta";
            var rows = new List<AppMetaRow>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                rows.Add(new AppMetaRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8) != 0,
                    reader.IsDBNull(9) ? null : reader.GetInt32(9)));
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Source != "manual" && ProcessNames.DisplayNameForPath(rows[i].ExePath ?? rows[i].AppKey) is { } branded)
                    rows[i] = rows[i] with { DisplayName = branded };
            return rows;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retention: intervals 90 days, hourly buckets 365 days, daily totals kept forever.</summary>
    public async Task PruneAsync(DateTimeOffset now, CancellationToken token = default)
    {
        var eventsCutoff = now.AddDays(-90).ToUnixTimeSeconds();
        var hourCutoff = now.AddDays(-365).ToUnixTimeSeconds();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM app_event WHERE end_utc < $events;
                DELETE FROM coverage WHERE end_utc < $events;
                DELETE FROM app_hour WHERE hour_utc < $hours;
                """;
            command.Parameters.AddWithValue("$events", eventsCutoff);
            command.Parameters.AddWithValue("$hours", hourCutoff);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
