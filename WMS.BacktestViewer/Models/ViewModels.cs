using System;
using System.Collections.Generic;

namespace WMS.BacktestViewer.Models
{
    /// <summary>
    /// Запись бэктеста для списка (combobox)
    /// </summary>
    public class BacktestRunInfo
    {
        public Guid Id { get; set; }
        public string WaveNumber { get; set; }
        public DateTime WaveDate { get; set; }
        public string WaveStatus { get; set; }
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

        public override string ToString()
        {
            return $"Волна {WaveNumber} ({WaveDate:dd.MM.yyyy}) — {ImprovementPct:F1}% улучшение, {DaysSaved} дней сэкономлено";
        }
    }

    /// <summary>
    /// Событие для Ганта — одна палета на одном работнике
    /// </summary>
    public class GanttTask
    {
        public long Id { get; set; }
        public string TimelineType { get; set; } // "fact" / "optimized"
        public string WorkerCode { get; set; }
        public string WorkerName { get; set; }
        public string WorkerRole { get; set; }
        public string TaskRef { get; set; }
        public string TaskType { get; set; } // Replenishment / Distribution
        public DateTime DayDate { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double DurationSec { get; set; }
        public string FromBin { get; set; }
        public string ToBin { get; set; }
        public string FromZone { get; set; }
        public string ToZone { get; set; }
        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public decimal WeightKg { get; set; }
        public int Qty { get; set; }
        public int SequenceNumber { get; set; }
        public int BufferLevel { get; set; }
        public string DurationSource { get; set; }
        public double TransitionSec { get; set; }

        // Для отображения
        public string DisplayName =>
            $"{TaskType?.Substring(0, 4)} | {FromZone}→{ToZone} | {WeightKg:F0}кг | {DurationSec:F0}с";

        public string WorkerDisplay =>
            $"[{TimelineType?.ToUpper()}] {WorkerRole} {WorkerCode} ({WorkerName})";
    }

    /// <summary>
    /// Решение оптимизатора (ПОЧЕМУ)
    /// </summary>
    public class DecisionInfo
    {
        public int DecisionSeq { get; set; }
        public DateTime DayDate { get; set; }
        public string DecisionType { get; set; }
        public string TaskRef { get; set; }
        public string TaskType { get; set; }
        public string ChosenWorkerCode { get; set; }
        public double ChosenWorkerRemainingSec { get; set; }
        public double ChosenTaskPriority { get; set; }
        public double ChosenTaskDurationSec { get; set; }
        public decimal ChosenTaskWeightKg { get; set; }
        public int BufferLevelBefore { get; set; }
        public int BufferLevelAfter { get; set; }
        public int BufferCapacity { get; set; }
        public string AltWorkersJson { get; set; }
        public string AltTasksJson { get; set; }
        public string ActiveConstraint { get; set; }
        public string ReasonText { get; set; }
    }
}
