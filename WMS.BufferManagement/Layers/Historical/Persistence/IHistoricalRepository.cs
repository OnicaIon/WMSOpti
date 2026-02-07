using WMS.BufferManagement.Layers.Historical.Persistence.Models;

namespace WMS.BufferManagement.Layers.Historical.Persistence;

/// <summary>
/// Репозиторий для работы с историческими данными
/// </summary>
public interface IHistoricalRepository
{
    // === Инициализация ===

    /// <summary>
    /// Инициализация схемы БД (создание таблиц, hypertables, индексов)
    /// </summary>
    Task InitializeSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверка доступности БД
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    // === Задания (Tasks) ===

    /// <summary>
    /// Сохранить запись о задании
    /// </summary>
    Task SaveTaskAsync(TaskRecord task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохранить пакет записей о заданиях
    /// </summary>
    Task SaveTasksBatchAsync(IEnumerable<TaskRecord> tasks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Очистить все задания (TRUNCATE)
    /// </summary>
    Task TruncateTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить количество заданий
    /// </summary>
    Task<long> GetTasksCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить статистику заданий по карщикам
    /// </summary>
    Task<IReadOnlyList<ForkliftTaskStats>> GetForkliftStatsAsync(
        DateTime fromTime,
        DateTime toTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить топ медленных маршрутов
    /// </summary>
    Task<IReadOnlyList<RouteStats>> GetSlowestRoutesAsync(
        int limit = 20,
        int lastDays = 7,
        CancellationToken cancellationToken = default);

    // === Метрики сборщиков (Picker Metrics) ===

    /// <summary>
    /// Сохранить метрику сборщика
    /// </summary>
    Task SavePickerMetricAsync(PickerMetric metric, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохранить пакет метрик сборщиков
    /// </summary>
    Task SavePickerMetricsBatchAsync(IEnumerable<PickerMetric> metrics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить почасовую статистику сборщика
    /// </summary>
    Task<IReadOnlyList<PickerHourlyStats>> GetPickerHourlyStatsAsync(
        string pickerId,
        DateTime fromTime,
        DateTime toTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить среднюю скорость сборщиков по часам дня (для ML)
    /// </summary>
    Task<IReadOnlyList<PickerHourlyPattern>> GetPickerHourlyPatternsAsync(
        int lastDays = 30,
        CancellationToken cancellationToken = default);

    // === Снимки буфера (Buffer Snapshots) ===

    /// <summary>
    /// Сохранить снимок состояния буфера
    /// </summary>
    Task SaveBufferSnapshotAsync(BufferSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить статистику буфера за период
    /// </summary>
    Task<BufferPeriodStats?> GetBufferStatsAsync(
        DateTime fromTime,
        DateTime toTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить временной ряд уровня буфера
    /// </summary>
    Task<IReadOnlyList<BufferSnapshot>> GetBufferTimeSeriesAsync(
        DateTime fromTime,
        DateTime toTime,
        TimeSpan? bucket = null,
        CancellationToken cancellationToken = default);

    // === Products (справочник) ===

    /// <summary>
    /// Сохранить или обновить продукты (UPSERT)
    /// </summary>
    Task SaveProductsBatchAsync(IEnumerable<ProductRecord> products, CancellationToken cancellationToken = default);

    // === ML Data Export ===

    /// <summary>
    /// Экспорт данных для обучения модели скорости сборщиков
    /// </summary>
    Task<IReadOnlyList<PickerSpeedTrainingRow>> ExportPickerSpeedTrainingDataAsync(
        int lastDays = 30,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Экспорт данных для обучения модели прогноза спроса
    /// </summary>
    Task<IReadOnlyList<DemandTrainingRow>> ExportDemandTrainingDataAsync(
        int lastDays = 30,
        CancellationToken cancellationToken = default);

    // === Workers & Route Statistics ===

    /// <summary>
    /// Обновляет таблицу workers на основе данных из tasks
    /// </summary>
    Task UpdateWorkersFromTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Рассчитывает статистику маршрутов для карщиков с нормализацией (IQR)
    /// </summary>
    Task UpdateRouteStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Получает список работников с их статистикой
    /// </summary>
    Task<List<WorkerRecord>> GetWorkersAsync(string? role = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Получает статистику маршрутов
    /// </summary>
    Task<List<RouteStatistics>> GetRouteStatisticsAsync(
        string? fromZone = null, string? toZone = null, int minTrips = 3,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Прогнозирует время выполнения маршрута
    /// </summary>
    Task<decimal?> PredictRouteDurationAsync(string fromSlot, string toSlot, CancellationToken cancellationToken = default);

    // === Picker + Product Statistics ===

    /// <summary>
    /// Рассчитывает статистику пикер + товар (строк/мин по товару)
    /// Вызывать периодически (не при каждом импорте!)
    /// </summary>
    Task UpdatePickerProductStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Получает статистику пикер + товар
    /// </summary>
    Task<List<PickerProductStats>> GetPickerProductStatsAsync(
        string? pickerId = null, string? productSku = null, int minLines = 3,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Среднее время перехода между палетами per worker из исторических данных.
    /// gap = next.started_at - prev.completed_at (в пределах одного дня, gap > 0 и &lt; 10 мин).
    /// </summary>
    Task<List<WorkerTransitionStats>> GetWorkerTransitionStatsAsync(
        string? role = null, int minTransitions = 5,
        CancellationToken cancellationToken = default);

    // === Zones & Cells ===

    /// <summary>
    /// Сохранить или обновить зоны (UPSERT)
    /// </summary>
    /// <param name="zones">Список зон из WMS</param>
    /// <param name="bufferZoneCodes">Коды зон которые считаются буферными</param>
    Task UpsertZonesAsync(
        IEnumerable<WMS.BufferManagement.Infrastructure.WmsIntegration.WmsZoneRecord> zones,
        HashSet<string> bufferZoneCodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохранить или обновить ячейки (UPSERT)
    /// </summary>
    Task UpsertCellsAsync(
        IEnumerable<WMS.BufferManagement.Infrastructure.WmsIntegration.WmsCellRecord> cells,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить количество буферных ячеек (is_buffer=true)
    /// </summary>
    Task<int> GetBufferCellsCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить ёмкость буфера (количество ячеек в буферных зонах)
    /// </summary>
    Task<int> GetBufferCapacityAsync(CancellationToken cancellationToken = default);

    // === Backtest Persistence ===

    /// <summary>
    /// Сохранить полный результат бэктеста в БД (все 6 таблиц).
    /// При повторном запуске той же волны — удаляет старые данные (CASCADE) и записывает заново.
    /// </summary>
    Task<Guid> SaveBacktestResultAsync(
        Services.Backtesting.BacktestResult result,
        Services.Backtesting.SimulationDecisionContext decisions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить список бэктестов (для UI). Фильтр по волне опционален.
    /// </summary>
    Task<List<Services.Backtesting.BacktestRunRecord>> GetBacktestRunsAsync(
        string? waveNumber = null,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить события для диаграммы Ганта по run_id.
    /// timelineType: "fact", "optimized" или null (оба).
    /// </summary>
    Task<List<Services.Backtesting.ScheduleEventRecord>> GetScheduleEventsAsync(
        Guid runId,
        string? timelineType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить лог решений оптимизатора по run_id.
    /// </summary>
    Task<List<Services.Backtesting.DecisionLogRecord>> GetDecisionLogAsync(
        Guid runId,
        CancellationToken cancellationToken = default);
}

// === Дополнительные модели для статистики ===

public class ForkliftTaskStats
{
    public string ForkliftId { get; set; } = string.Empty;
    public int TotalTasks { get; set; }
    public int CompletedTasks { get; set; }
    public int FailedTasks { get; set; }
    public decimal AvgDurationSec { get; set; }
    public decimal TotalDistanceMeters { get; set; }
}

public class RouteStats
{
    public string FromZone { get; set; } = string.Empty;
    public string ToZone { get; set; } = string.Empty;
    public decimal AvgDurationSec { get; set; }
    public int TaskCount { get; set; }
}

public class PickerHourlyPattern
{
    public string PickerId { get; set; } = string.Empty;
    public int HourOfDay { get; set; }
    public decimal AvgConsumptionRate { get; set; }
    public decimal AvgEfficiency { get; set; }
    public int SampleCount { get; set; }
}

// === ML Training Data ===

public class PickerSpeedTrainingRow
{
    public string PickerId { get; set; } = string.Empty;
    public int HourOfDay { get; set; }
    public int DayOfWeek { get; set; }
    public float AvgSpeedLast7Days { get; set; }
    public float AvgSpeedSameHour { get; set; }
    public float Speed { get; set; }  // Target
}

public class DemandTrainingRow
{
    public int HourOfDay { get; set; }
    public int DayOfWeek { get; set; }
    public int ActivePickers { get; set; }
    public float AvgPickerSpeed { get; set; }
    public float BufferLevel { get; set; }
    public float DemandNext15Min { get; set; }  // Target
}
