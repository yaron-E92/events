using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.EventHandlers;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports;

public class TCPEventTransport : IEventTransport, IAsyncDisposable
{
    private readonly IEventSerializer _serializer;
    private readonly IEventAggregator? _localAggregator;
    private readonly PersistentSessionListener _listener;
    private readonly PersistentEventPublisher _publisher;

    internal IEventSerializer SerializerForTesting => _serializer;
    internal PersistentSessionListener ListenerForTesting => _listener;
    internal PersistentEventPublisher PublisherForTesting => _publisher;

    public TCPEventTransport(
        int listenPort,
        IEventSerializer? serializer = null,
        IEventAggregator? eventAggregator = null,
        TimeSpan? heartbeatInterval = null,
        string? authenticationToken = null)
    {
        _serializer = serializer ?? new JsonEventSerializer();
        _localAggregator = eventAggregator;

        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        ResilientSessionOptions sessionOptions = new()
        {
            RequireAuthentication = authenticationToken is not null,
            AuthenticationToken = authenticationToken,
            HeartbeatInterval = interval,
            HeartbeatTimeout = TimeSpan.FromTicks(interval.Ticks * 2),
        };

        _localAggregator?.RegisterEventType<PublishFailed>();
        _localAggregator?.SubscribeToEventType(new PublishFailedHandler());

        _listener = new PersistentSessionListener(listenPort, sessionOptions, this);

        _publisher = new PersistentEventPublisher(_listener, sessionOptions, _localAggregator, serializer ?? new JsonEventSerializer());
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        return _listener.StartAsync(cancellationToken);
    }

    public Task ConnectToPeerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        return ConnectToPeerAsync(Guid.Empty, host, port, cancellationToken);
    }

    public Task ConnectToPeerAsync(Guid userId, string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
        }

        return _publisher.ConnectAsync(userId, host, port, cancellationToken);
    }

    /// <summary>
    /// Processes an incoming domain event asynchronously.
    /// </summary>
    /// <typeparam name="T">The type of the domain event, which must implement <see cref="IDomainEvent"/>.</typeparam>
    /// <param name="domainEvent">The domain event to be processed. Cannot be <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
    /// <remarks>The persistent listener uses it's event handlers to fire this event</remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task AcceptIncomingTrafficAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await _listener.HandleReceivedEventAsync(new EventReceived<T>(DateTime.UtcNow, domainEvent), cancellationToken);
    }

    public async Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var payload = _serializer.Serialize(domainEvent);
        await _publisher.PublishAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public void Subscribe<TEvent>() where TEvent : class, IDomainEvent
    {
        _localAggregator?.SubscribeToEventType(new EventReceivedHandler<TEvent>(_localAggregator));
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.DisposeAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
    }
}
