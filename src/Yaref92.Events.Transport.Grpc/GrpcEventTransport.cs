using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Grpc;

/// <summary>
/// gRPC-based implementation of <see cref="IEventTransport"/>.
/// Bridges inbound frames from <see cref="GrpcSessionService"/> and outbound
/// publishing through <see cref="GrpcClientConnectionManager"/>.
/// </summary>
public sealed class GrpcEventTransport : IEventTransport, IAsyncDisposable
{
    private readonly GrpcSessionService _sessionService;
    private readonly GrpcClientConnectionManager _clientManager;
    private readonly IPersistentPortListener _listener;
    private readonly IPersistentFramePublisher _publisher;

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

    internal IPersistentPortListener PersistentPortListener => _listener;

    internal IPersistentFramePublisher PersistentFramePublisher => _publisher;

    public GrpcEventTransport(
        GrpcSessionService sessionService,
        GrpcClientConnectionManager? clientManager = null)
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _clientManager = clientManager ?? new GrpcClientConnectionManager();
        _listener = new GrpcSessionPortListener(_sessionService);
        _publisher = new GrpcSessionFramePublisher(_clientManager);

        _sessionService.EventReceived += OnInboundEventAsync;
        _clientManager.EventReceived += OnInboundEventAsync;
        _listener.SessionConnectionAccepted += OnSessionConnectionAcceptedByListener;
        SessionInboundConnectionDropped += OnSessionInboundConnectionDropped;
        _listener.ConnectionManager.AckReceived += OnAckReceived;
        _listener.ConnectionManager.PingReceived += OnPingReceived;
    }

    public Task ConnectAsync(string host, int port, string? authenticationSecret = null, CancellationToken cancellationToken = default)
    {
        return _clientManager.ConnectAsync(host, port, authenticationSecret, cancellationToken);
    }

    public Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        return _clientManager.PublishAsync(domainEvent, cancellationToken: cancellationToken);
    }

    private async Task<bool> OnInboundEventAsync(IDomainEvent domainEvent)
    {
        var handler = EventReceived;
        if (handler is null)
        {
            return false;
        }

        try
        {
            return await handler(domainEvent).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private Task OnPingReceived(SessionKey key)
    {
        _publisher.ConnectionManager.SendPong(key);
        return Task.CompletedTask;
    }

    private Task OnAckReceived(Guid eventId, SessionKey sessionKey)
    {
        return _publisher.ConnectionManager.OnAckReceived(eventId, sessionKey);
    }

    private Task<bool> OnSessionInboundConnectionDropped(SessionKey key, CancellationToken token)
    {
        return _publisher.ConnectionManager.TryReconnectAsync(key, token);
    }

    private Task OnSessionConnectionAcceptedByListener(SessionKey sessionKey, CancellationToken cancellationToken)
    {
        return _publisher.ConnectionManager.ConnectAsync(sessionKey, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _sessionService.EventReceived -= OnInboundEventAsync;
        _clientManager.EventReceived -= OnInboundEventAsync;
        _listener.SessionConnectionAccepted -= OnSessionConnectionAcceptedByListener;
        SessionInboundConnectionDropped -= OnSessionInboundConnectionDropped;
        _listener.ConnectionManager.AckReceived -= OnAckReceived;
        _listener.ConnectionManager.PingReceived -= OnPingReceived;
        return _clientManager.DisposeAsync();
    }

    private sealed class GrpcSessionPortListener : IPersistentPortListener
    {
        private readonly GrpcSessionService _service;
        private readonly GrpcInboundConnectionManager _connectionManager;

        public int Port => 0;

        public IInboundConnectionManager ConnectionManager => _connectionManager;

        public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;

        event Func<SessionKey, CancellationToken, Task>? IPersistentPortListener.SessionConnectionAccepted
        {
            add => SessionConnectionAccepted += value;
            remove => SessionConnectionAccepted -= value;
        }

        event IEventTransport.SessionInboundConnectionDroppedHandler? IPersistentPortListener.SessionInboundConnectionDropped
        {
            add => _connectionManager.SessionInboundConnectionDropped += value;
            remove => _connectionManager.SessionInboundConnectionDropped -= value;
        }

        public GrpcSessionPortListener(GrpcSessionService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _connectionManager = new GrpcInboundConnectionManager(service);
            _service.SessionConnectionAccepted += OnSessionAcceptedAsync;
        }

        private Task OnSessionAcceptedAsync(SessionKey sessionKey, CancellationToken cancellationToken)
        {
            Func<SessionKey, CancellationToken, Task>? handler = SessionConnectionAccepted;
            if (handler is null)
            {
                return Task.CompletedTask;
            }

            return handler(sessionKey, cancellationToken);
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _service.SessionConnectionAccepted -= OnSessionAcceptedAsync;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GrpcSessionFramePublisher : IPersistentFramePublisher
    {
        private readonly GrpcClientConnectionManager _clientManager;
        private readonly GrpcOutboundConnectionManager _connectionManager;

        public IOutboundConnectionManager ConnectionManager => _connectionManager;

        public GrpcSessionFramePublisher(GrpcClientConnectionManager clientManager)
        {
            _clientManager = clientManager ?? throw new ArgumentNullException(nameof(clientManager));
            _connectionManager = new GrpcOutboundConnectionManager(_clientManager);
        }

        void IPersistentFramePublisher.AcknowledgeEventReceipt(Guid eventId, SessionKey sessionKey)
        {
            _connectionManager.SendAck(eventId, sessionKey);
        }

        public Task PublishToAllAsync(Guid eventId, string eventEnvelopePayload, CancellationToken cancellationToken)
        {
            return _clientManager.PublishSerializedAsync(eventEnvelopePayload, eventId, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GrpcInboundConnectionManager : IInboundConnectionManager
    {
        private readonly GrpcSessionService _service;

        public SessionManager SessionManager { get; }

        public event Func<IDomainEvent, SessionKey, Task>? EventReceived;

        event Func<IDomainEvent, SessionKey, Task>? IInboundConnectionManager.EventReceived
        {
            add => EventReceived += value;
            remove => EventReceived -= value;
        }

        public event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;

        event IEventTransport.SessionInboundConnectionDroppedHandler? IInboundConnectionManager.SessionInboundConnectionDropped
        {
            add => SessionInboundConnectionDropped += value;
            remove => SessionInboundConnectionDropped -= value;
        }

        public event Func<Guid, SessionKey, Task>? AckReceived;

        event Func<Guid, SessionKey, Task>? IInboundConnectionManager.AckReceived
        {
            add => AckReceived += value;
            remove => AckReceived -= value;
        }

        public event Func<SessionKey, Task>? PingReceived;

        event Func<SessionKey, Task>? IInboundConnectionManager.PingReceived
        {
            add => PingReceived += value;
            remove => PingReceived -= value;
        }

        public GrpcInboundConnectionManager(GrpcSessionService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            SessionManager = new SessionManager(0, new ResilientSessionOptions());
            _service.InboundEventReceived += HandleInboundEventAsync;
            _service.InboundAckReceived += HandleInboundAckAsync;
            _service.InboundPingReceived += HandleInboundPingAsync;
            _service.SessionConnectionClosed += HandleSessionClosedAsync;
        }

        private Task HandleInboundEventAsync(IDomainEvent domainEvent, SessionKey sessionKey)
        {
            return EventReceived?.Invoke(domainEvent, sessionKey) ?? Task.CompletedTask;
        }

        private Task HandleInboundAckAsync(Guid eventId, SessionKey sessionKey)
        {
            return AckReceived?.Invoke(eventId, sessionKey) ?? Task.CompletedTask;
        }

        private Task HandleInboundPingAsync(SessionKey sessionKey)
        {
            return PingReceived?.Invoke(sessionKey) ?? Task.CompletedTask;
        }

        private Task HandleSessionClosedAsync(SessionKey sessionKey, CancellationToken token)
        {
            return SessionInboundConnectionDropped?.Invoke(sessionKey, token) ?? Task.FromResult(false);
        }

        public Task<ConnectionInitializationResult> HandleIncomingTransientConnectionAsync(System.Net.Sockets.TcpClient incomingTransientConnection, CancellationToken serverToken)
        {
            return Task.FromResult(new ConnectionInitializationResult(false, new SessionKey(Guid.Empty, string.Empty, 0)));
        }

        public ValueTask DisposeAsync()
        {
            _service.InboundEventReceived -= HandleInboundEventAsync;
            _service.InboundAckReceived -= HandleInboundAckAsync;
            _service.InboundPingReceived -= HandleInboundPingAsync;
            _service.SessionConnectionClosed -= HandleSessionClosedAsync;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GrpcOutboundConnectionManager : IOutboundConnectionManager
    {
        private readonly GrpcClientConnectionManager _clientManager;
        private readonly SessionManager _sessionManager = new(0, new ResilientSessionOptions());

        public SessionManager SessionManager => _sessionManager;

        public GrpcOutboundConnectionManager(GrpcClientConnectionManager clientManager)
        {
            _clientManager = clientManager ?? throw new ArgumentNullException(nameof(clientManager));
        }

        public Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken = default)
        {
            return _clientManager.ConnectAsync(host, port, cancellationToken: cancellationToken);
        }

        public Task ConnectAsync(SessionKey sessionKey, CancellationToken cancellationToken = default)
        {
            if (sessionKey is null)
            {
                throw new ArgumentNullException(nameof(sessionKey));
            }

            return _clientManager.ConnectAsync(sessionKey.Host, sessionKey.Port, cancellationToken: cancellationToken);
        }

        public void QueueEventBroadcast(Guid eventId, string eventEnvelopeJson)
        {
            _ = _clientManager.PublishSerializedAsync(eventEnvelopeJson, eventId);
        }

        public Task<bool> TryReconnectAsync(SessionKey sessionKey, CancellationToken token)
        {
            return Task.FromResult(false);
        }

        public void SendAck(Guid eventId, SessionKey sessionKey)
        {
            _ = _clientManager.SendAckAsync(eventId, sessionKey, CancellationToken.None);
        }

        public Task OnAckReceived(Guid eventId, SessionKey sessionKey)
        {
            return _clientManager.NotifyAckAsync(eventId, sessionKey);
        }

        public void SendPong(SessionKey sessionKey)
        {
            _ = _clientManager.SendPongAsync(sessionKey, CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
