using System;
using System.Collections.Concurrent;
using System.Linq;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Sessions.Events;

namespace Yaref92.Events.Transports;

internal sealed class PersistentEventPublisher(
    PersistentSessionListener listener,
    ResilientSessionOptions options,
    IEventAggregator? localAggregator, IEventSerializer eventSerializer) : IAsyncDisposable, IAsyncEventHandler<SessionJoined>, IAsyncEventHandler<SessionLeft>
{
    private readonly PersistentSessionListener _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    private readonly ResilientSessionOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IEventAggregator? _localAggregator = localAggregator;
    private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<SessionKey, byte> _activeSessions = new();
    private readonly ConcurrentDictionary<SessionKey, byte> _startedSessions = new();

    public IEventSerializer EventSerializer { get; } = eventSerializer;

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var sessionKey = _sessions.Keys.FirstOrDefault(session =>
            session.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && session.Port == port)
            ?? new SessionKey(Guid.NewGuid(), host, port);

        var session = GetOrCreateSession(sessionKey);
        return EnsureSessionStartedAsync(session, cancellationToken);
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

        return Task.Run(async () =>
        {
            var sessionKey = domainEvent.SessionKey;
            var session = GetOrCreateSession(sessionKey);
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
            var client = _listener.GetOrCreatePersistentClient(key, CreatePersistentClient);
            var peerSession = new ResilientPeerSession(key, client, _options, _localAggregator, EventSerializer);
            _listener.RegisterPersistentSession(peerSession);
            return peerSession;
        });
    }

    private ResilientSessionClient CreatePersistentClient(SessionKey sessionKey)
    {
        return new ResilientSessionClient(sessionKey, _options, _localAggregator);
    }

    private Task EnsureSessionStartedAsync(IResilientPeerSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_startedSessions.TryAdd(session.SessionKey, 0))
        {
            return session.StartAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }
}
