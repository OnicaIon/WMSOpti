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
