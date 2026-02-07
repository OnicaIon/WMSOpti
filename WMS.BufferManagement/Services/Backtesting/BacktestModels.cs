using System.Text.Json;
using System.Text.Json.Serialization;

namespace WMS.BufferManagement.Services.Backtesting;

/// <summary>
/// Конвертер для DateTime? — пустая строка → null
/// </summary>
public class NullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;
            if (DateTime.TryParse(str, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return null;
        }
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString("O"));
        else
            writer.WriteNullValue();
    }
}

// ============================================================================
// Ответ от 1С — данные волны
// ============================================================================

/// <summary>
/// Ответ от 1С HTTP-сервиса /wave-tasks
/// </summary>
public class WaveTasksResponse
{
    [JsonPropertyName("waveNumber")]
    public string WaveNumber { get; set; } = string.Empty;

    [JsonPropertyName("waveDate")]
    public DateTime WaveDate { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("replenishmentTasks")]
    public List<WaveTaskGroup> ReplenishmentTasks { get; set; } = new();

    [JsonPropertyName("distributionTasks")]
    public List<WaveTaskGroup> DistributionTasks { get; set; } = new();
}

/// <summary>
/// Группа заданий одного исполнителя (rtWMSProductSelection)
/// </summary>
public class WaveTaskGroup
{
    [JsonPropertyName("taskRef")]
    public string TaskRef { get; set; } = string.Empty;

    [JsonPropertyName("taskNumber")]
    public string TaskNumber { get; set; } = string.Empty;

    [JsonPropertyName("assigneeCode")]
    public string AssigneeCode { get; set; } = string.Empty;

    [JsonPropertyName("assigneeName")]
    public string AssigneeName { get; set; } = string.Empty;

    [JsonPropertyName("templateCode")]
    public string TemplateCode { get; set; } = string.Empty;

    [JsonPropertyName("executionStatus")]
    public string ExecutionStatus { get; set; } = string.Empty;

    [JsonPropertyName("executionDate")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? ExecutionDate { get; set; }

    [JsonPropertyName("actions")]
    public List<WaveTaskAction> Actions { get; set; } = new();

    /// <summary>Ссылка на связанную replenishment-задачу (для distribution)</summary>
    [JsonPropertyName("prevTaskRef")]
    public string PrevTaskRef { get; set; } = string.Empty;
}

/// <summary>
/// Одно действие в задании (строка из PlanActions / sgTaskAction)
/// </summary>
public class WaveTaskAction
{
    [JsonPropertyName("storageBin")]
    public string StorageBin { get; set; } = string.Empty;

    [JsonPropertyName("allocationBin")]
    public string AllocationBin { get; set; } = string.Empty;

    [JsonPropertyName("productCode")]
    public string ProductCode { get; set; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("weightKg")]
    public decimal WeightKg { get; set; }

    [JsonPropertyName("qtyPlan")]
    public int QtyPlan { get; set; }

    [JsonPropertyName("qtyFact")]
    public int QtyFact { get; set; }

