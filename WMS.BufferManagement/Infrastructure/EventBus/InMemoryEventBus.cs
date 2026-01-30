using System.Collections.Concurrent;
using WMS.BufferManagement.Domain.Events;

namespace WMS.BufferManagement.Infrastructure.EventBus;

/// <summary>
/// In-memory реализация шины событий
/// </summary>
public class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();

    public void Publish<T>(T @event) where T : DomainEvent
    {
        var eventType = typeof(T);

        if (_handlers.TryGetValue(eventType, out var handlers))
        {
            List<Delegate> handlersCopy;
            lock (_lock)
            {
                handlersCopy = handlers.ToList();
            }

            foreach (var handler in handlersCopy)
            {
                try
                {
                    ((Action<T>)handler)(@event);
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"Error in event handler: {ex.Message}");
                }
            }
        }
    }

    public void Subscribe<T>(Action<T> handler) where T : DomainEvent
    {
        var eventType = typeof(T);

        lock (_lock)
        {
            if (!_handlers.ContainsKey(eventType))
            {
                _handlers[eventType] = new List<Delegate>();
            }
            _handlers[eventType].Add(handler);
        }
    }

    public void Unsubscribe<T>(Action<T> handler) where T : DomainEvent
    {
        var eventType = typeof(T);

        lock (_lock)
        {
            if (_handlers.TryGetValue(eventType, out var handlers))
            {
                handlers.Remove(handler);
            }
        }
    }
}
