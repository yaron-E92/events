using System.Collections.Concurrent;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Sessions.Events;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.Transports;

internal sealed class PersistentEventPublisher : IAsyncDisposable, IAsyncEventHandler<SessionJoined>, IAsyncEventHandler<SessionLeft>
{
    private readonly PersistentPortListener _listener;
    private readonly ResilientSessionOptions _options;
    private readonly IEventAggregator? _localAggregator;
    //private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<SessionKey, byte> _activeSessions = new();
    private readonly ConcurrentDictionary<SessionKey, byte> _startedSessions = new();
    private readonly OutboundConnectionManager _outboundConnectionManager;

    public PersistentEventPublisher(
        PersistentPortListener listener,
        ResilientSessionOptions options,
        IEventAggregator? localAggregator, IEventSerializer eventSerializer, SessionManager sessionManager)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _localAggregator = localAggregator;
        EventSerializer = eventSerializer;
        SessionManager = sessionManager;
        _outboundConnectionManager = new(_options, SessionManager);
    }
#if DEBUG
    //internal ConcurrentDictionary<SessionKey, IResilientPeerSession> SessionsForTesting => _sessionManager.;
#endif
    public IEventSerializer EventSerializer { get; }
    public SessionManager SessionManager { get; }

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        return ConnectAsync(Guid.Empty, host, port, cancellationToken);
    }

    public Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var sessionKey = ResolveSessionKey(host, port, userId);
        return EnsureSessionExistenceAndStart(sessionKey, cancellationToken);
    }

    private async Task<IResilientPeerSession> EnsureSessionExistenceAndStart(SessionKey sessionKey, CancellationToken cancellationToken)
    {
        IResilientPeerSession session = GetOrCreateSession(sessionKey);
        await EnsureSessionStartedAsync(session, cancellationToken);
        return session;
    }

    public Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var tasks = _outboundConnectionManager.QueueBroadcast(payload); _sessions.Values.Select(session => session.PublishAsync(payload, cancellationToken));
        return Task.WhenAll(tasks);
    }

    public Task OnNextAsync(SessionJoined domainEvent, CancellationToken cancellationToken = default)
    {
        if (SessionKey.IsNullOrInvalid(domainEvent.SessionKey))
        {
            ArgumentNullException.ThrowIfNull(domainEvent.SessionKey);
            throw new ArgumentException("The session key for the joined session is invalid", nameof(domainEvent));
        }

        return Task.Run(async () =>
        {
            var sessionKey = domainEvent.SessionKey;
            IResilientPeerSession session = GetOrCreateSession(sessionKey);
            _activeSessions.TryAdd(sessionKey, 0);
            await EnsureSessionStartedAsync(session, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task OnNextAsync(SessionLeft domainEvent, CancellationToken cancellationToken = default)
    {
        SessionKey sessionKey = domainEvent.SessionKey;
        if (!SessionKey.IsNullOrInvalid(sessionKey))
        {
            _activeSessions.TryRemove(sessionKey, out _);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _activeSessions.Clear();
        _startedSessions.Clear();
    }

    private IResilientPeerSession GetOrCreateSession(SessionKey sessionKey)
    {
        return _sessions.GetOrAdd(sessionKey, key =>
        {
            var connection = _outboundConnectionManager.GetOrCreatePersistentClient(key, CreatePersistentClient);
            var peerSession = SessionManager.GetOrGenerate(key);
            _listener.RegisterPersistentSession(peerSession);
            return peerSession;
        });
    }

    private ResilientSessionConnection CreatePersistentClient(SessionKey sessionKey)
    {
        return new ResilientSessionConnection(sessionKey, _options, _localAggregator);
    }

    private SessionKey ResolveSessionKey(string host, int port, Guid userId)
    {
        if (userId != Guid.Empty)
        {
            return new SessionKey(userId, host, port);
        }

        return _sessions.Keys.FirstOrDefault(session =>
                   session.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && session.Port == port)
               ?? new SessionKey(Guid.NewGuid(), host, port);
    }

    private Task EnsureSessionStartedAsync(IResilientPeerSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_startedSessions.TryAdd(session.Key, 0))
        {
            return session.StartAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }

    internal async Task AcknowledgeFrameReceipt(SessionKey sessionKey, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
        EnsureSessionExistenceAndStart(sessionKey, cancellationToken);

    }

    internal void EnqueueAck(Guid eventId, SessionKey sessionKey)
    {
        _outboundConnectionManager.EnqueueAck(eventId, sessionKey);
    }
}
