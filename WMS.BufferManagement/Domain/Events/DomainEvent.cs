namespace WMS.BufferManagement.Domain.Events;

/// <summary>
/// Базовый класс для доменных событий
/// </summary>
public abstract class DomainEvent
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
