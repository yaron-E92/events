using System.Collections.Concurrent;
using System.Net.Sockets;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Sessions.Events;

namespace Yaref92.Events.Transports;

internal sealed class PersistentEventPublisher(
    PersistentSessionListener listener,
    ResilientSessionOptions options,
    IEventAggregator? localAggregator) : IAsyncDisposable, IAsyncEventHandler<SessionJoined>, IAsyncEventHandler<SessionLeft>
{
    private readonly PersistentSessionListener _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    private readonly ResilientSessionOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IEventAggregator? _localAggregator = localAggregator;
    private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<SessionKey, byte> _activeSessions = new();
    private TcpClient? _anonymousConnection;

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        SessionKey sessionKey = _activeSessions.SingleOrDefault(session => session.Key.Host.Equals(host) && session.Key.Port == port).Key;
        if (sessionKey is not null)
        {
            var session = _sessions.GetOrAdd(sessionKey, key =>
            {
                var peerSession = new ResilientPeerSession(key, _options, _localAggregator);
                _listener.RegisterPersistentSession(peerSession);
                return peerSession;
            });

            return session.StartAsync(cancellationToken);
        }

        _anonymousConnection = new TcpClient(host, port);
        return Task.CompletedTask;
    }

    public Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var tasks = _sessions.Values.Select(session => session.PublishAsync(payload, cancellationToken));
        return Task.WhenAll(tasks);
    }

    public Task OnNextAsync(SessionJoined domainEvent, CancellationToken cancellationToken = default)
    {
        if (SessionKey.IsNullOrInvalid(domainEvent.SessionKey))
        {
            ArgumentNullException.ThrowIfNull(domainEvent.SessionKey);
            throw new ArgumentException("The session key for the joined session is invalid", nameof(domainEvent));
        }

        return Task.Run(() =>
        {

            SessionKey sessionKey = domainEvent.SessionKey;
            if (!_sessions.TryGetValue(sessionKey, out var session))
            {
                session = new ResilientPeerSession(sessionKey, _options, _localAggregator);
                _sessions[sessionKey] = session;
                _listener.RegisterPersistentSession(session);
            }
            _ = session.StartAsync(cancellationToken);
            _activeSessions.TryAdd(domainEvent.SessionKey, 0);
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
        _anonymousConnection?.Dispose();
    }
}
