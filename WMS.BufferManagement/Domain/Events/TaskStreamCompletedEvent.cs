using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Events;

/// <summary>
/// Событие завершения потока заданий
/// </summary>
public class TaskStreamCompletedEvent : DomainEvent
{
    public TaskStream Stream { get; init; } = null!;
    public int TotalTasks { get; init; }
    public TimeSpan TotalDuration { get; init; }
}
