namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Сборщик заказов
/// </summary>
public class Picker
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Текущее состояние сборщика
    /// </summary>
    public PickerState State { get; set; } = PickerState.Idle;

    /// <summary>
    /// Текущий заказ (если есть)
    /// </summary>
    public Order? CurrentOrder { get; set; }

    /// <summary>
    /// Средняя скорость сборки (единиц товара в час)
    /// </summary>
    public double AveragePickRatePerHour { get; set; } = 100;

    /// <summary>
    /// Текущая скорость (может отличаться от средней)
    /// </summary>
    public double CurrentPickRatePerHour { get; set; } = 100;

    /// <summary>
    /// Количество собранных заказов
    /// </summary>
    public int CompletedOrdersCount { get; set; }

    /// <summary>
    /// Количество обработанных палет
    /// </summary>
    public int ProcessedPalletsCount { get; set; }

    /// <summary>
    /// Общее время работы (секунды)
    /// </summary>
    public double TotalWorkTimeSeconds { get; set; }

    /// <summary>
    /// Скорость потребления палет (палет/час)
    /// </summary>
    public double PalletConsumptionRatePerHour { get; set; } = 5;
}

public enum PickerState
{
    Idle,       // Ожидает работу
    Picking,    // Собирает заказ
    Waiting,    // Ждёт палету в буфере
    Break,      // На перерыве
    Offline     // Не работает
}
