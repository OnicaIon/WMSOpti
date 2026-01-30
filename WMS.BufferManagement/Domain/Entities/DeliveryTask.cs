namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Задание на перемещение палеты
/// </summary>
public class DeliveryTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Палета для перемещения
    /// </summary>
    public Pallet Pallet { get; init; } = null!;

    /// <summary>
    /// Статус задания
    /// </summary>
    public DeliveryTaskStatus Status { get; set; } = DeliveryTaskStatus.Pending;

    /// <summary>
    /// Приоритет задания (наследуется от веса товара)
    /// </summary>
    public int Priority => Pallet.Priority;

    /// <summary>
    /// Вес товара на палете (для сортировки)
    /// </summary>
    public double WeightKg => Pallet.Product.WeightKg;

    /// <summary>
    /// Назначенный карщик
    /// </summary>
    public Forklift? AssignedForklift { get; set; }

    /// <summary>
    /// Поток, к которому принадлежит задание
    /// </summary>
    public TaskStream? Stream { get; set; }

    /// <summary>
    /// Порядковый номер в потоке
    /// </summary>
    public int SequenceInStream { get; set; }

    /// <summary>
    /// Время создания задания
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Время начала выполнения
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Время завершения
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Ожидаемое время завершения
    /// </summary>
    public DateTime? EstimatedCompletionTime { get; set; }

    /// <summary>
    /// Фактическое время выполнения
    /// </summary>
    public TimeSpan? ActualDuration => CompletedAt.HasValue && StartedAt.HasValue
        ? CompletedAt.Value - StartedAt.Value
        : null;
}

public enum DeliveryTaskStatus
{
    Pending,     // Ожидает выполнения
    Assigned,    // Назначено карщику
    InProgress,  // Выполняется
    Completed,   // Завершено
    Cancelled    // Отменено
}
