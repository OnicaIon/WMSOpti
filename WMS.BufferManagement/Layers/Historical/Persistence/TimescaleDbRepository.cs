using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using WMS.BufferManagement.Layers.Historical.Persistence.Models;

namespace WMS.BufferManagement.Layers.Historical.Persistence;

/// <summary>
/// Конфигурация для исторического хранилища
/// </summary>
public class HistoricalOptions
{
    public string Provider { get; set; } = "TimescaleDB";
    public string ConnectionString { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 90;
    public int ChunkIntervalDays { get; set; } = 7;
    public bool CompressionEnabled { get; set; } = true;
    public int CompressionAfterDays { get; set; } = 7;
}

/// <summary>
/// Реализация репозитория на базе TimescaleDB
/// </summary>
public class TimescaleDbRepository : IHistoricalRepository, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly HistoricalOptions _options;
    private readonly ILogger<TimescaleDbRepository> _logger;

    public TimescaleDbRepository(IOptions<HistoricalOptions> options, ILogger<TimescaleDbRepository> logger)
    {
        _options = options.Value;
        _connectionString = _options.ConnectionString;
        _logger = logger;
    }

    // === Инициализация ===

    public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        // Включаем TimescaleDB extension
        await ExecuteNonQueryAsync(conn, "CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;", cancellationToken);

        // Создаём таблицу tasks
        await ExecuteNonQueryAsync(conn, @"
            CREATE TABLE IF NOT EXISTS tasks (
                id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                created_at      TIMESTAMPTZ NOT NULL,
                started_at      TIMESTAMPTZ,
                completed_at    TIMESTAMPTZ,
                pallet_id       VARCHAR(50) NOT NULL,
                product_type    VARCHAR(50),
                weight_kg       DECIMAL(10,2),
                weight_category VARCHAR(20),
                forklift_id     VARCHAR(50),
                from_zone       VARCHAR(50),
                from_slot       VARCHAR(50),
                to_zone         VARCHAR(50),
                to_slot         VARCHAR(50),
                distance_meters DECIMAL(10,2),
                status          VARCHAR(20) NOT NULL,
                duration_sec    DECIMAL(10,2),
                failure_reason  TEXT
            );", cancellationToken);

        // Конвертируем в hypertable (если ещё не)
        await TryCreateHypertable(conn, "tasks", "created_at", $"{_options.ChunkIntervalDays} days", cancellationToken);

        // Создаём таблицу picker_metrics
        await ExecuteNonQueryAsync(conn, @"
            CREATE TABLE IF NOT EXISTS picker_metrics (
                time             TIMESTAMPTZ NOT NULL,
                picker_id        VARCHAR(50) NOT NULL,
                consumption_rate DECIMAL(10,2),
                items_picked     INT,
                efficiency       DECIMAL(5,2),
                active           BOOLEAN,
                PRIMARY KEY (time, picker_id)
            );", cancellationToken);

        await TryCreateHypertable(conn, "picker_metrics", "time", "1 day", cancellationToken);

        // Создаём таблицу buffer_snapshots
        await ExecuteNonQueryAsync(conn, @"
            CREATE TABLE IF NOT EXISTS buffer_snapshots (
                time              TIMESTAMPTZ NOT NULL PRIMARY KEY,
                buffer_level      DECIMAL(5,4),
                buffer_state      VARCHAR(20),
                pallets_count     INT,
                active_forklifts  INT,
                active_pickers    INT,
                consumption_rate  DECIMAL(10,2),
                delivery_rate     DECIMAL(10,2),
                queue_length      INT,
                pending_tasks     INT
            );", cancellationToken);

        await TryCreateHypertable(conn, "buffer_snapshots", "time", "1 day", cancellationToken);

        // Создаём индексы
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_tasks_forklift ON tasks (forklift_id, created_at DESC);",
            cancellationToken);
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks (status, created_at DESC);",
            cancellationToken);
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_picker_metrics_picker ON picker_metrics (picker_id, time DESC);",
            cancellationToken);

