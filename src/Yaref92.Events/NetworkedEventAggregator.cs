using Yaref92.Events.Abstractions;
using System.Collections.Concurrent;

namespace Yaref92.Events;

/// <summary>
/// An event aggregator that bridges local in-memory event delivery with networked event propagation via an <see cref="IEventTransport"/>.
/// Publishes events both locally and over the network, and dispatches received network events to local subscribers.
/// </summary>
public class NetworkedEventAggregator : IEventAggregator
{
    private readonly IEventAggregator _localAggregator;
    private readonly IEventTransport _transport;
    private readonly ConcurrentDictionary<string, byte> _recentEventIds = new();
    private readonly TimeSpan _deduplicationWindow = TimeSpan.FromMinutes(5); // Placeholder for future config

    public NetworkedEventAggregator(IEventAggregator localAggregator, IEventTransport transport)
    {
        _localAggregator = localAggregator ?? throw new ArgumentNullException(nameof(localAggregator));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public ISet<Type> EventTypes => _localAggregator.EventTypes;
    public IReadOnlyCollection<IEventSubscriber> Subscribers => _localAggregator.Subscribers;

    public bool RegisterEventType<T>() where T : class, IDomainEvent
    {
        // Register locally and subscribe to network events of this type
        var registered = _localAggregator.RegisterEventType<T>();
        _transport.Subscribe<T>(async (evt, ct) =>
        {
            if (!IsDuplicate(evt))
            {
                MarkSeen(evt);
                await _localAggregator.PublishEventAsync(evt, ct).ConfigureAwait(false);
            }
        });
        return registered;
    }

    public void PublishEvent<T>(T domainEvent) where T : class, IDomainEvent
    {
        _localAggregator.PublishEvent(domainEvent);
        // Fire and forget network publish
        _ = _transport.PublishAsync(domainEvent);
    }

    public async Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        Task[] tasks =
        [
            _localAggregator.PublishEventAsync(domainEvent, cancellationToken),
            _transport.PublishAsync(domainEvent, cancellationToken),
        ];
        await Task.WhenAll(tasks);
    }

    public void SubscribeToEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.SubscribeToEventType(subscriber);
    }

    public void SubscribeToEventType<T>(IAsyncEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.SubscribeToEventType(subscriber);
    }

    public void UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.UnsubscribeFromEventType(subscriber);
    }

    public void UnsubscribeFromEventType<T>(IAsyncEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.UnsubscribeFromEventType(subscriber);
    }

    // Basic deduplication placeholder (to be improved with event IDs)
    private bool IsDuplicate(IDomainEvent evt)
    {
        // For now, use timestamp+type as a naive key
        var key = evt.GetType().FullName + ":" + evt.DateTimeOccurredUtc.Ticks;
        return !_recentEventIds.TryAdd(key, 0);
    }

    private void MarkSeen(IDomainEvent evt)
    {
        var key = evt.GetType().FullName + ":" + evt.DateTimeOccurredUtc.Ticks;
        _recentEventIds[key] = 0;
        // TODO: Cleanup old keys periodically
    }
} 
