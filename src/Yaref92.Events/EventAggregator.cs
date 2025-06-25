using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaref92.Events.Abstractions;
using System.Collections.Immutable;
using System.Linq;

namespace Yaref92.Events;

public class EventAggregator : IEventAggregator
{
    private readonly ConcurrentDictionary<Type, byte> _eventTypes = new();
    public ISet<Type> EventTypes => _eventTypes.Keys.ToImmutableHashSet();

    private readonly ILogger<EventAggregator>? _logger;
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<IEventSubscriber, byte>> _subscribersByType = new();
    public IReadOnlyCollection<IEventSubscriber> Subscribers => _subscribersByType.Values.SelectMany(dict => dict.Keys).ToImmutableHashSet();

    public EventAggregator() : this(null) { }

    public EventAggregator(ILogger<EventAggregator>? logger)
    {
        _logger = logger;
    }

    public bool RegisterEventType<T>() where T : class, IDomainEvent
    {
        var added = _eventTypes.TryAdd(typeof(T), 0);
        if (!added)
        {
            _logger?.LogWarning("Event type {EventType} is already registered.", typeof(T).FullName);
        }
        return added;
    }

    void IEventAggregator.PublishEvent<T>(T domainEvent)
    {
        if (!_eventTypes.ContainsKey(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }

        if (domainEvent is null)
        {
            _logger?.LogWarning("Attempted to publish a null event of type {EventType}.", typeof(T).FullName);
            throw new ArgumentNullException(nameof(domainEvent), "Cannot publish a null event.");
        }

        if (_subscribersByType.TryGetValue(typeof(T), out var subscribers))
        {
            foreach (var subscriber in subscribers.Keys.OfType<IEventSubscriber<T>>())
            {
                subscriber.OnNext(domainEvent);
            }
        }
    }

    void IEventAggregator.SubscribeToEventType<T>(IEventSubscriber<T> subscriber)
    {
        if (!_eventTypes.ContainsKey(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }
        var dict = _subscribersByType.GetOrAdd(typeof(T), _ => new ConcurrentDictionary<IEventSubscriber, byte>());
        dict.TryAdd(subscriber, 0);
    }

    void IEventAggregator.UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber)
    {
        if (!_eventTypes.ContainsKey(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }
        if (_subscribersByType.TryGetValue(typeof(T), out var dict))
        {
            dict.TryRemove(subscriber, out _);
        }
    }
}
