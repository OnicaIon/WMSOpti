using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Layers.Realtime.Dispatcher;

/// <summary>
/// Интерфейс диспетчера заданий
/// </summary>
public interface IDispatcher
{
    /// <summary>
    /// Добавить задание в очередь
    /// </summary>
    void EnqueueTask(DeliveryTask task);

    /// <summary>
    /// Добавить поток заданий
    /// </summary>
    void EnqueueStream(TaskStream stream);

    /// <summary>
    /// Назначить задания свободным карщикам
    /// </summary>
    void DispatchTasks(IEnumerable<Forklift> forklifts);

    /// <summary>
    /// Получить статистику очереди
    /// </summary>
    QueueStats GetQueueStats();

    /// <summary>
    /// Текущий поток (выполняющийся)
    /// </summary>
    TaskStream? CurrentStream { get; }

    /// <summary>
    /// Ожидающие потоки
    /// </summary>
    IReadOnlyList<TaskStream> PendingStreams { get; }
}

public record QueueStats(
    int TotalTasks,
    int PendingTasks,
    int InProgressTasks,
    int CompletedTasks,
    int TotalStreams,
    int CompletedStreams,
    double Utilization
);
