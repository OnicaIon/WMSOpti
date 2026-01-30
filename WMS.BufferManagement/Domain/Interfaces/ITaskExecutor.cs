using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Interfaces;

/// <summary>
/// Интерфейс исполнителя заданий (для интеграции с внешней системой управления)
/// </summary>
public interface ITaskExecutor
{
    /// <summary>
    /// Назначить задание карщику
    /// </summary>
    Task AssignTaskAsync(DeliveryTask task, Forklift forklift);

    /// <summary>
    /// Отменить задание
    /// </summary>
    Task CancelTaskAsync(DeliveryTask task);

    /// <summary>
    /// Подтвердить завершение задания
    /// </summary>
    Task CompleteTaskAsync(DeliveryTask task);

    /// <summary>
    /// Получить статус задания
    /// </summary>
    Task<Entities.DeliveryTaskStatus> GetTaskStatusAsync(string taskId);

    /// <summary>
    /// Запустить поток заданий
    /// </summary>
    Task StartStreamAsync(TaskStream stream);

    /// <summary>
    /// Остановить поток заданий
    /// </summary>
    Task StopStreamAsync(TaskStream stream);
}
