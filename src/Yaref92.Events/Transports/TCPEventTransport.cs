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
    private readonly TempListener _listener;
    private readonly TempPublisher _publisher;
    private readonly SessionManager _sessionManager;

#if DEBUG
    internal IEventSerializer SerializerForTesting => _serializer;
    //internal PersistentPortListener ListenerForTesting => _listener;
    //internal PersistentEventPublisher PublisherForTesting => _publisher; 
#endif

    IPersistentPortListener IEventTransport.PersistentPortListener => _listener;

    IPersistentFramePublisher IEventTransport.PersistentFramePublisher => _publisher;

    private event Func<IDomainEvent, Task<bool>> EventReceived;

    event Func<IDomainEvent, Task<bool>> IEventTransport.EventReceived 
    {
        add => EventReceived += value;
        remove => EventReceived -= value;
    }

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

        _sessionManager = new SessionManager(listenPort, sessionOptions, _serializer, _localAggregator);

        _listener = new TempListener(listenPort, sessionOptions, _serializer, _sessionManager);

        _publisher = new TempPublisher(_listener, sessionOptions, _localAggregator, serializer ?? new JsonEventSerializer(), _sessionManager);
        _listener.SessionConnectionAccepted += async (sessionKey, cancellationToken) =>
        {
            await _publisher?.ConnectionManager.ConnectAsync(sessionKey, cancellationToken)!;
        };
        _listener.SessionConnectionRemoved += async (s, e) =>
        {
            await _publisher?.OnNextAsync(new Sessions.Events.SessionLeft(s), CancellationToken.None)!;
        };
        _listener.ConnectionManager.EventReceived += OnEventReceived;
    }

    // Invoked from the listener'sessionKey inbound connection manager when an event is received
    private async Task OnEventReceived(IDomainEvent domainEvent, SessionKey sessionKey)
    {
        bool eventReceievedSuccessfully = await EventReceived.Invoke(domainEvent);
        if (eventReceievedSuccessfully)
        {
            _publisher.AcknowledgeEventReceipt(domainEvent.EventId, sessionKey);
        }
    }

    //private Task OnListenerFrameReceived(SessionKey sessionKey, SessionFrame _, CancellationToken cancellationToken)
    //{
    //    return _publisher.AcknowledgeFrameReceipt(sessionKey, cancellationToken);
    //}

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

        return _publisher.ConnectionManager.ConnectAsync(userId, host, port, cancellationToken);
    }

    /// <summary>
    /// Processes an incoming domain event asynchronously.
    /// </summary>
    /// <typeparam name="T">The type of the domain event, which must implement <see cref="IDomainEvent"/>.</typeparam>
    /// <param name="domainEvent">The domain event to be processed. Cannot be <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
    /// <remarks>The persistent listener uses it'sessionKey event handlers to fire this event</remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task AcceptIncomingEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await _listener.HandleReceivedEventAsync(new EventReceived<T>(DateTime.UtcNow, domainEvent), cancellationToken);
    }

    public async Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var eventEnvelopeJson = _serializer.Serialize(domainEvent);
        await _publisher.PublishAsync(eventEnvelopeJson, cancellationToken).ConfigureAwait(false);
    }

    public void Subscribe<TEvent>() where TEvent : class, IDomainEvent
    {
        _localAggregator?.SubscribeToEventType(new EventReceivedHandler<TEvent>(typeof(TEvent), _localAggregator));
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.DisposeAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
    }

    void IEventTransport.AcknowledgeEventReceipt(Guid eventId, SessionKey sessionKey)
    {
        _publisher.EnqueueAck(eventId, sessionKey);
    }

    private async Task PublishIncomingEventLocallyAsync(string eventEnvelopePayload, CancellationToken cancellationToken)
    {
        if (_localAggregator is null)
        {
            return;
        }

        (_, IDomainEvent? domainEvent) = _serializer.Deserialize(eventEnvelopePayload);
        if (domainEvent is null)
        {
            return;
        }

        await PublishDomainEventAsync(domainEvent, cancellationToken).ConfigureAwait(false);
    }

    private Task PublishDomainEventAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        if (_localAggregator is null)
        {
            return Task.CompletedTask;
        }

        dynamic aggregator = _localAggregator;
        return (Task) aggregator.PublishEventAsync((dynamic) domainEvent, cancellationToken);
    }
}
