using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Interfaces;

/// <summary>
/// Интерфейс сборщика метрик для Historical Intelligence
/// </summary>
public interface IMetricsCollector
{
    /// <summary>
    /// Записать метрику уровня буфера
    /// </summary>
    void RecordBufferLevel(double level, DateTime timestamp);

    /// <summary>
    /// Записать время доставки палеты
    /// </summary>
    void RecordDeliveryTime(Forklift forklift, Pallet pallet, TimeSpan duration);

    /// <summary>
    /// Записать скорость сборщика
    /// </summary>
    void RecordPickerSpeed(Picker picker, double palletsPerHour);

    /// <summary>
    /// Записать завершение потока
    /// </summary>
    void RecordStreamCompletion(TaskStream stream, TimeSpan duration);

    /// <summary>
    /// Записать утилизацию карщика
    /// </summary>
    void RecordForkliftUtilization(Forklift forklift, double utilization);

    /// <summary>
    /// Получить средний уровень буфера за период
    /// </summary>
    Task<double> GetAverageBufferLevelAsync(TimeSpan period);

    /// <summary>
    /// Получить среднее время доставки
    /// </summary>
    Task<TimeSpan> GetAverageDeliveryTimeAsync(TimeSpan period);

    /// <summary>
    /// Получить скорость потребления буфера
    /// </summary>
    Task<double> GetConsumptionRateAsync(TimeSpan period);
}
