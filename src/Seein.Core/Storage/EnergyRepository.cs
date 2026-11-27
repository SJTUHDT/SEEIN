using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Seein.Core.Storage;

public sealed class EnergyRepository : IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EnergyRepository(string databasePath)
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
                CREATE TABLE IF NOT EXISTS daily_energy (
                    local_date TEXT PRIMARY KEY,
                    local_month TEXT NOT NULL,
                    watt_hours REAL NOT NULL CHECK(watt_hours >= 0)
                );
                CREATE INDEX IF NOT EXISTS ix_daily_energy_month ON daily_energy(local_month);
                CREATE TABLE IF NOT EXISTS minute_power (
                    minute_utc INTEGER PRIMARY KEY,
                    min_w REAL NOT NULL,
                    avg_w REAL NOT NULL,
                    max_w REAL NOT NULL,
                    sample_count INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await MigrateAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task MigrateAsync(CancellationToken token)
    {
        using var versionCommand = _connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion) return;

        await using var transaction = await _connection.BeginTransactionAsync(token).ConfigureAwait(false);
        var columns = await GetColumnsAsync("daily_energy", (SqliteTransaction)transaction, token).ConfigureAwait(false);
        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cpu_wh"] = "REAL NULL",
            ["gpu_wh"] = "REAL NULL",
            ["other_wh"] = "REAL NULL",
            ["loss_wh"] = "REAL NULL",
            ["unclassified_wh"] = "REAL NOT NULL DEFAULT 0",
            ["covered_seconds"] = "REAL NOT NULL DEFAULT 0",
            ["classified_seconds"] = "REAL NOT NULL DEFAULT 0",
            ["cpu_covered_seconds"] = "REAL NOT NULL DEFAULT 0",
            ["gpu_covered_seconds"] = "REAL NOT NULL DEFAULT 0"
        };

        foreach (var (name, definition) in additions)
        {
            if (columns.Contains(name)) continue;
            using var alter = _connection.CreateCommand();
            alter.Transaction = (SqliteTransaction)transaction;
            alter.CommandText = $"ALTER TABLE daily_energy ADD COLUMN {name} {definition}";
            await alter.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        using var classifyHistory = _connection.CreateCommand();
        classifyHistory.Transaction = (SqliteTransaction)transaction;
        classifyHistory.CommandText = """
            UPDATE daily_energy SET unclassified_wh=watt_hours
            WHERE cpu_wh IS NULL AND gpu_wh IS NULL AND other_wh IS NULL AND loss_wh IS NULL
              AND unclassified_wh=0;
            PRAGMA user_version=1;
            """;
        await classifyHistory.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> GetColumnsAsync(string table, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) columns.Add(reader.GetString(1));
        return columns;
    }

    public async Task AddEnergyAsync(DateTimeOffset timestamp, double deltaWh, CancellationToken token = default)
    {
        if (!double.IsFinite(deltaWh) || deltaWh <= 0) return;
        var local = DateOnly.FromDateTime(timestamp.ToLocalTime().DateTime);
        await AddLegacyEnergyAsync(local, deltaWh, token).ConfigureAwait(false);
    }

    public async Task AddLegacyEnergyAsync(DateOnly localDate, double deltaWh, CancellationToken token = default)
    {
        if (!double.IsFinite(deltaWh) || deltaWh <= 0) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO daily_energy(local_date,local_month,watt_hours,unclassified_wh)
                VALUES($date,$month,$wh,$wh)
                ON CONFLICT(local_date) DO UPDATE SET
                    watt_hours=watt_hours+$wh, unclassified_wh=unclassified_wh+$wh;
                """;
            AddDateParameters(command, localDate);
            command.Parameters.AddWithValue("$wh", deltaWh);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task AddEnergyAsync(DateOnly localDate, EnergyDelta delta, CancellationToken token = default)
    {
        if (!double.IsFinite(delta.TotalWh) || delta.TotalWh <= 0) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO daily_energy(local_date,local_month,watt_hours,cpu_wh,gpu_wh,other_wh,loss_wh,
                    unclassified_wh,covered_seconds,classified_seconds,cpu_covered_seconds,gpu_covered_seconds)
                VALUES($date,$month,$total,$cpu,$gpu,$other,$loss,$unclassified,$covered,$classified,$cpu_covered,$gpu_covered)
                ON CONFLICT(local_date) DO UPDATE SET
                    watt_hours=watt_hours+$total,
                    cpu_wh=CASE WHEN $cpu IS NULL THEN cpu_wh ELSE COALESCE(cpu_wh,0)+$cpu END,
                    gpu_wh=CASE WHEN $gpu IS NULL THEN gpu_wh ELSE COALESCE(gpu_wh,0)+$gpu END,
                    other_wh=COALESCE(other_wh,0)+$other,
                    loss_wh=COALESCE(loss_wh,0)+$loss,
                    unclassified_wh=unclassified_wh+$unclassified,
                    covered_seconds=covered_seconds+$covered,
                    classified_seconds=classified_seconds+$classified,
                    cpu_covered_seconds=cpu_covered_seconds+$cpu_covered,
                    gpu_covered_seconds=gpu_covered_seconds+$gpu_covered;
                """;
            AddDateParameters(command, localDate);
            command.Parameters.AddWithValue("$total", delta.TotalWh);
            command.Parameters.AddWithValue("$cpu", (object?)delta.CpuWh ?? DBNull.Value);
            command.Parameters.AddWithValue("$gpu", (object?)delta.GpuWh ?? DBNull.Value);
            command.Parameters.AddWithValue("$other", delta.OtherWh);
            command.Parameters.AddWithValue("$loss", delta.LossWh);
            command.Parameters.AddWithValue("$unclassified", delta.UnclassifiedWh);
            command.Parameters.AddWithValue("$covered", delta.CoveredSeconds);
            command.Parameters.AddWithValue("$classified", delta.ClassifiedSeconds);
            command.Parameters.AddWithValue("$cpu_covered", delta.CpuCoveredSeconds);
            command.Parameters.AddWithValue("$gpu_covered", delta.GpuCoveredSeconds);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertMinutePowerAsync(DateTimeOffset timestamp, double min, double average, double max, int count, CancellationToken token = default)
    {
        if (count <= 0 || !double.IsFinite(min) || !double.IsFinite(average) || !double.IsFinite(max)) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var minute = timestamp.ToUnixTimeSeconds() / 60 * 60;
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO minute_power(minute_utc,min_w,avg_w,max_w,sample_count) VALUES($m,$min,$avg,$max,$count)
                ON CONFLICT(minute_utc) DO UPDATE SET min_w=min(min_w,$min), max_w=max(max_w,$max),
                    avg_w=((avg_w*sample_count)+($avg*$count))/(sample_count+$count), sample_count=sample_count+$count;
                """;
            command.Parameters.AddWithValue("$m", minute);
            command.Parameters.AddWithValue("$min", min);
            command.Parameters.AddWithValue("$avg", average);
            command.Parameters.AddWithValue("$max", max);
            command.Parameters.AddWithValue("$count", count);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<(double Today, double Month, double Total)> GetTotalsAsync(DateTimeOffset now, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var local = now.ToLocalTime();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT
                  COALESCE(SUM(CASE WHEN local_date=$date THEN watt_hours ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN local_month=$month THEN watt_hours ELSE 0 END),0),
                  COALESCE(SUM(watt_hours),0)
                FROM daily_energy;
                """;
            command.Parameters.AddWithValue("$date", local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$month", local.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            return (reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2));
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DailyEnergyEntry>> GetDailyEnergyAsync(string month, CancellationToken token = default)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Month must use yyyy-MM.", nameof(month));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Days recorded before the covered_seconds column existed have no duration, so the number of
            // minutes that actually hold a power sample is used as the "time in use" fallback.
            var minutes = new Dictionary<string, double>(StringComparer.Ordinal);
            using var minuteCommand = _connection.CreateCommand();
            minuteCommand.CommandText = """
                SELECT strftime('%Y-%m-%d', minute_utc, 'unixepoch', 'localtime') AS day, COUNT(*)
                FROM minute_power GROUP BY day
                """;
            await using (var minuteReader = await minuteCommand.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await minuteReader.ReadAsync(token).ConfigureAwait(false))
                    if (!minuteReader.IsDBNull(0)) minutes[minuteReader.GetString(0)] = minuteReader.GetInt64(1) * 60d;

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT local_date,watt_hours,covered_seconds FROM daily_energy WHERE local_month=$month ORDER BY local_date";
            command.Parameters.AddWithValue("$month", month);
            var entries = new List<DailyEnergyEntry>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var date = reader.GetString(0);
                var covered = reader.GetDouble(2);
                if (covered <= 0 && minutes.TryGetValue(date, out var sampled)) covered = sampled;
                entries.Add(new DailyEnergyEntry(date, reader.GetDouble(1), covered));
            }
            return entries;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Highest single day ever recorded, used on the overview page.</summary>
    public async Task<DailyEnergyEntry?> GetHighestDayAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT local_date,watt_hours,covered_seconds FROM daily_energy WHERE watt_hours > 0 ORDER BY watt_hours DESC LIMIT 1";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false)
                ? new DailyEnergyEntry(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2))
                : null;
        }
        finally { _gate.Release(); }
    }
    public async Task<(string? FirstMonth, string? LastMonth)> GetEnergyMonthRangeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT MIN(local_month),MAX(local_month) FROM daily_energy";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        finally { _gate.Release(); }
    }

    public async Task<EnergyDayResult> GetEnergyDayAsync(string localDate, string requestId,
        DateTimeOffset now, CancellationToken token = default)
    {
        if (!DateOnly.TryParseExact(localDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ArgumentException("Date must use yyyy-MM-dd.", nameof(localDate));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            double total = 0, unclassified = 0, covered = 0, classified = 0, cpuCovered = 0, gpuCovered = 0;
            double? cpu = null, gpu = null, other = null, loss = null;
            var exists = false;
            using var day = _connection.CreateCommand();
            day.CommandText = """
                SELECT watt_hours,cpu_wh,gpu_wh,other_wh,loss_wh,unclassified_wh,covered_seconds,
                    classified_seconds,cpu_covered_seconds,gpu_covered_seconds
                FROM daily_energy WHERE local_date=$date;
                """;
            day.Parameters.AddWithValue("$date", localDate);
            await using (var reader = await day.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    exists = true;
                    total = reader.GetDouble(0);
                    cpu = ReadNullable(reader, 1); gpu = ReadNullable(reader, 2); other = ReadNullable(reader, 3); loss = ReadNullable(reader, 4);
                    unclassified = reader.GetDouble(5); covered = reader.GetDouble(6); classified = reader.GetDouble(7);
                    cpuCovered = reader.GetDouble(8); gpuCovered = reader.GetDouble(9);
                }
            }

            var points = new List<PowerHistoryPoint>();
            var (startUtc, endUtc) = GetLocalDayUtcRange(date, TimeZoneInfo.Local);
            var cutoff = now.ToUniversalTime().AddDays(-365).ToUnixTimeSeconds();
            var start = Math.Max(startUtc.ToUnixTimeSeconds(), cutoff);
            if (start < endUtc.ToUnixTimeSeconds())
            {
                using var curve = _connection.CreateCommand();
                curve.CommandText = """
                    SELECT minute_utc,min_w,avg_w,max_w,sample_count
                    FROM minute_power WHERE minute_utc >= $start AND minute_utc < $end ORDER BY minute_utc;
                    """;
                curve.Parameters.AddWithValue("$start", start);
                curve.Parameters.AddWithValue("$end", endUtc.ToUnixTimeSeconds());
                await using var reader = await curve.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var seconds = Math.Clamp(reader.GetInt32(4) * .9, 0, 60);
                    points.Add(new PowerHistoryPoint(reader.GetInt64(0), 60, reader.GetDouble(1), reader.GetDouble(2),
                        reader.GetDouble(3), seconds, seconds < 45 ? 1 : 0));
                }
            }

            var isLegacy = exists && (unclassified > 0 || (cpu is null && gpu is null && other is null && loss is null));
            return new EnergyDayResult(localDate, requestId, total, cpu, gpu, other, loss, unclassified,
                covered, classified, points, "1 分钟", isLegacy,
                CpuCoveredSeconds: cpuCovered, GpuCoveredSeconds: gpuCovered);
        }
        finally { _gate.Release(); }
    }

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetLocalDayUtcRange(DateOnly date, TimeZoneInfo zone)
    {
        static DateTimeOffset Boundary(DateOnly localDate, TimeZoneInfo timeZone)
        {
            var local = DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            for (var minute = 0; timeZone.IsInvalidTime(local) && minute < 1440; minute++) local = local.AddMinutes(1);
            var offset = timeZone.IsAmbiguousTime(local)
                ? timeZone.GetAmbiguousTimeOffsets(local).Max()
                : timeZone.GetUtcOffset(local);
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }

        return (Boundary(date, zone), Boundary(date.AddDays(1), zone));
    }

    public async Task PruneMinuteHistoryAsync(DateTimeOffset now, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM minute_power WHERE minute_utc < $cutoff";
            command.Parameters.AddWithValue("$cutoff", now.AddDays(-365).ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<(string? BootId, double SessionWh)> GetSessionStateAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT key,value FROM state WHERE key IN ('boot_id','session_wh')";
            string? bootId = null; double sessionWh = 0;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (reader.GetString(0) == "boot_id") bootId = reader.GetString(1);
                else if (double.TryParse(reader.GetString(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) sessionWh = value;
            }
            return (bootId, sessionWh);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveSessionStateAsync(string bootId, double sessionWh, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(token).ConfigureAwait(false);
            foreach (var pair in new[] { new KeyValuePair<string, string>("boot_id", bootId), new("session_wh", sessionWh.ToString("R", CultureInfo.InvariantCulture)) })
            {
                using var command = _connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT INTO state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
                command.Parameters.AddWithValue("$key", pair.Key); command.Parameters.AddWithValue("$value", pair.Value);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static double? ReadNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static void AddDateParameters(SqliteCommand command, DateOnly localDate)
    {
        command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$month", localDate.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
