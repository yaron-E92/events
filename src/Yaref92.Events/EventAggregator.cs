using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaref92.Events.Abstractions;
using System.Collections.Immutable;

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

    public virtual void PublishEvent<T>(T domainEvent) where T : class, IDomainEvent
    {
        ValidateEvent(domainEvent);

        if (_subscribersByType.TryGetValue(typeof(T), out var subscribers))
        {
            foreach (var subscriber in subscribers.Keys.OfType<IEventSubscriber<T>>())
            {
                subscriber.OnNext(domainEvent);
            }
        }
    }

    protected void ValidateEvent<T>(T domainEvent) where T : class, IDomainEvent
    {
        ValidateEventRegistration<T>();

        if (domainEvent is null)
        {
            _logger?.LogError("Attempted to publish a null event of type {EventType}.", typeof(T).FullName);
            throw new ArgumentNullException(nameof(domainEvent), "Cannot publish a null event.");
        }
    }

    protected void ValidateEventRegistration<T>() where T : class, IDomainEvent
    {
        if (!_eventTypes.ContainsKey(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }
    }

    public virtual void SubscribeToEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        ValidateEventRegistration<T>();
        var dict = _subscribersByType.GetOrAdd(typeof(T), _ => new ConcurrentDictionary<IEventSubscriber, byte>());
        if (!dict.TryAdd(subscriber, 0))
        {
            _logger?.LogWarning("Subscriber {SubscriberType} is already subscribed to event type {EventType}.",
                subscriber?.GetType().FullName, typeof(T).FullName);
        }
    }

    public virtual void UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        if (subscriber is null)
        {
            _logger?.LogError("Attempted to unsubscribe a null subscriber of type {SubscriberType}.", typeof(IEventSubscriber<T>).FullName);
            throw new ArgumentNullException(nameof(subscriber));
        }
        ValidateEventRegistration<T>();
        if (_subscribersByType.TryGetValue(typeof(T), out var dict))
        {
            dict.TryRemove(subscriber, out _);
        }
    }
}
