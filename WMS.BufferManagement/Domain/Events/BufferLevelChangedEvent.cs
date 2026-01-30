namespace WMS.BufferManagement.Domain.Events;

/// <summary>
/// Событие изменения уровня буфера
/// </summary>
public class BufferLevelChangedEvent : DomainEvent
{
    public int PreviousCount { get; init; }
    public int CurrentCount { get; init; }
    public int Capacity { get; init; }

    public double PreviousLevel => (double)PreviousCount / Capacity;
    public double CurrentLevel => (double)CurrentCount / Capacity;

    public double PreviousPercentage => PreviousLevel * 100;
    public double CurrentPercentage => CurrentLevel * 100;

    public bool IsIncreasing => CurrentCount > PreviousCount;
    public bool IsDecreasing => CurrentCount < PreviousCount;
}
