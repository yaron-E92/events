using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

using Yaref92.Events.Abstractions;
using System.Collections.Immutable;

namespace Yaref92.Events;

public class EventAggregator : IEventAggregator
{
    private readonly ConcurrentDictionary<Type, byte> _eventTypes = new();
    public ISet<Type> EventTypes => _eventTypes.Keys.ToImmutableHashSet();

    private readonly IObservable<IDomainEvent> _eventStream;
    private readonly ISubject<IDomainEvent> _subject;
    private readonly ILogger<EventAggregator>? _logger;

    private readonly ConcurrentDictionary<(Type, IEventSubscriber), List<IDisposable>> _subscriptionGroups = new();
    public IReadOnlyCollection<IEventSubscriber> Subscribers => _subscriptionGroups.Keys.Select(k => k.Item2).ToImmutableHashSet();
    public IReadOnlyCollection<IDisposable> Subscriptions => _subscriptionGroups.Values.SelectMany(v => v).ToImmutableHashSet();


    public EventAggregator() : this(null) { }

    public EventAggregator(ILogger<EventAggregator>? logger)
    {
        _logger = logger;
        _subject = Subject.Synchronize(new Subject<IDomainEvent>());
        _eventStream = _subject.AsObservable();
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

        _subject.OnNext(domainEvent);
    }

    void IEventAggregator.SubscribeToEventType<T>(IEventSubscriber<T> subscriber)
    {
        if (!_eventTypes.ContainsKey(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }

        var subscription = _eventStream.OfType<T>().Subscribe(subscriber);
        var key = (typeof(T), subscriber);
        _subscriptionGroups.AddOrUpdate(
            key,
            _ => new List<IDisposable> { subscription },
            (_, list) => { lock (list) { list.Add(subscription); } return list; }
        );
    }

    void IEventAggregator.UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber)
    {
        if (!_eventTypes.ContainsKey(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }

        var key = (typeof(T), subscriber);
        if (_subscriptionGroups.TryRemove(key, out var disposables))
        {
            lock (disposables)
            {
                foreach (var d in disposables)
                {
                    d.Dispose();
                }
            }
        }
    }
}
