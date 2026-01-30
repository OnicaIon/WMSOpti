using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Layers.Tactical.Models;

/// <summary>
/// Волна заказов
/// </summary>
public class Wave
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public int SequenceNumber { get; init; }

    /// <summary>
    /// Заказы в волне
    /// </summary>
    public List<Order> Orders { get; init; } = new();

    /// <summary>
    /// Потоки заданий для этой волны
    /// </summary>
    public List<TaskStream> Streams { get; init; } = new();

    /// <summary>
    /// Время начала волны
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Крайний срок завершения волны
    /// </summary>
    public DateTime Deadline { get; set; }

    /// <summary>
    /// Статус волны
    /// </summary>
    public WaveStatus Status { get; set; } = WaveStatus.Pending;

    /// <summary>
    /// Общее количество палет в волне
    /// </summary>
    public int TotalPallets => Streams.Sum(s => s.AllTasks.Count);
}

public enum WaveStatus
{
    Pending,
    InProgress,
    Completed,
    Overdue
}
