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
    private readonly ConcurrentDictionary<Guid, DateTime> _recentIncomingEventIds = new();
    private readonly TimeSpan _deduplicationWindow;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public NetworkedEventAggregator(IEventAggregator localAggregator, IEventTransport transport, TimeSpan? deduplicationWindow = null)
    {
        _localAggregator = localAggregator ?? throw new ArgumentNullException(nameof(localAggregator));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.EventReceived += OnEventReceived; // Subscribe to incoming network events from transport
        _deduplicationWindow = deduplicationWindow ?? TimeSpan.FromMinutes(15);
        _cleanupTimer = new Timer(_deduplicationWindow.TotalMilliseconds / 2);
        _cleanupTimer.Elapsed += (s, e) => CleanupOldEventIds();
        _cleanupTimer.AutoReset = true;
        _cleanupTimer.Start();
    }

    /// <summary>
    /// Publishes an incoming domain event received from the transport to local subscribers, if it hasn't been seen before.
    /// </summary>
    /// <param name="incomingDomainEvent"></param>
    /// <returns>
    /// true if successfully published or if it already received this event in the deduplication window.
    /// false if there was an error publishing the event locally.
    /// </returns>
    async Task<bool> OnEventReceived(IDomainEvent incomingDomainEvent)
    {
        if (_recentIncomingEventIds.ContainsKey(incomingDomainEvent.EventId))
        {
            return true;
        }

        Task publishTask = PublishIncomingEventLocallyAsync(incomingDomainEvent, CancellationToken.None);

        try
        {
            await publishTask.ConfigureAwait(false);
            _recentIncomingEventIds.TryAdd(incomingDomainEvent.EventId, DateTime.UtcNow);
            return true;
        }
        catch
        {
            _recentIncomingEventIds.TryRemove(incomingDomainEvent.EventId, out _);
            return false;
        }
    }

    public ISet<Type> EventTypes => _localAggregator.EventTypes;
    public IReadOnlyCollection<IEventHandler> Subscribers => _localAggregator.Subscribers;

    public bool RegisterEventType<T>() where T : class, IDomainEvent
    {
        // Register locally and subscribe to network events of this type
        var registered = _localAggregator.RegisterEventType<T>();
        return registered;
    }

    public void PublishEvent<T>(T domainEvent) where T : class, IDomainEvent
    {
        _localAggregator.PublishEvent(domainEvent);
        // Fire and forget network publish
        _ = _transport.PublishEventAsync(domainEvent);
    }

    public async Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        Task[] tasks =
        [
            _localAggregator.PublishEventAsync(domainEvent, cancellationToken),
            _transport.PublishEventAsync(domainEvent, cancellationToken),
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

    private Task PublishIncomingEventLocallyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        if (_localAggregator is null)
        {
            return Task.FromException(new InvalidOperationException("Can't publish locally without a local aggregator"));
        }

        dynamic aggregator = _localAggregator;
        return (Task) aggregator.PublishEventAsync((dynamic) domainEvent, cancellationToken);
    }

    private void CleanupOldEventIds()
    {
        DateTime threshold = DateTime.UtcNow - _deduplicationWindow;
        foreach (var kvp in _recentIncomingEventIds)
        {
            if (kvp.Value < threshold)
            {
                _recentIncomingEventIds.TryRemove(kvp.Key, out _);
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
