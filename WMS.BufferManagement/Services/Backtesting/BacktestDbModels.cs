using System.Text.Json.Serialization;

namespace WMS.BufferManagement.Services.Backtesting;

// ============================================================================
// Модели для сохранения результатов бэктеста в БД
// ============================================================================

/// <summary>
/// Контейнер для сбора решений оптимизатора и событий Ганта во время симуляции.
/// Передаётся в SimulateDay/OptimizeCrossDay, заполняется как side-effect.
/// </summary>
public class SimulationDecisionContext
{
    /// <summary>События для диаграммы Ганта (факт + оптимизация)</summary>
    public List<ScheduleEventRecord> ScheduleEvents { get; } = new();

    /// <summary>Лог решений оптимизатора (ПОЧЕМУ)</summary>
    public List<DecisionLogRecord> Decisions { get; } = new();

    /// <summary>Счётчик порядка назначений в симуляции</summary>
    public int SequenceCounter { get; set; }

    /// <summary>Счётчик решений</summary>
    public int DecisionCounter { get; set; }
}

/// <summary>
/// Событие для диаграммы Ганта — одна палета (task group) на одном работнике.
/// Строки с timeline_type="fact" имеют реальные timestamps из 1С,
/// строки с timeline_type="optimized" — симулированные offset от начала дня.
/// </summary>
public class ScheduleEventRecord
{
    /// <summary>"fact" или "optimized"</summary>
    public string TimelineType { get; set; } = string.Empty;

    public string WorkerCode { get; set; } = string.Empty;
    public string? WorkerName { get; set; }
    public string? WorkerRole { get; set; }

    /// <summary>UUID задачи rtWMSProductSelection</summary>
    public string? TaskRef { get; set; }
    /// <summary>Replenishment / Distribution</summary>
    public string? TaskType { get; set; }

    public DateTime DayDate { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double DurationSec { get; set; }

    public string? FromBin { get; set; }
    public string? ToBin { get; set; }
    public string? FromZone { get; set; }
    public string? ToZone { get; set; }

    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public decimal WeightKg { get; set; }
    public int Qty { get; set; }

    /// <summary>Порядковый номер назначения в симуляции (только для optimized)</summary>
    public int SequenceNumber { get; set; }
    /// <summary>Уровень буфера при назначении (только для optimized)</summary>
    public int BufferLevel { get; set; }
    /// <summary>Источник оценки длительности: actual/route_stats/picker_product/default</summary>
    public string? DurationSource { get; set; }
    /// <summary>Время перехода перед этой задачей (сек)</summary>
    public double TransitionSec { get; set; }
}

/// <summary>
/// Лог одного решения оптимизатора — ПОЧЕМУ назначена именно эта палета
/// именно этому работнику, или ПОЧЕМУ не удалось назначить.
/// </summary>
public class DecisionLogRecord
{
    /// <summary>Порядковый номер решения в рамках run</summary>
    public int DecisionSeq { get; set; }

    public DateTime DayDate { get; set; }

    /// <summary>assign_repl / assign_dist / skip_repl / skip_dist</summary>
    public string DecisionType { get; set; } = string.Empty;

    // --- Что назначено ---
    public string? TaskRef { get; set; }
    public string? TaskType { get; set; }
    public string? ChosenWorkerCode { get; set; }
    public double ChosenWorkerRemainingSec { get; set; }
    public double ChosenTaskPriority { get; set; }
    public double ChosenTaskDurationSec { get; set; }
    public decimal ChosenTaskWeightKg { get; set; }

    // --- Буфер ---
    public int BufferLevelBefore { get; set; }
    public int BufferLevelAfter { get; set; }
    public int BufferCapacity { get; set; }

    // --- Альтернативы (JSON) ---
    /// <summary>Top-3 альтернативных работника: [{code, remaining, load, tasks}]</summary>
    public string? AltWorkersJson { get; set; }
    /// <summary>Top-3 следующих палет в очереди: [{ref_, priority, duration, weight}]</summary>
    public string? AltTasksJson { get; set; }

    // --- Причина ---
    /// <summary>buffer_full / no_capacity / buffer_empty / no_ready_dist / none</summary>
    public string? ActiveConstraint { get; set; }
    /// <summary>Человекочитаемое объяснение решения</summary>
    public string? ReasonText { get; set; }
}

/// <summary>
/// Запись backtest_runs для чтения из БД (список бэктестов для UI)
/// </summary>
public class BacktestRunRecord
{
    public Guid Id { get; set; }
    public string WaveNumber { get; set; } = string.Empty;
    public DateTime WaveDate { get; set; }
    public string? WaveStatus { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public int TotalReplGroups { get; set; }
    public int TotalDistGroups { get; set; }
    public int UniqueWorkers { get; set; }
    public double ActualActiveSec { get; set; }
    public double OptimizedDurationSec { get; set; }
    public double ImprovementPct { get; set; }
    public int OriginalWaveDays { get; set; }
    public int OptimizedWaveDays { get; set; }
    public int DaysSaved { get; set; }
}
