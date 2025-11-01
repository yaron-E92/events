using System.Collections.Concurrent;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Transports;

internal sealed class PersistentEventPublisher : IAsyncDisposable
{
    private readonly PersistentSessionListener _listener;
    private readonly ResilientSessionOptions _options;
    private readonly IEventAggregator? _eventAggregator;
    private readonly Func<string, string, CancellationToken, Task> _payloadHandler;
    private readonly ConcurrentDictionary<string, IPersistentPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<string, byte> _activeSessions = new();

    public PersistentEventPublisher(
        PersistentSessionListener listener,
        ResilientSessionOptions options,
        Func<string, string, CancellationToken, Task> payloadHandler,
        IEventAggregator? eventAggregator)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _payloadHandler = payloadHandler ?? throw new ArgumentNullException(nameof(payloadHandler));
        _eventAggregator = eventAggregator;

        _listener.SessionJoined += OnSessionJoinedAsync;
        _listener.SessionLeft += OnSessionLeftAsync;
    }

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var key = GetSessionKey(host, port);
        var session = _sessions.GetOrAdd(key, _ =>
        {
            var peer = new ResilientPeerSession(host, port, _options, _payloadHandler, _eventAggregator);
            _listener.RegisterPersistentSession(peer);
            return peer;
        });

        _listener.RegisterPersistentSession(session);
        return session.StartAsync(cancellationToken);
    }

    public Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var tasks = _sessions.Values.Select(session => session.PublishAsync(payload, cancellationToken));
        return Task.WhenAll(tasks);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.SessionJoined -= OnSessionJoinedAsync;
        _listener.SessionLeft -= OnSessionLeftAsync;

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _activeSessions.Clear();
    }

    private Task OnSessionJoinedAsync(string sessionKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionKey))
        {
            _activeSessions[sessionKey] = 0;
        }

        if (_sessions.TryGetValue(sessionKey, out var session))
        {
            _listener.RegisterPersistentSession(session);
        }

        return Task.CompletedTask;
    }

    private Task OnSessionLeftAsync(string sessionKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionKey))
        {
            _activeSessions.TryRemove(sessionKey, out _);
        }

        return Task.CompletedTask;
    }

    private static string GetSessionKey(string host, int port) => $"{host}:{port}";
}
