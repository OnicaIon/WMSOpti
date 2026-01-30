using WMS.BufferManagement.Domain.Events;

namespace WMS.BufferManagement.Infrastructure.EventBus;

/// <summary>
/// Интерфейс шины событий
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Опубликовать событие
    /// </summary>
    void Publish<T>(T @event) where T : DomainEvent;

    /// <summary>
    /// Подписаться на событие
    /// </summary>
    void Subscribe<T>(Action<T> handler) where T : DomainEvent;

    /// <summary>
    /// Отписаться от события
    /// </summary>
    void Unsubscribe<T>(Action<T> handler) where T : DomainEvent;
}
