namespace WMS.BufferManagement.Layers.Historical.Persistence.Models;

/// <summary>
/// Запись о выполненном задании для хранения в TimescaleDB
/// Таблица: tasks (hypertable по created_at)
/// </summary>
public class TaskRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Палета
    public string PalletId { get; set; } = string.Empty;
    public string? ProductType { get; set; }
    public decimal? WeightKg { get; set; }
    public string? WeightCategory { get; set; }  // Heavy, Medium, Light

    // Исполнитель
    public string? ForkliftId { get; set; }

    // Маршрут
    public string? FromZone { get; set; }
    public string? FromSlot { get; set; }
    public string? ToZone { get; set; }
    public string? ToSlot { get; set; }
    public decimal? DistanceMeters { get; set; }

    // Результат
    public string Status { get; set; } = "Pending";  // Pending, InProgress, Completed, Failed, Cancelled
    public decimal? DurationSec { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>
    /// Создать из доменной сущности DeliveryTask
    /// </summary>
    /// <param name="task">Задание на доставку</param>
    /// <param name="forkliftId">ID карщика (опционально)</param>
    /// <param name="fromZone">Зона отправления (по умолчанию Storage)</param>
    /// <param name="toZone">Зона назначения (по умолчанию Buffer)</param>
    /// <param name="distanceMeters">Расстояние в метрах (опционально)</param>
    public static TaskRecord FromDeliveryTask(
        Domain.Entities.DeliveryTask task,
        string? forkliftId = null,
        string? fromZone = "Storage",
        string? toZone = "Buffer",
        decimal? distanceMeters = null)
    {
        return new TaskRecord
        {
            Id = Guid.TryParse(task.Id, out var id) ? id : Guid.NewGuid(),
            CreatedAt = task.CreatedAt,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt,
            PalletId = task.Pallet?.Id ?? string.Empty,
            ProductType = task.Pallet?.Product?.Name,
            WeightKg = (decimal?)task.WeightKg,
            WeightCategory = task.Pallet?.Product?.Category.ToString(),
            ForkliftId = forkliftId ?? task.AssignedForklift?.Id,
            FromZone = fromZone,
            FromSlot = task.Pallet?.Id,  // Используем ID палеты как slot
            ToZone = toZone,
            ToSlot = null,
            DistanceMeters = distanceMeters,
            Status = task.Status.ToString(),
            DurationSec = task.CompletedAt.HasValue && task.StartedAt.HasValue
                ? (decimal)(task.CompletedAt.Value - task.StartedAt.Value).TotalSeconds
                : null
        };
    }
}