    [JsonPropertyName("completedAt")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("startedAt")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("durationSec")]
    public double? DurationSec { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}

// ============================================================================
// Внутренние модели для расчётов
// ============================================================================

/// <summary>
/// Фактическая хронология выполнения волны
/// </summary>
public class ActualTimeline
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    /// <summary>Полное время от-до (включая ночи, перерывы)</summary>
    public TimeSpan WallClockDuration => EndTime - StartTime;
    /// <summary>Активное время (только когда хотя бы 1 работник работал)</summary>
    public TimeSpan ActiveDuration { get; set; }
    public int TotalActions { get; set; }
    public List<WorkerTimeline> WorkerTimelines { get; set; } = new();
}

/// <summary>
/// Хронология одного работника
/// </summary>
public class WorkerTimeline
{
    public string WorkerCode { get; set; } = string.Empty;
    public string WorkerName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    /// <summary>Сумма длительностей всех действий работника</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Actions.Sum(a => a.DurationSec));
    public int TaskCount { get; set; }
    public List<ActionTiming> Actions { get; set; } = new();
}

/// <summary>
/// Хронометраж одного действия
/// </summary>
public class ActionTiming
{
    public string FromBin { get; set; } = string.Empty;
    public string ToBin { get; set; } = string.Empty;
    public string FromZone { get; set; } = string.Empty;
    public string ToZone { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal WeightKg { get; set; }
    public int Qty { get; set; }
    public double DurationSec { get; set; }
    public string WorkerCode { get; set; } = string.Empty;
    /// <summary>Replenishment / Distribution</summary>
    public string TaskType { get; set; } = string.Empty;
    /// <summary>UUID задачи rtWMSProductSelection (для связи repl→dist)</summary>
    public string TaskGroupRef { get; set; } = string.Empty;
    /// <summary>Ссылка на replenishment-задачу (для distribution)</summary>
    public string PrevTaskRef { get; set; } = string.Empty;
}

/// <summary>
/// Оптимизированный план назначения заданий
/// </summary>
public class OptimizedPlan
{
    public Dictionary<string, List<ActionTiming>> WorkerAssignments { get; set; } = new();
    public bool IsOptimal { get; set; }
    public double SolverObjective { get; set; }
    public TimeSpan SolverTime { get; set; }
}

/// <summary>
/// Симулированная хронология оптимизированного выполнения
/// </summary>
public class SimulatedTimeline
{
    public TimeSpan TotalDuration { get; set; }
    public double WaveMeanDurationSec { get; set; }
    public List<SimulatedWorkerTimeline> WorkerTimelines { get; set; } = new();
}

/// <summary>
/// Симулированная хронология одного работника
/// </summary>
public class SimulatedWorkerTimeline
{
    public string WorkerCode { get; set; } = string.Empty;
    public string WorkerName { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public int TaskCount { get; set; }
    public List<SimulatedAction> Actions { get; set; } = new();
}

/// <summary>
/// Симулированное действие с прогнозным временем
/// </summary>
public class SimulatedAction
{
    public string FromBin { get; set; } = string.Empty;
    public string ToBin { get; set; } = string.Empty;
    public string FromZone { get; set; } = string.Empty;
    public string ToZone { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public decimal WeightKg { get; set; }
    public double EstimatedDurationSec { get; set; }
    public string DurationSource { get; set; } = string.Empty; // "route_stats", "picker_product", "default"
}

// ============================================================================
// Разбивка по дням
// ============================================================================

/// <summary>
/// Разбивка бэктеста по одному календарному дню
/// </summary>
public class DayBreakdown
{
    public DateTime Date { get; set; }
    public int Workers { get; set; }
    public int ForkliftWorkers { get; set; }
    public int PickerWorkers { get; set; }
    public int ReplActions { get; set; }
    public int DistActions { get; set; }
    public int TotalActions { get; set; }
    /// <summary>Фактическое активное время (merged intervals) за день</summary>
    public TimeSpan ActualActiveDuration { get; set; }
    /// <summary>Оптимизированный makespan за день</summary>
    public TimeSpan OptimizedMakespan { get; set; }
    public double ImprovementPercent { get; set; }

    // Палеты (task groups) — факт vs оптимизация
    public int OriginalReplGroups { get; set; }
    public int OriginalDistGroups { get; set; }
    public int OptimizedReplGroups { get; set; }
    public int OptimizedDistGroups { get; set; }
    /// <summary>Разница: опт палет - факт палет</summary>
    public int AdditionalPallets { get; set; }

    // Буфер
    public int BufferLevelStart { get; set; }
    public int BufferLevelEnd { get; set; }
}

// ============================================================================
// Результат бэктеста
// ============================================================================

/// <summary>
/// Полный результат бэктестирования волны
/// </summary>
public class BacktestResult
{
    // Метаданные волны
    public string WaveNumber { get; set; } = string.Empty;
    public DateTime WaveDate { get; set; }
    public string WaveStatus { get; set; } = string.Empty;

    // Объёмы
    public int TotalReplenishmentTasks { get; set; }
    public int TotalDistributionTasks { get; set; }
    public int TotalActions { get; set; }
    public int UniqueWorkers { get; set; }

