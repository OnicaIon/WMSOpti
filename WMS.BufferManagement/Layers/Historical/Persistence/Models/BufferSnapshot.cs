namespace WMS.BufferManagement.Layers.Historical.Persistence.Models;

/// <summary>
/// Снимок состояния буфера для хранения в TimescaleDB
/// Таблица: buffer_snapshots (hypertable по time)
/// </summary>
public class BufferSnapshot
{
    /// <summary>
    /// Временная метка снимка
    /// </summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Уровень заполнения буфера (0.0 - 1.0)
    /// </summary>
    public decimal BufferLevel { get; set; }

    /// <summary>
    /// Состояние буфера (Normal, Low, Critical, Overflow)
    /// </summary>
    public string BufferState { get; set; } = "Normal";

    /// <summary>
    /// Количество палет в буфере
    /// </summary>
    public int PalletsCount { get; set; }

    /// <summary>
    /// Количество активных карщиков
    /// </summary>
    public int ActiveForklifts { get; set; }

    /// <summary>
    /// Количество активных сборщиков
    /// </summary>
    public int ActivePickers { get; set; }

    /// <summary>
    /// Скорость потребления (палет/час)
    /// </summary>
    public decimal? ConsumptionRate { get; set; }

    /// <summary>
    /// Скорость доставки (палет/час)
    /// </summary>
    public decimal? DeliveryRate { get; set; }

    /// <summary>
    /// Длина очереди заданий
    /// </summary>
    public int QueueLength { get; set; }

    /// <summary>
    /// Количество ожидающих заданий
    /// </summary>
    public int PendingTasks { get; set; }

    /// <summary>
    /// Создать из текущего состояния системы
    /// </summary>
    /// <param name="buffer">Зона буфера</param>
    /// <param name="bufferState">Текущее состояние буфера (Normal, Low, Critical, Overflow)</param>
    /// <param name="activeForklifts">Количество активных карщиков</param>
    /// <param name="activePickers">Количество активных сборщиков</param>
    /// <param name="consumptionRate">Скорость потребления (палет/час)</param>
    /// <param name="deliveryRate">Скорость доставки (палет/час)</param>
    /// <param name="queueLength">Длина очереди заданий</param>
    /// <param name="pendingTasks">Количество ожидающих заданий</param>
    public static BufferSnapshot FromSystemState(
        Domain.Entities.BufferZone buffer,
        string bufferState,
        int activeForklifts,
        int activePickers,
        double consumptionRate,
        double deliveryRate,
        int queueLength,
        int pendingTasks)
    {
        return new BufferSnapshot
        {
            Time = DateTime.UtcNow,
            BufferLevel = (decimal)buffer.FillLevel,
            BufferState = bufferState,
            PalletsCount = buffer.CurrentCount,
            ActiveForklifts = activeForklifts,
            ActivePickers = activePickers,
            ConsumptionRate = (decimal)consumptionRate,
            DeliveryRate = (decimal)deliveryRate,
            QueueLength = queueLength,
            PendingTasks = pendingTasks
        };
    }
}

/// <summary>
/// Агрегированная статистика буфера за период
/// </summary>
public class BufferPeriodStats
{
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public decimal AvgLevel { get; set; }
    public decimal MinLevel { get; set; }
    public decimal MaxLevel { get; set; }
    public decimal AvgConsumption { get; set; }
    public decimal AvgDelivery { get; set; }
    public int CriticalCount { get; set; }  // Сколько раз был Critical
}
