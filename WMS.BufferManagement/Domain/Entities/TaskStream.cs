namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Поток заданий - группа заданий, выполняемых последовательно.
/// Внутри потока задания сортируются по весу (тяжёлые первыми).
/// Потоки между собой также выполняются последовательно.
/// </summary>
public class TaskStream
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Название потока
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Порядковый номер потока (для последовательного выполнения)
    /// </summary>
    public int SequenceNumber { get; init; }

    /// <summary>
    /// Задания в потоке (внутренний список)
    /// </summary>
    private readonly List<DeliveryTask> _tasks = new();

    /// <summary>
    /// Задания, отсортированные по весу (тяжёлые первыми)
    /// </summary>
    public IReadOnlyList<DeliveryTask> Tasks => _tasks
        .OrderByDescending(t => t.WeightKg)
        .ToList();

    /// <summary>
    /// Все задания без сортировки
    /// </summary>
    public IReadOnlyList<DeliveryTask> AllTasks => _tasks.AsReadOnly();

    /// <summary>
    /// Статус потока
    /// </summary>
    public StreamStatus Status { get; set; } = StreamStatus.Pending;

    /// <summary>
    /// Время создания потока
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
    /// Добавить задание в поток
    /// </summary>
    public void AddTask(DeliveryTask task)
    {
        task.Stream = this;
        _tasks.Add(task);
        UpdateSequenceNumbers();
    }

    /// <summary>
    /// Добавить несколько заданий
    /// </summary>
    public void AddTasks(IEnumerable<DeliveryTask> tasks)
    {
        foreach (var task in tasks)
        {
            task.Stream = this;
            _tasks.Add(task);
        }
        UpdateSequenceNumbers();
    }

    /// <summary>
    /// Обновить порядковые номера заданий по весу
    /// </summary>
    private void UpdateSequenceNumbers()
    {
        var sorted = _tasks.OrderByDescending(t => t.WeightKg).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].SequenceInStream = i;
        }
    }

    /// <summary>
    /// Получить следующее задание для выполнения
    /// </summary>
    public DeliveryTask? GetNextPendingTask()
    {
        return Tasks.FirstOrDefault(t => t.Status == DeliveryTaskStatus.Pending);
    }

    /// <summary>
    /// Проверить, завершён ли поток
    /// </summary>
    public bool IsCompleted => _tasks.All(t => t.Status == DeliveryTaskStatus.Completed);

    /// <summary>
    /// Процент выполнения
    /// </summary>
    public double CompletionPercentage => _tasks.Count == 0
        ? 0
        : (double)_tasks.Count(t => t.Status == DeliveryTaskStatus.Completed) / _tasks.Count * 100;
}

public enum StreamStatus
{
    Pending,     // Ожидает выполнения
    InProgress,  // Выполняется
    Completed,   // Завершён
    Cancelled    // Отменён
}