    // Факт
    public DateTime ActualStartTime { get; set; }
    public DateTime ActualEndTime { get; set; }
    /// <summary>Полное время от-до (включая ночи, перерывы)</summary>
    public TimeSpan ActualWallClockDuration { get; set; }
    /// <summary>Активное время (только когда кто-то работал)</summary>
    public TimeSpan ActualActiveDuration { get; set; }

    // Оптимизация
    public TimeSpan OptimizedDuration { get; set; }
    public double ImprovementPercent { get; set; }
    public TimeSpan ImprovementTime { get; set; }
    public bool OptimizerIsOptimal { get; set; }

    // Разбивка по дням
    public List<DayBreakdown> DayBreakdowns { get; set; } = new();

    // Разбивка по работникам
    public List<WorkerBreakdown> WorkerBreakdowns { get; set; } = new();

    // Детали заданий (для подробного отчёта)
    public List<TaskDetail> TaskDetails { get; set; } = new();

    // Метаданные анализа
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    public int ActualDurationsUsed { get; set; }
    public int RouteStatsUsed { get; set; }
    public int PickerStatsUsed { get; set; }
    public int DefaultEstimatesUsed { get; set; }
    /// <summary>Среднее фактическое время действия (для default fallback)</summary>
    public double WaveMeanDurationSec { get; set; }

    // Кросс-дневная оптимизация
    /// <summary>Всего палет (task groups) в волне</summary>
    public int TotalReplGroups { get; set; }
    public int TotalDistGroups { get; set; }
    /// <summary>Факт: за сколько дней выполнена волна</summary>
    public int OriginalWaveDays { get; set; }
    /// <summary>Опт: за сколько дней можно выполнить</summary>
    public int OptimizedWaveDays { get; set; }
    public int DaysSaved { get; set; }
    /// <summary>Ёмкость буфера (из конфига)</summary>
    public int BufferCapacity { get; set; }

    // Время перехода между палетами (рассчитано из данных волны)
    /// <summary>Среднее время перехода пикера между палетами (сек)</summary>
    public double PickerTransitionSec { get; set; }
    /// <summary>Среднее время перехода форклифта между палетами (сек)</summary>
    public double ForkliftTransitionSec { get; set; }
    /// <summary>Количество переходов пикеров (для статистики)</summary>
    public int PickerTransitionCount { get; set; }
    /// <summary>Количество переходов форклифтов (для статистики)</summary>
    public int ForkliftTransitionCount { get; set; }
}

/// <summary>
/// Разбивка результата по работнику
/// </summary>
public class WorkerBreakdown
{
    public string WorkerCode { get; set; } = string.Empty;
    public string WorkerName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int ActualTasks { get; set; }
    public int OptimizedTasks { get; set; }
    public TimeSpan ActualDuration { get; set; }
    public TimeSpan OptimizedDuration { get; set; }
    public double ImprovementPercent { get; set; }
}

/// <summary>
/// Детали одной палеты (task group) для подробного отчёта.
/// Одна строка = одна палета (rtWMSProductSelection), НЕ отдельное действие.
/// </summary>
public class TaskDetail
{
    public string TaskNumber { get; set; } = string.Empty;
    public string TaskRef { get; set; } = string.Empty;
    public string PrevTaskRef { get; set; } = string.Empty;
    public string WorkerCode { get; set; } = string.Empty;
    public string WorkerName { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty; // Replenishment / Distribution
    public string Route { get; set; } = string.Empty; // "A→I"
    public string FromBin { get; set; } = string.Empty; // Откуда (ячейка)
    public string ToBin { get; set; } = string.Empty;   // Куда (ячейка)
    public int ActionCount { get; set; }
    public decimal TotalWeightKg { get; set; }
    public int TotalQty { get; set; }

    // Фактическое время палеты (от первого действия до последнего)
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? ActualDurationSec { get; set; }

    // Оптимизация
    public double OptimizedDurationSec { get; set; }
    public string? OptimizedWorkerCode { get; set; }

    // Связанная задача (для dist: кто принёс (repl); для repl: кто забрал (dist))
    public string? LinkedWorkerCode { get; set; }
    public string? LinkedWorkerName { get; set; }
    public double? LinkedActualDurationSec { get; set; }
    public string? LinkedOptWorkerCode { get; set; }
    public double? LinkedOptDurationSec { get; set; }
}
