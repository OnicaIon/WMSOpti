using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Layers.Realtime.Dispatcher;

/// <summary>
/// Очередь заданий с приоритетом по весу
/// </summary>
public class TaskQueue
{
    private readonly List<DeliveryTask> _tasks = new();
    private readonly object _lock = new();

    /// <summary>
    /// Количество заданий в очереди
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _tasks.Count; }
    }

    /// <summary>
    /// Добавить задание (будет отсортировано по весу)
    /// </summary>
    public void Enqueue(DeliveryTask task)
    {
        lock (_lock)
        {
            _tasks.Add(task);
        }
    }

    /// <summary>
    /// Добавить несколько заданий
    /// </summary>
    public void EnqueueRange(IEnumerable<DeliveryTask> tasks)
    {
        lock (_lock)
        {
            _tasks.AddRange(tasks);
        }
    }

    /// <summary>
    /// Получить следующее задание (самое тяжёлое из pending)
    /// </summary>
    public DeliveryTask? Dequeue()
    {
        lock (_lock)
        {
            var task = _tasks
                .Where(t => t.Status == DeliveryTaskStatus.Pending)
                .OrderByDescending(t => t.WeightKg)
                .FirstOrDefault();

            if (task != null)
            {
                task.Status = DeliveryTaskStatus.Assigned;
            }

            return task;
        }
    }

    /// <summary>
    /// Посмотреть следующее задание без удаления
    /// </summary>
    public DeliveryTask? Peek()
    {
        lock (_lock)
        {
            return _tasks
                .Where(t => t.Status == DeliveryTaskStatus.Pending)
                .OrderByDescending(t => t.WeightKg)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Получить все pending задания, отсортированные по весу
    /// </summary>
    public IReadOnlyList<DeliveryTask> GetPendingTasksByWeight()
    {
        lock (_lock)
        {
            return _tasks
                .Where(t => t.Status == DeliveryTaskStatus.Pending)
                .OrderByDescending(t => t.WeightKg)
                .ToList();
        }
    }

    /// <summary>
    /// Удалить завершённые задания
    /// </summary>
    public void RemoveCompleted()
    {
        lock (_lock)
        {
            _tasks.RemoveAll(t => t.Status == DeliveryTaskStatus.Completed);
        }
    }

    /// <summary>
    /// Очистить очередь
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _tasks.Clear();
        }
    }
}