        // Настраиваем compression (если включено)
        if (_options.CompressionEnabled)
        {
            await SetupCompression(conn, "tasks", "forklift_id", cancellationToken);
            await SetupCompression(conn, "picker_metrics", "picker_id", cancellationToken);
            await SetupCompression(conn, "buffer_snapshots", null, cancellationToken);
        }

        // Настраиваем retention policy
        await SetupRetentionPolicy(conn, "tasks", cancellationToken);
        await SetupRetentionPolicy(conn, "picker_metrics", cancellationToken);
        await SetupRetentionPolicy(conn, "buffer_snapshots", cancellationToken);

        _logger.LogInformation("TimescaleDB schema initialized successfully");
    }

    private async Task TryCreateHypertable(NpgsqlConnection conn, string table, string timeColumn,
        string chunkInterval, CancellationToken ct)
    {
        try
        {
            await ExecuteNonQueryAsync(conn,
                $"SELECT create_hypertable('{table}', '{timeColumn}', chunk_time_interval => INTERVAL '{chunkInterval}', if_not_exists => TRUE);",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create hypertable for {Table}, may already exist", table);
        }
    }

    private async Task SetupCompression(NpgsqlConnection conn, string table, string? segmentBy, CancellationToken ct)
    {
        try
        {
            var segmentClause = segmentBy != null ? $", timescaledb.compress_segmentby = '{segmentBy}'" : "";
            await ExecuteNonQueryAsync(conn,
                $"ALTER TABLE {table} SET (timescaledb.compress{segmentClause});", ct);
            await ExecuteNonQueryAsync(conn,
                $"SELECT add_compression_policy('{table}', INTERVAL '{_options.CompressionAfterDays} days', if_not_exists => TRUE);", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not setup compression for {Table}", table);
        }
    }

    private async Task SetupRetentionPolicy(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        try
        {
            await ExecuteNonQueryAsync(conn,
                $"SELECT add_retention_policy('{table}', INTERVAL '{_options.RetentionDays} days', if_not_exists => TRUE);", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not setup retention policy for {Table}", table);
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // === Tasks ===

    public async Task SaveTaskAsync(TaskRecord task, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO tasks (id, created_at, started_at, completed_at, pallet_id, product_type,
                weight_kg, weight_category, forklift_id, from_zone, from_slot, to_zone, to_slot,
                distance_meters, status, duration_sec, failure_reason)
            VALUES (@id, @created_at, @started_at, @completed_at, @pallet_id, @product_type,
                @weight_kg, @weight_category, @forklift_id, @from_zone, @from_slot, @to_zone, @to_slot,
                @distance_meters, @status, @duration_sec, @failure_reason)
            ON CONFLICT (id) DO UPDATE SET
                started_at = EXCLUDED.started_at,
                completed_at = EXCLUDED.completed_at,
                status = EXCLUDED.status,
                duration_sec = EXCLUDED.duration_sec,
                failure_reason = EXCLUDED.failure_reason;", conn);

        AddTaskParameters(cmd, task);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveTasksBatchAsync(IEnumerable<TaskRecord> tasks, CancellationToken cancellationToken = default)
    {
        var taskList = tasks.ToList();
        if (taskList.Count == 0) return;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(conn);

        foreach (var task in taskList)
        {
            var cmd = new NpgsqlBatchCommand(@"
                INSERT INTO tasks (id, created_at, started_at, completed_at, pallet_id, product_type,
                    weight_kg, weight_category, forklift_id, from_zone, from_slot, to_zone, to_slot,
                    distance_meters, status, duration_sec, failure_reason)
                VALUES (@id, @created_at, @started_at, @completed_at, @pallet_id, @product_type,
                    @weight_kg, @weight_category, @forklift_id, @from_zone, @from_slot, @to_zone, @to_slot,
                    @distance_meters, @status, @duration_sec, @failure_reason)
                ON CONFLICT (id) DO NOTHING;");

            AddTaskParametersToBatch(cmd, task);
            batch.BatchCommands.Add(cmd);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Saved {Count} task records", taskList.Count);
    }

    public async Task<IReadOnlyList<ForkliftTaskStats>> GetForkliftStatsAsync(
        DateTime fromTime, DateTime toTime, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                forklift_id,
                COUNT(*) AS total_tasks,
                COUNT(*) FILTER (WHERE status = 'Completed') AS completed_tasks,
                COUNT(*) FILTER (WHERE status = 'Failed') AS failed_tasks,
                COALESCE(AVG(duration_sec), 0) AS avg_duration_sec,
                COALESCE(SUM(distance_meters), 0) AS total_distance_meters
            FROM tasks
            WHERE created_at >= @from_time AND created_at < @to_time
              AND forklift_id IS NOT NULL
            GROUP BY forklift_id
            ORDER BY total_tasks DESC;", conn);

        cmd.Parameters.AddWithValue("from_time", fromTime);
        cmd.Parameters.AddWithValue("to_time", toTime);

        var results = new List<ForkliftTaskStats>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ForkliftTaskStats
            {
                ForkliftId = reader.GetString(0),
                TotalTasks = reader.GetInt32(1),
                CompletedTasks = reader.GetInt32(2),
                FailedTasks = reader.GetInt32(3),
                AvgDurationSec = reader.GetDecimal(4),
                TotalDistanceMeters = reader.GetDecimal(5)
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<RouteStats>> GetSlowestRoutesAsync(
        int limit = 20, int lastDays = 7, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                from_zone,
                to_zone,
                AVG(duration_sec) AS avg_duration_sec,
                COUNT(*) AS task_count
            FROM tasks
            WHERE status = 'Completed'
              AND created_at > NOW() - make_interval(days => @last_days)
              AND from_zone IS NOT NULL AND to_zone IS NOT NULL
            GROUP BY from_zone, to_zone
            ORDER BY avg_duration_sec DESC
            LIMIT @limit;", conn);

        cmd.Parameters.AddWithValue("last_days", lastDays);
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<RouteStats>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RouteStats
            {
                FromZone = reader.GetString(0),
                ToZone = reader.GetString(1),
                AvgDurationSec = reader.GetDecimal(2),
                TaskCount = reader.GetInt32(3)
            });
        }
        return results;
    }

    // === Picker Metrics ===

    public async Task SavePickerMetricAsync(PickerMetric metric, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO picker_metrics (time, picker_id, consumption_rate, items_picked, efficiency, active)
            VALUES (@time, @picker_id, @consumption_rate, @items_picked, @efficiency, @active)
            ON CONFLICT (time, picker_id) DO UPDATE SET
                consumption_rate = EXCLUDED.consumption_rate,
                items_picked = EXCLUDED.items_picked,
                efficiency = EXCLUDED.efficiency,
                active = EXCLUDED.active;", conn);

        cmd.Parameters.AddWithValue("time", metric.Time);
        cmd.Parameters.AddWithValue("picker_id", metric.PickerId);
        cmd.Parameters.AddWithValue("consumption_rate", (object?)metric.ConsumptionRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("items_picked", (object?)metric.ItemsPicked ?? DBNull.Value);
        cmd.Parameters.AddWithValue("efficiency", (object?)metric.Efficiency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("active", metric.Active);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SavePickerMetricsBatchAsync(IEnumerable<PickerMetric> metrics, CancellationToken cancellationToken = default)
    {
        var metricList = metrics.ToList();
        if (metricList.Count == 0) return;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY picker_metrics (time, picker_id, consumption_rate, items_picked, efficiency, active) FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        foreach (var m in metricList)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(m.Time, NpgsqlTypes.NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(m.PickerId, cancellationToken);
            await writer.WriteAsync(m.ConsumptionRate, cancellationToken);
            await writer.WriteAsync(m.ItemsPicked, cancellationToken);
            await writer.WriteAsync(m.Efficiency, cancellationToken);
            await writer.WriteAsync(m.Active, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
        _logger.LogDebug("Saved {Count} picker metrics via COPY", metricList.Count);
    }

    public async Task<IReadOnlyList<PickerHourlyStats>> GetPickerHourlyStatsAsync(
        string pickerId, DateTime fromTime, DateTime toTime, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                time_bucket('1 hour', time) AS hour,
                picker_id,
                AVG(consumption_rate) AS avg_rate,
                MAX(consumption_rate) AS max_rate,
                AVG(efficiency) AS avg_efficiency,
                COUNT(*) AS samples
            FROM picker_metrics
            WHERE picker_id = @picker_id
              AND time >= @from_time AND time < @to_time
            GROUP BY hour, picker_id
            ORDER BY hour;", conn);

        cmd.Parameters.AddWithValue("picker_id", pickerId);
        cmd.Parameters.AddWithValue("from_time", fromTime);
        cmd.Parameters.AddWithValue("to_time", toTime);

        var results = new List<PickerHourlyStats>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PickerHourlyStats
            {
                Hour = reader.GetDateTime(0),
                PickerId = reader.GetString(1),
                AvgRate = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
                MaxRate = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
                AvgEfficiency = reader.IsDBNull(4) ? 0 : reader.GetDecimal(4),
                Samples = reader.GetInt32(5)
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<PickerHourlyPattern>> GetPickerHourlyPatternsAsync(
        int lastDays = 30, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                picker_id,
                EXTRACT(HOUR FROM time)::INT AS hour_of_day,
                AVG(consumption_rate) AS avg_consumption_rate,
                AVG(efficiency) AS avg_efficiency,
                COUNT(*) AS sample_count
            FROM picker_metrics
            WHERE time > NOW() - make_interval(days => @last_days)
              AND consumption_rate IS NOT NULL
            GROUP BY picker_id, hour_of_day
            ORDER BY picker_id, hour_of_day;", conn);

        cmd.Parameters.AddWithValue("last_days", lastDays);

        var results = new List<PickerHourlyPattern>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PickerHourlyPattern
            {
                PickerId = reader.GetString(0),
                HourOfDay = reader.GetInt32(1),
                AvgConsumptionRate = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
                AvgEfficiency = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
                SampleCount = reader.GetInt32(4)
            });
        }
        return results;
    }

    // === Buffer Snapshots ===

    public async Task SaveBufferSnapshotAsync(BufferSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO buffer_snapshots (time, buffer_level, buffer_state, pallets_count,
                active_forklifts, active_pickers, consumption_rate, delivery_rate, queue_length, pending_tasks)
            VALUES (@time, @buffer_level, @buffer_state, @pallets_count,
                @active_forklifts, @active_pickers, @consumption_rate, @delivery_rate, @queue_length, @pending_tasks)
            ON CONFLICT (time) DO UPDATE SET
                buffer_level = EXCLUDED.buffer_level,
                buffer_state = EXCLUDED.buffer_state,
                pallets_count = EXCLUDED.pallets_count,
                active_forklifts = EXCLUDED.active_forklifts,
                active_pickers = EXCLUDED.active_pickers,
                consumption_rate = EXCLUDED.consumption_rate,
                delivery_rate = EXCLUDED.delivery_rate,
                queue_length = EXCLUDED.queue_length,
                pending_tasks = EXCLUDED.pending_tasks;", conn);

        cmd.Parameters.AddWithValue("time", snapshot.Time);
        cmd.Parameters.AddWithValue("buffer_level", snapshot.BufferLevel);
        cmd.Parameters.AddWithValue("buffer_state", snapshot.BufferState);
        cmd.Parameters.AddWithValue("pallets_count", snapshot.PalletsCount);
        cmd.Parameters.AddWithValue("active_forklifts", snapshot.ActiveForklifts);
        cmd.Parameters.AddWithValue("active_pickers", snapshot.ActivePickers);
        cmd.Parameters.AddWithValue("consumption_rate", (object?)snapshot.ConsumptionRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("delivery_rate", (object?)snapshot.DeliveryRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("queue_length", snapshot.QueueLength);
        cmd.Parameters.AddWithValue("pending_tasks", snapshot.PendingTasks);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BufferPeriodStats?> GetBufferStatsAsync(
        DateTime fromTime, DateTime toTime, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                MIN(time) AS period_start,
                MAX(time) AS period_end,
                AVG(buffer_level) AS avg_level,
                MIN(buffer_level) AS min_level,
                MAX(buffer_level) AS max_level,
                AVG(consumption_rate) AS avg_consumption,
                AVG(delivery_rate) AS avg_delivery,
                COUNT(*) FILTER (WHERE buffer_state = 'Critical') AS critical_count
            FROM buffer_snapshots
            WHERE time >= @from_time AND time < @to_time;", conn);

        cmd.Parameters.AddWithValue("from_time", fromTime);
        cmd.Parameters.AddWithValue("to_time", toTime);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
        {
            return new BufferPeriodStats
            {
                PeriodStart = reader.GetDateTime(0),
                PeriodEnd = reader.GetDateTime(1),
                AvgLevel = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
                MinLevel = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
                MaxLevel = reader.IsDBNull(4) ? 0 : reader.GetDecimal(4),
                AvgConsumption = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                AvgDelivery = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                CriticalCount = reader.GetInt32(7)
            };
        }
        return null;
    }

    public async Task<IReadOnlyList<BufferSnapshot>> GetBufferTimeSeriesAsync(
        DateTime fromTime, DateTime toTime, TimeSpan? bucket = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        var bucketInterval = bucket ?? TimeSpan.FromMinutes(5);
        await using var cmd = new NpgsqlCommand($@"
            SELECT
                time_bucket(@bucket, time) AS bucket_time,
                AVG(buffer_level) AS buffer_level,
                MODE() WITHIN GROUP (ORDER BY buffer_state) AS buffer_state,
                AVG(pallets_count)::INT AS pallets_count,
                AVG(active_forklifts)::INT AS active_forklifts,
                AVG(active_pickers)::INT AS active_pickers,
                AVG(consumption_rate) AS consumption_rate,
                AVG(delivery_rate) AS delivery_rate,
                AVG(queue_length)::INT AS queue_length,
                AVG(pending_tasks)::INT AS pending_tasks
            FROM buffer_snapshots
            WHERE time >= @from_time AND time < @to_time
            GROUP BY bucket_time
            ORDER BY bucket_time;", conn);

        cmd.Parameters.AddWithValue("bucket", bucketInterval);
        cmd.Parameters.AddWithValue("from_time", fromTime);
        cmd.Parameters.AddWithValue("to_time", toTime);

        var results = new List<BufferSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BufferSnapshot
            {
                Time = reader.GetDateTime(0),
                BufferLevel = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1),
                BufferState = reader.IsDBNull(2) ? "Normal" : reader.GetString(2),
                PalletsCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                ActiveForklifts = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                ActivePickers = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                ConsumptionRate = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                DeliveryRate = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                QueueLength = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                PendingTasks = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
            });
        }
        return results;
    }

    // === ML Data Export ===

    public async Task<IReadOnlyList<PickerSpeedTrainingRow>> ExportPickerSpeedTrainingDataAsync(
        int lastDays = 30, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            WITH picker_avg AS (
                SELECT picker_id, AVG(consumption_rate) AS avg_7d
                FROM picker_metrics
                WHERE time > NOW() - INTERVAL '7 days'
                GROUP BY picker_id
            ),
            hourly_avg AS (
                SELECT picker_id, EXTRACT(HOUR FROM time)::INT AS hour, AVG(consumption_rate) AS avg_same_hour
                FROM picker_metrics
                WHERE time > NOW() - make_interval(days => @last_days)
                GROUP BY picker_id, hour
            )
            SELECT
                m.picker_id,
                EXTRACT(HOUR FROM m.time)::INT AS hour_of_day,
                EXTRACT(DOW FROM m.time)::INT AS day_of_week,
                COALESCE(pa.avg_7d, 0)::REAL AS avg_speed_last_7_days,
                COALESCE(ha.avg_same_hour, 0)::REAL AS avg_speed_same_hour,
                m.consumption_rate::REAL AS speed
            FROM picker_metrics m
            LEFT JOIN picker_avg pa ON m.picker_id = pa.picker_id
            LEFT JOIN hourly_avg ha ON m.picker_id = ha.picker_id AND EXTRACT(HOUR FROM m.time)::INT = ha.hour
            WHERE m.time > NOW() - make_interval(days => @last_days)
              AND m.consumption_rate IS NOT NULL
            ORDER BY m.time;", conn);

        cmd.Parameters.AddWithValue("last_days", lastDays);

        var results = new List<PickerSpeedTrainingRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PickerSpeedTrainingRow
            {
                PickerId = reader.GetString(0),
                HourOfDay = reader.GetInt32(1),
                DayOfWeek = reader.GetInt32(2),
                AvgSpeedLast7Days = reader.GetFloat(3),
                AvgSpeedSameHour = reader.GetFloat(4),
                Speed = reader.GetFloat(5)
            });
        }

        _logger.LogInformation("Exported {Count} rows for picker speed training", results.Count);
        return results;
    }

    public async Task<IReadOnlyList<DemandTrainingRow>> ExportDemandTrainingDataAsync(
        int lastDays = 30, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            WITH future_demand AS (
                SELECT
                    time,
                    LEAD(consumption_rate, 3) OVER (ORDER BY time) * 0.25 AS demand_next_15min
                FROM buffer_snapshots
            )
            SELECT
                EXTRACT(HOUR FROM b.time)::INT AS hour_of_day,
                EXTRACT(DOW FROM b.time)::INT AS day_of_week,
                b.active_pickers,
                COALESCE(b.consumption_rate / NULLIF(b.active_pickers, 0), 0)::REAL AS avg_picker_speed,
                b.buffer_level::REAL,
                COALESCE(fd.demand_next_15min, 0)::REAL AS demand_next_15min
            FROM buffer_snapshots b
            JOIN future_demand fd ON b.time = fd.time
            WHERE b.time > NOW() - make_interval(days => @last_days)
              AND fd.demand_next_15min IS NOT NULL
            ORDER BY b.time;", conn);

        cmd.Parameters.AddWithValue("last_days", lastDays);

        var results = new List<DemandTrainingRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DemandTrainingRow
            {
                HourOfDay = reader.GetInt32(0),
                DayOfWeek = reader.GetInt32(1),
                ActivePickers = reader.GetInt32(2),
                AvgPickerSpeed = reader.GetFloat(3),
                BufferLevel = reader.GetFloat(4),
                DemandNext15Min = reader.GetFloat(5)
            });
        }

        _logger.LogInformation("Exported {Count} rows for demand training", results.Count);
        return results;
    }

    // === Helpers ===

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddTaskParameters(NpgsqlCommand cmd, TaskRecord task)
    {
        cmd.Parameters.AddWithValue("id", task.Id);
        cmd.Parameters.AddWithValue("created_at", task.CreatedAt);
        cmd.Parameters.AddWithValue("started_at", (object?)task.StartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("completed_at", (object?)task.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pallet_id", task.PalletId);
        cmd.Parameters.AddWithValue("product_type", (object?)task.ProductType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("weight_kg", (object?)task.WeightKg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("weight_category", (object?)task.WeightCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("forklift_id", (object?)task.ForkliftId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_zone", (object?)task.FromZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_slot", (object?)task.FromSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_zone", (object?)task.ToZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_slot", (object?)task.ToSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("distance_meters", (object?)task.DistanceMeters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", task.Status);
        cmd.Parameters.AddWithValue("duration_sec", (object?)task.DurationSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failure_reason", (object?)task.FailureReason ?? DBNull.Value);
    }

    private static void AddTaskParametersToBatch(NpgsqlBatchCommand cmd, TaskRecord task)
    {
        cmd.Parameters.AddWithValue("id", task.Id);
        cmd.Parameters.AddWithValue("created_at", task.CreatedAt);
        cmd.Parameters.AddWithValue("started_at", (object?)task.StartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("completed_at", (object?)task.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pallet_id", task.PalletId);
        cmd.Parameters.AddWithValue("product_type", (object?)task.ProductType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("weight_kg", (object?)task.WeightKg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("weight_category", (object?)task.WeightCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("forklift_id", (object?)task.ForkliftId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_zone", (object?)task.FromZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_slot", (object?)task.FromSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_zone", (object?)task.ToZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_slot", (object?)task.ToSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("distance_meters", (object?)task.DistanceMeters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", task.Status);
        cmd.Parameters.AddWithValue("duration_sec", (object?)task.DurationSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failure_reason", (object?)task.FailureReason ?? DBNull.Value);
    }

    public async ValueTask DisposeAsync()
    {
        // NpgsqlConnection is disposed per-operation, nothing to clean up
        await Task.CompletedTask;
    }
}
