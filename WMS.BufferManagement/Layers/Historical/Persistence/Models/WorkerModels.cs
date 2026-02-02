namespace WMS.BufferManagement.Layers.Historical.Persistence.Models;

/// <summary>
/// Работник склада (Picker или Forklift)
/// </summary>
public class WorkerRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;  // Picker, Forklift, Unknown

    // Статистика
    public int TotalTasks { get; set; }
    public int CompletedTasks { get; set; }
    public decimal? AvgDurationSec { get; set; }
    public decimal? MedianDurationSec { get; set; }
    public decimal? StdDevDurationSec { get; set; }
    public decimal? MinDurationSec { get; set; }
    public decimal? MaxDurationSec { get; set; }
    public decimal? Percentile95DurationSec { get; set; }

    // Производительность
    public decimal? TasksPerHour { get; set; }
    public decimal? EfficiencyScore { get; set; }  // 0-100%

    public DateTime FirstTaskAt { get; set; }
    public DateTime LastTaskAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Статистика маршрута (from_slot → to_slot) для карщиков
/// </summary>
public class RouteStatistics
{
    public string FromZone { get; set; } = string.Empty;
    public string FromSlot { get; set; } = string.Empty;
    public string ToZone { get; set; } = string.Empty;
    public string ToSlot { get; set; } = string.Empty;

    // Количество выполнений
    public int TotalTrips { get; set; }
    public int NormalizedTrips { get; set; }  // После отсечения выбросов

    // Статистика времени (после нормализации)
    public decimal AvgDurationSec { get; set; }
    public decimal MedianDurationSec { get; set; }
    public decimal StdDevDurationSec { get; set; }
    public decimal MinDurationSec { get; set; }
    public decimal MaxDurationSec { get; set; }
    public decimal Percentile5DurationSec { get; set; }
    public decimal Percentile95DurationSec { get; set; }

    // Границы нормализации (использованные для отсечения)
    public decimal LowerBoundSec { get; set; }
    public decimal UpperBoundSec { get; set; }
    public int OutliersRemoved { get; set; }

    // Прогноз
    public decimal PredictedDurationSec { get; set; }
    public decimal ConfidenceLevel { get; set; }  // 0-1

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Детальная статистика работника по периодам
/// </summary>
public class WorkerPeriodStats
{
    public string WorkerId { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string PeriodType { get; set; } = string.Empty;  // Hour, Day, Week

    public int TasksCompleted { get; set; }
    public decimal AvgDurationSec { get; set; }
    public decimal TotalWorkTimeSec { get; set; }
    public decimal IdleTimeSec { get; set; }
    public decimal EfficiencyPercent { get; set; }
}

/// <summary>
/// Статистика пикер + товар (скорость сборки по товару)
/// </summary>
public class PickerProductStats
{
    public string PickerId { get; set; } = string.Empty;
    public string PickerName { get; set; } = string.Empty;
    public string ProductSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;

    // Количество
    public int TotalLines { get; set; }          // Количество выполненных строк
    public decimal TotalQty { get; set; }        // Общее количество единиц
    public decimal TotalWeightKg { get; set; }   // Общий вес (кг)

    // Время на строку
    public decimal? AvgDurationSec { get; set; }
    public decimal? MedianDurationSec { get; set; }
    public decimal? StdDevDurationSec { get; set; }
    public decimal? MinDurationSec { get; set; }
    public decimal? MaxDurationSec { get; set; }

    // Скорость
    public decimal? LinesPerMin { get; set; }    // Строк в минуту
    public decimal? QtyPerMin { get; set; }      // Единиц в минуту
    public decimal? KgPerMin { get; set; }       // Кг в минуту

    // Уверенность прогноза
    public decimal? Confidence { get; set; }     // 0-1

    public DateTime FirstTaskAt { get; set; }
    public DateTime LastTaskAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
