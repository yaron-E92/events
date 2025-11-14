using System.Threading;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports;

public class TCPEventTransport : IEventTransport, IAsyncDisposable
{
    private readonly IEventSerializer _serializer;
    private readonly IPersistentPortListener _listener;
    private readonly IPersistentFramePublisher _publisher;
    private Task? _disposeTask;
    private int _disposeState;

#if DEBUG
    internal IEventSerializer SerializerForTesting => _serializer;
    internal IPersistentFramePublisher PublisherForTesting => _publisher;
#endif

    IPersistentPortListener IEventTransport.PersistentPortListener => _listener;

    IPersistentFramePublisher IEventTransport.PersistentFramePublisher => _publisher;

    private event Func<IDomainEvent, Task<bool>>? EventReceived;

    event Func<IDomainEvent, Task<bool>> IEventTransport.EventReceived 
    {
        add => EventReceived += value;
        remove => EventReceived -= value;
    }

    public event IEventTransport.SessionInboundConnectionDroppedHandler SessionInboundConnectionDropped
    {
        add => _listener.SessionInboundConnectionDropped += value;
        remove => _listener.SessionInboundConnectionDropped -= value;
    }

    public TCPEventTransport(
        int listenPort,
        IEventSerializer? serializer = null,
        TimeSpan? heartbeatInterval = null,
        string? authenticationToken = null)
        : this(CreateListener(listenPort, serializer, heartbeatInterval, authenticationToken, out var publisher, out var serializerToUse), publisher, serializerToUse)
    {
    }

    internal TCPEventTransport(
        IPersistentPortListener listener,
        IPersistentFramePublisher publisher,
        IEventSerializer serializer)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

        _listener.SessionConnectionAccepted += OnSessionConnectionAcceptedByListener;
        SessionInboundConnectionDropped += OnSessionInboundConnectionDropped;
        _listener.ConnectionManager.EventReceived += OnEventReceived;
        _listener.ConnectionManager.AckReceived += OnAckReceived;
        _listener.ConnectionManager.PingReceived += OnPingReceived;
    }

    private Task OnPingReceived(SessionKey key)
    {
        _publisher.ConnectionManager.SendPong(key);
        return Task.CompletedTask;
    }

    private async Task OnAckReceived(Guid eventId, SessionKey sessionKey)
    {
        await _publisher.ConnectionManager.OnAckReceived(eventId, sessionKey);
    }

    private async Task<bool> OnSessionInboundConnectionDropped(SessionKey key, CancellationToken token)
    {
        return await _publisher.ConnectionManager.TryReconnectAsync(key, token);
    }

    // Invoked from the listener when a resilient inbound session connection is accepted
    private async Task OnSessionConnectionAcceptedByListener(SessionKey sessionKey, CancellationToken cancellationToken)
    {
        await _publisher?.ConnectionManager.ConnectAsync(sessionKey, cancellationToken)!;
    }

    // Invoked from the listener's inbound connection manager when an event is received
    // Invokes the transports EventReceived so the aggregator can react
    private async Task OnEventReceived(IDomainEvent domainEvent, SessionKey sessionKey)
    {
        var handler = EventReceived;
        if (handler is null)
        {
            return;
        }

        bool eventReceievedSuccessfully = await handler(domainEvent).ConfigureAwait(false);
        if (eventReceievedSuccessfully)
        {
            _publisher.AcknowledgeEventReceipt(domainEvent.EventId, sessionKey);
        }
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

        return _publisher.ConnectionManager.ConnectAsync(userId, host, port, cancellationToken);
    }

    public async Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var eventEnvelopeJson = _serializer.Serialize(domainEvent);
        await _publisher.PublishToAllAsync(domainEvent.EventId, eventEnvelopeJson, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
        {
            _disposeTask = DisposeAsyncCore();
        }

        return _disposeTask is null ? ValueTask.CompletedTask : new ValueTask(_disposeTask);
    }

    private async Task DisposeAsyncCore()
    {
        Task publisherDispose = _publisher.DisposeAsync().AsTask();
        Task listenerDispose = _listener.DisposeAsync().AsTask();

        await Task.WhenAll(publisherDispose, listenerDispose).ConfigureAwait(false);
    }

    private static IPersistentPortListener CreateListener(
        int listenPort,
        IEventSerializer? serializer,
        TimeSpan? heartbeatInterval,
        string? authenticationToken,
        out IPersistentFramePublisher publisher,
        out IEventSerializer serializerToUse)
    {
        serializerToUse = serializer ?? new JsonEventSerializer();

        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        ResilientSessionOptions sessionOptions = new()
        {
            RequireAuthentication = authenticationToken is not null,
            AuthenticationToken = authenticationToken,
            HeartbeatInterval = interval,
            HeartbeatTimeout = TimeSpan.FromTicks(interval.Ticks * 2),
        };

        var sessionManager = new SessionManager(listenPort, sessionOptions);
        publisher = new PersistentEventPublisher(sessionManager);
        return new PersistentPortListener(listenPort, serializerToUse, sessionManager);
    }
}
