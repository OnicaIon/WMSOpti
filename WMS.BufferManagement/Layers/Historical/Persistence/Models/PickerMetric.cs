namespace WMS.BufferManagement.Layers.Historical.Persistence.Models;

/// <summary>
/// Метрика сборщика для хранения в TimescaleDB
/// Таблица: picker_metrics (hypertable по time)
/// </summary>
public class PickerMetric
{
    /// <summary>
    /// Временная метка измерения
    /// </summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ID сборщика
    /// </summary>
    public string PickerId { get; set; } = string.Empty;

    /// <summary>
    /// Скорость потребления (палет/час)
    /// </summary>
    public decimal? ConsumptionRate { get; set; }

    /// <summary>
    /// Количество собранных единиц товара
    /// </summary>
    public int? ItemsPicked { get; set; }

    /// <summary>
    /// Эффективность (% от нормы)
    /// </summary>
    public decimal? Efficiency { get; set; }

    /// <summary>
    /// Активен ли сборщик
    /// </summary>
    public bool Active { get; set; }

    /// <summary>
    /// Создать из доменной сущности Picker
    /// </summary>
    public static PickerMetric FromPicker(Domain.Entities.Picker picker)
    {
        var avgRate = picker.AveragePickRatePerHour > 0 ? picker.AveragePickRatePerHour : 1;
        var efficiency = picker.CurrentPickRatePerHour / avgRate * 100;

        return new PickerMetric
        {
            Time = DateTime.UtcNow,
            PickerId = picker.Id,
            ConsumptionRate = (decimal)picker.PalletConsumptionRatePerHour,
            Efficiency = (decimal)efficiency,
            Active = picker.State != Domain.Entities.PickerState.Offline &&
                     picker.State != Domain.Entities.PickerState.Break
        };
    }
}

/// <summary>
/// Агрегированная статистика сборщика по часам (из continuous aggregate)
/// </summary>
public class PickerHourlyStats
{
    public DateTime Hour { get; set; }
    public string PickerId { get; set; } = string.Empty;
    public decimal AvgRate { get; set; }
    public decimal MaxRate { get; set; }
    public decimal AvgEfficiency { get; set; }
    public int Samples { get; set; }
}
