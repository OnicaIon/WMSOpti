using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Events;

/// <summary>
/// Событие изменения состояния карщика
/// </summary>
public class ForkliftStateChangedEvent : DomainEvent
{
    public Forklift Forklift { get; init; } = null!;
    public ForkliftState PreviousState { get; init; }
    public ForkliftState CurrentState { get; init; }
}
