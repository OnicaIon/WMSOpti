namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Карщик (погрузчик)
/// </summary>
public class Forklift
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Текущее состояние карщика
    /// </summary>
    public ForkliftState State { get; set; } = ForkliftState.Idle;

    /// <summary>
    /// Текущее задание (если есть)
    /// </summary>
    public DeliveryTask? CurrentTask { get; set; }

    /// <summary>
    /// Средняя скорость движения (м/с)
    /// </summary>
    public double SpeedMetersPerSecond { get; init; } = 2.0;

    /// <summary>
    /// Время погрузки/разгрузки палеты (секунды)
    /// </summary>
    public double LoadUnloadTimeSeconds { get; init; } = 30.0;

    /// <summary>
    /// Текущая позиция (расстояние от буфера в метрах, 0 = у буфера)
    /// </summary>
    public double CurrentPositionMeters { get; set; } = 0;

    /// <summary>
    /// Количество выполненных заданий
    /// </summary>
    public int CompletedTasksCount { get; set; }

    /// <summary>
    /// Общее время работы (секунды)
    /// </summary>
    public double TotalWorkTimeSeconds { get; set; }

    /// <summary>
    /// Расчёт времени доставки палеты до буфера
    /// </summary>
    public TimeSpan EstimateDeliveryTime(Pallet pallet)
    {
        // Время до палеты + погрузка + время до буфера + разгрузка
        var timeToпалет = Math.Abs(pallet.StorageDistanceMeters - CurrentPositionMeters) / SpeedMetersPerSecond;
        var timeToBuffer = pallet.StorageDistanceMeters / SpeedMetersPerSecond;
        var totalSeconds = timeToпалет + LoadUnloadTimeSeconds + timeToBuffer + LoadUnloadTimeSeconds;
        return TimeSpan.FromSeconds(totalSeconds);
    }

    /// <summary>
    /// Утилизация карщика (0-1)
    /// </summary>
    public double Utilization { get; set; }
}

public enum ForkliftState
{
    Idle,           // Ожидает задания
    MovingToPallet, // Едет к палете
    Loading,        // Загружает палету
    MovingToBuffer, // Везёт палету в буфер
    Unloading,      // Выгружает палету
    Offline         // Не работает
}
