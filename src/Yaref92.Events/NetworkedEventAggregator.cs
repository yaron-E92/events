using Yaref92.Events.Abstractions;
using System.Collections.Concurrent;
using Timer = System.Timers.Timer;

namespace Yaref92.Events;

/// <summary>
/// An event aggregator that bridges local in-memory event delivery with networked event propagation via an <see cref="IEventTransport"/>.
/// Publishes events both locally and over the network, and dispatches received network events to local subscribers.
/// </summary>
public class NetworkedEventAggregator : IEventAggregator, IDisposable
{
    private readonly IEventAggregator _localAggregator;
    private readonly IEventTransport _transport;
    private readonly ConcurrentDictionary<Guid, DateTime> _recentEventIds = new();
    private readonly TimeSpan _deduplicationWindow;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public NetworkedEventAggregator(IEventAggregator localAggregator, IEventTransport transport, TimeSpan? deduplicationWindow = null)
    {
        _localAggregator = localAggregator ?? throw new ArgumentNullException(nameof(localAggregator));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _deduplicationWindow = deduplicationWindow ?? TimeSpan.FromMinutes(15);
        _cleanupTimer = new Timer(_deduplicationWindow.TotalMilliseconds / 2);
        _cleanupTimer.Elapsed += (s, e) => CleanupOldEventIds();
        _cleanupTimer.AutoReset = true;
        _cleanupTimer.Start();
    }

    public ISet<Type> EventTypes => _localAggregator.EventTypes;
    public IReadOnlyCollection<IEventHandler> Subscribers => _localAggregator.Subscribers;

    public bool RegisterEventType<T>() where T : class, IDomainEvent
    {
        // Register locally and subscribe to network events of this type
        var registered = _localAggregator.RegisterEventType<T>();
        _transport.Subscribe<T>();
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
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void SubscribeToEventType<T>(IEventHandler<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.SubscribeToEventType(subscriber);
    }

    public void SubscribeToEventType<T>(IAsyncEventHandler<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.SubscribeToEventType(subscriber);
    }

    public void UnsubscribeFromEventType<T>(IEventHandler<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.UnsubscribeFromEventType(subscriber);
    }

    public void UnsubscribeFromEventType<T>(IAsyncEventHandler<T> subscriber) where T : class, IDomainEvent
    {
        _localAggregator.UnsubscribeFromEventType(subscriber);
    }

    // Deduplication: returns true if this is the first time the event is seen (not a duplicate)
    private bool TryMarkSeen(IDomainEvent evt)
    {
        return _recentEventIds.TryAdd(evt.EventId, DateTime.UtcNow);
    }

    private void CleanupOldEventIds()
    {
        DateTime threshold = DateTime.UtcNow - _deduplicationWindow;
        foreach (var kvp in _recentEventIds)
        {
            if (kvp.Value < threshold)
            {
                _recentEventIds.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cleanupTimer?.Stop();
        _cleanupTimer?.Dispose();
        _disposed = true;
    }
} 
