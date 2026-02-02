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
/// Настройки расчёта статистики маршрутов
/// </summary>
public class RouteStatisticsOptions
{
    /// <summary>
    /// Нормализовать буферную зону (все ячейки буфера = одна точка)
    /// </summary>
    public bool NormalizeBufferZone { get; set; } = true;

    /// <summary>
    /// Код зоны буфера (например "I")
    /// </summary>
    public string BufferZoneCode { get; set; } = "I";

    /// <summary>
    /// Константа для замены ячеек буфера
    /// </summary>
    public string BufferZoneConstant { get; set; } = "BUFFER";
}

/// <summary>
/// Реализация репозитория на базе TimescaleDB
/// </summary>
public class TimescaleDbRepository : IHistoricalRepository, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly HistoricalOptions _options;
    private readonly RouteStatisticsOptions _routeOptions;
    private readonly ILogger<TimescaleDbRepository> _logger;

    public TimescaleDbRepository(
        IOptions<HistoricalOptions> options,
        IOptions<RouteStatisticsOptions> routeOptions,
        ILogger<TimescaleDbRepository> logger)
    {
        _options = options.Value;
        _routeOptions = routeOptions?.Value ?? new RouteStatisticsOptions();
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
                worker_id       VARCHAR(50),
                worker_name     VARCHAR(200),
                worker_role     VARCHAR(20),
                template_code   VARCHAR(10),
                template_name   VARCHAR(200),
                task_basis_number VARCHAR(50),
                from_zone       VARCHAR(50),
                from_slot       VARCHAR(50),
                to_zone         VARCHAR(50),
                to_slot         VARCHAR(50),
                distance_meters DECIMAL(10,2),
                status          VARCHAR(20) NOT NULL,
                duration_sec    DECIMAL(10,2),
                failure_reason  TEXT,
                forklift_id     VARCHAR(50)
            );", cancellationToken);

        // Добавляем новые колонки если таблица уже существует
        await TryAddColumn(conn, "tasks", "worker_id", "VARCHAR(50)", cancellationToken);
        await TryAddColumn(conn, "tasks", "worker_name", "VARCHAR(200)", cancellationToken);
        await TryAddColumn(conn, "tasks", "worker_role", "VARCHAR(20)", cancellationToken);
        await TryAddColumn(conn, "tasks", "template_code", "VARCHAR(10)", cancellationToken);
        await TryAddColumn(conn, "tasks", "template_name", "VARCHAR(200)", cancellationToken);
        await TryAddColumn(conn, "tasks", "task_basis_number", "VARCHAR(50)", cancellationToken);

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

        // Таблица работников
        await ExecuteNonQueryAsync(conn, @"
            CREATE TABLE IF NOT EXISTS workers (
                id                  VARCHAR(50) PRIMARY KEY,
                name                VARCHAR(200) NOT NULL,
                role                VARCHAR(20) NOT NULL,
                total_tasks         INT DEFAULT 0,
                completed_tasks     INT DEFAULT 0,
                avg_duration_sec    DECIMAL(10,2),
                median_duration_sec DECIMAL(10,2),
                stddev_duration_sec DECIMAL(10,2),
                min_duration_sec    DECIMAL(10,2),
                max_duration_sec    DECIMAL(10,2),
                p95_duration_sec    DECIMAL(10,2),
                tasks_per_hour      DECIMAL(10,2),
                efficiency_score    DECIMAL(5,2),
                first_task_at       TIMESTAMPTZ,
                last_task_at        TIMESTAMPTZ,
                updated_at          TIMESTAMPTZ DEFAULT NOW()
            );", cancellationToken);

        // Таблица статистики маршрутов
        await ExecuteNonQueryAsync(conn, @"
            CREATE TABLE IF NOT EXISTS route_statistics (
                from_zone           VARCHAR(50) NOT NULL,
                from_slot           VARCHAR(50) NOT NULL,
                to_zone             VARCHAR(50) NOT NULL,
                to_slot             VARCHAR(50) NOT NULL,
                total_trips         INT DEFAULT 0,
                normalized_trips    INT DEFAULT 0,
                avg_duration_sec    DECIMAL(10,2),
                median_duration_sec DECIMAL(10,2),
                stddev_duration_sec DECIMAL(10,2),
                min_duration_sec    DECIMAL(10,2),
                max_duration_sec    DECIMAL(10,2),
                p5_duration_sec     DECIMAL(10,2),
                p95_duration_sec    DECIMAL(10,2),
                lower_bound_sec     DECIMAL(10,2),
                upper_bound_sec     DECIMAL(10,2),
                outliers_removed    INT DEFAULT 0,
                predicted_duration_sec DECIMAL(10,2),
                confidence_level    DECIMAL(5,4),
                updated_at          TIMESTAMPTZ DEFAULT NOW(),
                PRIMARY KEY (from_slot, to_slot)
            );", cancellationToken);

        // Индексы для route_statistics
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_route_stats_zones ON route_statistics (from_zone, to_zone);",
            cancellationToken);

        // Таблица статистики пикер + товар
        await ExecuteNonQueryAsync(conn, @"
            CREATE TABLE IF NOT EXISTS picker_product_stats (
                picker_id           VARCHAR(50) NOT NULL,
                picker_name         VARCHAR(200),
                product_sku         VARCHAR(50) NOT NULL,
                product_name        VARCHAR(200),
                total_lines         INT DEFAULT 0,
                total_qty           DECIMAL(10,2) DEFAULT 0,
                avg_duration_sec    DECIMAL(10,2),
                median_duration_sec DECIMAL(10,2),
                stddev_duration_sec DECIMAL(10,2),
                min_duration_sec    DECIMAL(10,2),
                max_duration_sec    DECIMAL(10,2),
                lines_per_min       DECIMAL(10,4),
                qty_per_min         DECIMAL(10,4),
                confidence          DECIMAL(5,4),
                first_task_at       TIMESTAMPTZ,
                last_task_at        TIMESTAMPTZ,
                updated_at          TIMESTAMPTZ DEFAULT NOW(),
                PRIMARY KEY (picker_id, product_sku)
            );", cancellationToken);

        // Индексы для picker_product_stats
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_picker_product_picker ON picker_product_stats (picker_id);",
            cancellationToken);
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_picker_product_sku ON picker_product_stats (product_sku);",
            cancellationToken);

        // Создаём индексы
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_tasks_forklift ON tasks (forklift_id, created_at DESC);",
            cancellationToken);
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks (status, created_at DESC);",
            cancellationToken);
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_tasks_worker ON tasks (worker_id, worker_role, created_at DESC);",
            cancellationToken);
        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_tasks_template ON tasks (template_code, created_at DESC);",
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

    private async Task TryAddColumn(NpgsqlConnection conn, string table, string column, string type, CancellationToken ct)
    {
        try
        {
            await ExecuteNonQueryAsync(conn,
                $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {type};", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Column {Column} may already exist in {Table}", column, table);
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
                weight_kg, weight_category, qty, worker_id, worker_name, worker_role,
                template_code, template_name, task_basis_number,
                from_zone, from_slot, to_zone, to_slot,
                distance_meters, status, duration_sec, failure_reason, forklift_id)
            VALUES (@id, @created_at, @started_at, @completed_at, @pallet_id, @product_type,
                @weight_kg, @weight_category, @qty, @worker_id, @worker_name, @worker_role,
                @template_code, @template_name, @task_basis_number,
                @from_zone, @from_slot, @to_zone, @to_slot,
                @distance_meters, @status, @duration_sec, @failure_reason, @forklift_id)
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
                    weight_kg, weight_category, qty, worker_id, worker_name, worker_role,
                    template_code, template_name, task_basis_number,
                    from_zone, from_slot, to_zone, to_slot,
                    distance_meters, status, duration_sec, failure_reason, forklift_id)
                VALUES (@id, @created_at, @started_at, @completed_at, @pallet_id, @product_type,
                    @weight_kg, @weight_category, @qty, @worker_id, @worker_name, @worker_role,
                    @template_code, @template_name, @task_basis_number,
                    @from_zone, @from_slot, @to_zone, @to_slot,
                    @distance_meters, @status, @duration_sec, @failure_reason, @forklift_id)
                ON CONFLICT (id) DO NOTHING;");

            AddTaskParametersToBatch(cmd, task);
            batch.BatchCommands.Add(cmd);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Saved {Count} task records", taskList.Count);
    }

    public async Task TruncateTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(conn, "TRUNCATE TABLE tasks;", cancellationToken);
        _logger.LogInformation("Tasks table truncated");
    }

    public async Task<long> GetTasksCountAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM tasks;", conn);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : 0;
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

    // === Products ===

    public async Task SaveProductsBatchAsync(IEnumerable<ProductRecord> products, CancellationToken cancellationToken = default)
    {
        var productList = products.ToList();
        if (productList.Count == 0) return;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        // UPSERT with ON CONFLICT
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO products (code, sku, name, external_code, vendor_code, barcode,
                                  weight_kg, volume_m3, weight_category, category_code,
                                  category_name, max_qty_per_pallet, synced_at)
            VALUES (@code, @sku, @name, @external_code, @vendor_code, @barcode,
                    @weight_kg, @volume_m3, @weight_category, @category_code,
                    @category_name, @max_qty_per_pallet, @synced_at)
            ON CONFLICT (code) DO UPDATE SET
                sku = EXCLUDED.sku,
                name = EXCLUDED.name,
                external_code = EXCLUDED.external_code,
                vendor_code = EXCLUDED.vendor_code,
                barcode = EXCLUDED.barcode,
                weight_kg = EXCLUDED.weight_kg,
                volume_m3 = EXCLUDED.volume_m3,
                weight_category = EXCLUDED.weight_category,
                category_code = EXCLUDED.category_code,
                category_name = EXCLUDED.category_name,
                max_qty_per_pallet = EXCLUDED.max_qty_per_pallet,
                synced_at = EXCLUDED.synced_at;", conn);

        foreach (var p in productList)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("code", p.Code);
            cmd.Parameters.AddWithValue("sku", (object?)p.Sku ?? DBNull.Value);
            cmd.Parameters.AddWithValue("name", p.Name);
            cmd.Parameters.AddWithValue("external_code", (object?)p.ExternalCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("vendor_code", (object?)p.VendorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("barcode", (object?)p.Barcode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("weight_kg", p.WeightKg);
            cmd.Parameters.AddWithValue("volume_m3", p.VolumeM3);
            cmd.Parameters.AddWithValue("weight_category", p.WeightCategory);
            cmd.Parameters.AddWithValue("category_code", (object?)p.CategoryCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("category_name", (object?)p.CategoryName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("max_qty_per_pallet", p.MaxQtyPerPallet);
            cmd.Parameters.AddWithValue("synced_at", p.SyncedAt);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Saved {Count} products to database", productList.Count);
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

    private static async Task<int> ExecuteNonQueryWithCountAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync(ct);
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
        cmd.Parameters.AddWithValue("qty", task.Qty);
        cmd.Parameters.AddWithValue("worker_id", (object?)task.WorkerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("worker_name", (object?)task.WorkerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("worker_role", (object?)task.WorkerRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("template_code", (object?)task.TemplateCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("template_name", (object?)task.TemplateName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("task_basis_number", (object?)task.TaskBasisNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_zone", (object?)task.FromZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_slot", (object?)task.FromSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_zone", (object?)task.ToZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_slot", (object?)task.ToSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("distance_meters", (object?)task.DistanceMeters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", task.Status);
        cmd.Parameters.AddWithValue("duration_sec", (object?)task.DurationSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failure_reason", (object?)task.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("forklift_id", (object?)task.ForkliftId ?? DBNull.Value);
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
        cmd.Parameters.AddWithValue("qty", task.Qty);
        cmd.Parameters.AddWithValue("worker_id", (object?)task.WorkerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("worker_name", (object?)task.WorkerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("worker_role", (object?)task.WorkerRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("template_code", (object?)task.TemplateCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("template_name", (object?)task.TemplateName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("task_basis_number", (object?)task.TaskBasisNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_zone", (object?)task.FromZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from_slot", (object?)task.FromSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_zone", (object?)task.ToZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_slot", (object?)task.ToSlot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("distance_meters", (object?)task.DistanceMeters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", task.Status);
        cmd.Parameters.AddWithValue("duration_sec", (object?)task.DurationSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failure_reason", (object?)task.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("forklift_id", (object?)task.ForkliftId ?? DBNull.Value);
    }

    // =====================================================
    // WORKERS & STATISTICS
    // =====================================================

    /// <summary>
    /// Обновляет таблицу workers на основе данных из tasks
    /// </summary>
    public async Task UpdateWorkersFromTasksAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Обновляем/вставляем работников с их статистикой
        var sql = @"
            WITH worker_roles AS (
                -- Определяем роль по наиболее частому типу задач (кроме Unknown)
                SELECT DISTINCT ON (worker_id) worker_id, worker_role
                FROM (
                    SELECT worker_id, worker_role, COUNT(*) as cnt
                    FROM tasks
                    WHERE worker_id IS NOT NULL AND worker_id != ''
                      AND worker_role != 'Unknown'
                    GROUP BY worker_id, worker_role
                    ORDER BY worker_id, cnt DESC
                ) sub
            )
            INSERT INTO workers (id, name, role, total_tasks, completed_tasks,
                avg_duration_sec, median_duration_sec, stddev_duration_sec,
                min_duration_sec, max_duration_sec, p95_duration_sec,
                tasks_per_hour, efficiency_score, first_task_at, last_task_at, updated_at)
            SELECT
                t.worker_id,
                MAX(t.worker_name) as name,
                COALESCE(wr.worker_role, 'Unknown') as role,
                COUNT(*) as total_tasks,
                COUNT(*) FILTER (WHERE t.status = 'Completed') as completed_tasks,
                AVG(t.duration_sec) FILTER (WHERE t.duration_sec IS NOT NULL) as avg_duration_sec,
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY t.duration_sec) FILTER (WHERE t.duration_sec IS NOT NULL) as median_duration_sec,
                STDDEV(t.duration_sec) FILTER (WHERE t.duration_sec IS NOT NULL) as stddev_duration_sec,
                MIN(t.duration_sec) FILTER (WHERE t.duration_sec IS NOT NULL) as min_duration_sec,
                MAX(t.duration_sec) FILTER (WHERE t.duration_sec IS NOT NULL) as max_duration_sec,
                PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY t.duration_sec) FILTER (WHERE t.duration_sec IS NOT NULL) as p95_duration_sec,
                -- Производительность: заданий в час
                CASE WHEN EXTRACT(EPOCH FROM (MAX(t.completed_at) - MIN(t.started_at))) > 0
                    THEN COUNT(*) FILTER (WHERE t.status = 'Completed') * 3600.0 /
                         NULLIF(EXTRACT(EPOCH FROM (MAX(t.completed_at) - MIN(t.started_at))), 0)
                    ELSE NULL
                END as tasks_per_hour,
                -- Эффективность: % завершённых от общего
                CASE WHEN COUNT(*) > 0
                    THEN 100.0 * COUNT(*) FILTER (WHERE t.status = 'Completed') / COUNT(*)
                    ELSE NULL
                END as efficiency_score,
                MIN(t.created_at) as first_task_at,
                MAX(t.created_at) as last_task_at,
                NOW() as updated_at
            FROM tasks t
            LEFT JOIN worker_roles wr ON t.worker_id = wr.worker_id
            WHERE t.worker_id IS NOT NULL AND t.worker_id != ''
            GROUP BY t.worker_id, wr.worker_role
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                role = EXCLUDED.role,
                total_tasks = EXCLUDED.total_tasks,
                completed_tasks = EXCLUDED.completed_tasks,
                avg_duration_sec = EXCLUDED.avg_duration_sec,
                median_duration_sec = EXCLUDED.median_duration_sec,
                stddev_duration_sec = EXCLUDED.stddev_duration_sec,
                min_duration_sec = EXCLUDED.min_duration_sec,
                max_duration_sec = EXCLUDED.max_duration_sec,
                p95_duration_sec = EXCLUDED.p95_duration_sec,
                tasks_per_hour = EXCLUDED.tasks_per_hour,
                efficiency_score = EXCLUDED.efficiency_score,
                first_task_at = EXCLUDED.first_task_at,
                last_task_at = EXCLUDED.last_task_at,
                updated_at = NOW();";

        var affected = await ExecuteNonQueryWithCountAsync(conn, sql, ct);
        _logger.LogInformation("Updated {Count} workers from tasks", affected);
    }

    /// <summary>
    /// Рассчитывает статистику маршрутов для карщиков с нормализацией
    /// </summary>
    public async Task UpdateRouteStatisticsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Если NormalizeBufferZone=true, все ячейки буфера считаются одной точкой
        var toSlotExpr = "to_slot";
        if (_routeOptions.NormalizeBufferZone)
        {
            // CASE WHEN to_zone = 'I' THEN 'BUFFER' ELSE to_slot END
            toSlotExpr = $"CASE WHEN to_zone = '{_routeOptions.BufferZoneCode}' THEN '{_routeOptions.BufferZoneConstant}' ELSE to_slot END";
            _logger.LogInformation("Route statistics: normalizing buffer zone '{Zone}' to '{Const}'",
                _routeOptions.BufferZoneCode, _routeOptions.BufferZoneConstant);
        }

        var sql = $@"
            WITH normalized_tasks AS (
                SELECT
                    from_slot,
                    {toSlotExpr} as to_slot,
                    from_zone,
                    to_zone,
                    duration_sec
                FROM tasks
                WHERE worker_role = 'Forklift'
                  AND from_slot IS NOT NULL AND from_slot != ''
                  AND to_slot IS NOT NULL AND to_slot != ''
                  AND duration_sec IS NOT NULL AND duration_sec > 0
            ),
            route_quartiles AS (
                SELECT
                    from_slot,
                    to_slot,
                    MAX(from_zone) as from_zone,
                    MAX(to_zone) as to_zone,
                    COUNT(*) as total_trips,
                    PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY duration_sec) as q1,
                    PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY duration_sec) as q3,
                    PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY duration_sec) as median_duration,
                    PERCENTILE_CONT(0.05) WITHIN GROUP (ORDER BY duration_sec) as p5_duration,
                    PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY duration_sec) as p95_duration,
                    AVG(duration_sec) as avg_all,
                    MIN(duration_sec) as min_all,
                    MAX(duration_sec) as max_all,
                    STDDEV(duration_sec) as stddev_all
                FROM normalized_tasks
                GROUP BY from_slot, to_slot
            )
            INSERT INTO route_statistics (
                from_zone, from_slot, to_zone, to_slot,
                total_trips, normalized_trips,
                avg_duration_sec, median_duration_sec, stddev_duration_sec,
                min_duration_sec, max_duration_sec, p5_duration_sec, p95_duration_sec,
                lower_bound_sec, upper_bound_sec, outliers_removed,
                predicted_duration_sec, confidence_level, updated_at)
            SELECT
                from_zone,
                from_slot,
                to_zone,
                to_slot,
                total_trips,
                total_trips as normalized_trips,
                avg_all as avg_duration,
                median_duration,
                COALESCE(stddev_all, 0) as stddev_duration,
                min_all as min_duration,
                max_all as max_duration,
                p5_duration,
                p95_duration,
                GREATEST(q1 - 1.5 * COALESCE(q3 - q1, 0), 0) as lower_bound,
                q3 + 1.5 * COALESCE(q3 - q1, 0) as upper_bound,
                0 as outliers_removed,
                median_duration as predicted_duration,
                LEAST(1.0, total_trips::numeric / 100.0) *
                    CASE WHEN stddev_all > 0 AND avg_all > 0
                         THEN LEAST(1.0, avg_all / (stddev_all * 2))
                         ELSE 0.5
                    END as confidence_level,
                NOW() as updated_at
            FROM route_quartiles
            ON CONFLICT (from_slot, to_slot) DO UPDATE SET
                from_zone = EXCLUDED.from_zone,
                to_zone = EXCLUDED.to_zone,
                total_trips = EXCLUDED.total_trips,
                normalized_trips = EXCLUDED.normalized_trips,
                avg_duration_sec = EXCLUDED.avg_duration_sec,
                median_duration_sec = EXCLUDED.median_duration_sec,
                stddev_duration_sec = EXCLUDED.stddev_duration_sec,
                min_duration_sec = EXCLUDED.min_duration_sec,
                max_duration_sec = EXCLUDED.max_duration_sec,
                p5_duration_sec = EXCLUDED.p5_duration_sec,
                p95_duration_sec = EXCLUDED.p95_duration_sec,
                lower_bound_sec = EXCLUDED.lower_bound_sec,
                upper_bound_sec = EXCLUDED.upper_bound_sec,
                outliers_removed = EXCLUDED.outliers_removed,
                predicted_duration_sec = EXCLUDED.predicted_duration_sec,
                confidence_level = EXCLUDED.confidence_level,
                updated_at = NOW();";

        var affected = await ExecuteNonQueryWithCountAsync(conn, sql, ct);
        _logger.LogInformation("Updated {Count} route statistics", affected);
    }

    /// <summary>
    /// Рассчитывает статистику пикер + товар
    /// </summary>
    public async Task UpdatePickerProductStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // IQR нормализация по duration_sec для отсечения выбросов
        var sql = @"
            WITH base_tasks AS (
                SELECT
                    worker_id,
                    worker_name,
                    COALESCE(NULLIF(product_type, ''), 'UNKNOWN') as product_sku,
                    duration_sec,
                    COALESCE(NULLIF(qty, 0), 1) as qty,
                    COALESCE(weight_kg, 0) as weight_kg,
                    created_at
                FROM tasks
                WHERE worker_role = 'Picker'
                  AND worker_id IS NOT NULL AND worker_id != ''
                  AND duration_sec IS NOT NULL AND duration_sec > 0
            ),
            -- Рассчитываем квартили для каждой комбинации пикер+товар
            quartiles AS (
                SELECT
                    worker_id,
                    product_sku,
                    PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY duration_sec) as q1,
                    PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY duration_sec) as q3,
                    COUNT(*) as total_raw
                FROM base_tasks
                GROUP BY worker_id, product_sku
                HAVING COUNT(*) >= 3
            ),
            -- Фильтруем выбросы по IQR
            normalized_tasks AS (
                SELECT
                    t.worker_id,
                    t.worker_name,
                    t.product_sku,
                    t.duration_sec,
                    t.qty,
                    t.weight_kg,
                    t.created_at,
                    q.q1,
                    q.q3,
                    q.total_raw
                FROM base_tasks t
                JOIN quartiles q ON t.worker_id = q.worker_id AND t.product_sku = q.product_sku
                WHERE t.duration_sec >= GREATEST(q.q1 - 1.5 * (q.q3 - q.q1), 1)
                  AND t.duration_sec <= q.q3 + 1.5 * (q.q3 - q.q1)
            )
            INSERT INTO picker_product_stats (
                picker_id, picker_name, product_sku, product_name,
                total_lines, total_qty, total_weight_kg,
                avg_duration_sec, median_duration_sec, stddev_duration_sec,
                min_duration_sec, max_duration_sec,
                lines_per_min, qty_per_min, kg_per_min, confidence,
                first_task_at, last_task_at, updated_at)
            SELECT
                worker_id as picker_id,
                MAX(worker_name) as picker_name,
                product_sku,
                product_sku as product_name,
                COUNT(*) as total_lines,
                SUM(qty) as total_qty,
                SUM(weight_kg) as total_weight_kg,
                AVG(duration_sec) as avg_duration_sec,
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY duration_sec) as median_duration_sec,
                COALESCE(STDDEV(duration_sec), 0) as stddev_duration_sec,
                MIN(duration_sec) as min_duration_sec,
                MAX(duration_sec) as max_duration_sec,
                -- строк в минуту (нормализованное)
                CASE WHEN AVG(duration_sec) > 0
                    THEN 60.0 / AVG(duration_sec)
                    ELSE NULL
                END as lines_per_min,
                -- единиц в минуту
                CASE WHEN AVG(duration_sec) > 0
                    THEN 60.0 * AVG(qty) / AVG(duration_sec)
                    ELSE NULL
                END as qty_per_min,
                -- кг в минуту
                CASE WHEN AVG(duration_sec) > 0 AND SUM(weight_kg) > 0
                    THEN 60.0 * SUM(weight_kg) / SUM(duration_sec)
                    ELSE NULL
                END as kg_per_min,
                -- уверенность: больше данных + меньше разброс = выше
                LEAST(1.0, COUNT(*)::numeric / 50.0) *
                    CASE WHEN STDDEV(duration_sec) > 0 AND AVG(duration_sec) > 0
                         THEN LEAST(1.0, AVG(duration_sec) / (STDDEV(duration_sec) * 2))
                         ELSE 0.5
                    END as confidence,
                MIN(created_at) as first_task_at,
                MAX(created_at) as last_task_at,
                NOW() as updated_at
            FROM normalized_tasks
            GROUP BY worker_id, product_sku
            HAVING COUNT(*) >= 3
            ON CONFLICT (picker_id, product_sku) DO UPDATE SET
                picker_name = EXCLUDED.picker_name,
                product_name = EXCLUDED.product_name,
                total_lines = EXCLUDED.total_lines,
                total_qty = EXCLUDED.total_qty,
                total_weight_kg = EXCLUDED.total_weight_kg,
                avg_duration_sec = EXCLUDED.avg_duration_sec,
                median_duration_sec = EXCLUDED.median_duration_sec,
                stddev_duration_sec = EXCLUDED.stddev_duration_sec,
                min_duration_sec = EXCLUDED.min_duration_sec,
                max_duration_sec = EXCLUDED.max_duration_sec,
                lines_per_min = EXCLUDED.lines_per_min,
                qty_per_min = EXCLUDED.qty_per_min,
                kg_per_min = EXCLUDED.kg_per_min,
                confidence = EXCLUDED.confidence,
                first_task_at = EXCLUDED.first_task_at,
                last_task_at = EXCLUDED.last_task_at,
                updated_at = NOW();";

        var affected = await ExecuteNonQueryWithCountAsync(conn, sql, ct);
        _logger.LogInformation("Updated {Count} picker-product statistics", affected);
    }

    /// <summary>
    /// Получает статистику пикер + товар
    /// </summary>
    public async Task<List<PickerProductStats>> GetPickerProductStatsAsync(
        string? pickerId = null, string? productSku = null, int minLines = 3, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = @"
            SELECT picker_id, picker_name, product_sku, product_name,
                   total_lines, total_qty, total_weight_kg,
                   avg_duration_sec, median_duration_sec, stddev_duration_sec,
                   min_duration_sec, max_duration_sec,
                   lines_per_min, qty_per_min, kg_per_min, confidence,
                   first_task_at, last_task_at, updated_at
            FROM picker_product_stats
            WHERE (@picker_id IS NULL OR picker_id = @picker_id)
              AND (@product_sku IS NULL OR product_sku = @product_sku)
              AND total_lines >= @min_lines
            ORDER BY total_lines DESC";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("picker_id", (object?)pickerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("product_sku", (object?)productSku ?? DBNull.Value);
        cmd.Parameters.AddWithValue("min_lines", minLines);

        var stats = new List<PickerProductStats>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            stats.Add(new PickerProductStats
            {
                PickerId = reader.GetString(0),
                PickerName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ProductSku = reader.GetString(2),
                ProductName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                TotalLines = reader.GetInt32(4),
                TotalQty = reader.GetDecimal(5),
                TotalWeightKg = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                AvgDurationSec = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                MedianDurationSec = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                StdDevDurationSec = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                MinDurationSec = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                MaxDurationSec = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                LinesPerMin = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                QtyPerMin = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                KgPerMin = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                Confidence = reader.IsDBNull(15) ? null : reader.GetDecimal(15),
                FirstTaskAt = reader.IsDBNull(16) ? DateTime.MinValue : reader.GetDateTime(16),
                LastTaskAt = reader.IsDBNull(17) ? DateTime.MinValue : reader.GetDateTime(17),
                UpdatedAt = reader.GetDateTime(18)
            });
        }

        return stats;
    }

    /// <summary>
    /// Получает список работников с их статистикой
    /// </summary>
    public async Task<List<WorkerRecord>> GetWorkersAsync(string? role = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = @"
            SELECT id, name, role, total_tasks, completed_tasks,
                   avg_duration_sec, median_duration_sec, stddev_duration_sec,
                   min_duration_sec, max_duration_sec, p95_duration_sec,
                   tasks_per_hour, efficiency_score, first_task_at, last_task_at, updated_at
            FROM workers
            WHERE (@role IS NULL OR role = @role)
            ORDER BY total_tasks DESC";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("role", (object?)role ?? DBNull.Value);

        var workers = new List<WorkerRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            workers.Add(new WorkerRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Role = reader.GetString(2),
                TotalTasks = reader.GetInt32(3),
                CompletedTasks = reader.GetInt32(4),
                AvgDurationSec = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                MedianDurationSec = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                StdDevDurationSec = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                MinDurationSec = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                MaxDurationSec = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                Percentile95DurationSec = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                TasksPerHour = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                EfficiencyScore = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                FirstTaskAt = reader.IsDBNull(13) ? DateTime.MinValue : reader.GetDateTime(13),
                LastTaskAt = reader.IsDBNull(14) ? DateTime.MinValue : reader.GetDateTime(14),
                UpdatedAt = reader.GetDateTime(15)
            });
        }

        return workers;
    }

    /// <summary>
    /// Получает статистику маршрутов
    /// </summary>
    public async Task<List<RouteStatistics>> GetRouteStatisticsAsync(
        string? fromZone = null, string? toZone = null, int minTrips = 3, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = @"
            SELECT from_zone, from_slot, to_zone, to_slot,
                   total_trips, normalized_trips,
                   avg_duration_sec, median_duration_sec, stddev_duration_sec,
                   min_duration_sec, max_duration_sec, p5_duration_sec, p95_duration_sec,
                   lower_bound_sec, upper_bound_sec, outliers_removed,
                   predicted_duration_sec, confidence_level, updated_at
            FROM route_statistics
            WHERE (@from_zone IS NULL OR from_zone = @from_zone)
              AND (@to_zone IS NULL OR to_zone = @to_zone)
              AND total_trips >= @min_trips
            ORDER BY total_trips DESC";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from_zone", (object?)fromZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to_zone", (object?)toZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("min_trips", minTrips);

        var routes = new List<RouteStatistics>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            routes.Add(new RouteStatistics
            {
                FromZone = reader.IsDBNull(0) ? "" : reader.GetString(0),
                FromSlot = reader.GetString(1),
                ToZone = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ToSlot = reader.GetString(3),
                TotalTrips = reader.GetInt32(4),
                NormalizedTrips = reader.GetInt32(5),
                AvgDurationSec = reader.GetDecimal(6),
                MedianDurationSec = reader.GetDecimal(7),
                StdDevDurationSec = reader.GetDecimal(8),
                MinDurationSec = reader.GetDecimal(9),
                MaxDurationSec = reader.GetDecimal(10),
                Percentile5DurationSec = reader.GetDecimal(11),
                Percentile95DurationSec = reader.GetDecimal(12),
                LowerBoundSec = reader.GetDecimal(13),
                UpperBoundSec = reader.GetDecimal(14),
                OutliersRemoved = reader.GetInt32(15),
                PredictedDurationSec = reader.GetDecimal(16),
                ConfidenceLevel = reader.GetDecimal(17),
                UpdatedAt = reader.GetDateTime(18)
            });
        }

        return routes;
    }

    /// <summary>
    /// Прогнозирует время выполнения маршрута
    /// </summary>
    public async Task<decimal?> PredictRouteDurationAsync(string fromSlot, string toSlot, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = @"
            SELECT predicted_duration_sec
            FROM route_statistics
            WHERE from_slot = @from_slot AND to_slot = @to_slot";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from_slot", fromSlot);
        cmd.Parameters.AddWithValue("to_slot", toSlot);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is decimal d ? d : null;
    }

    public async ValueTask DisposeAsync()
    {
        // NpgsqlConnection is disposed per-operation, nothing to clean up
        await Task.CompletedTask;
    }
}
