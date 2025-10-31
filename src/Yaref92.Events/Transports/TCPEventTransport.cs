using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;

namespace Yaref92.Events.Transports;

public class TCPEventTransport : IEventTransport, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Type, ConcurrentBag<Func<object, CancellationToken, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<string, PersistentSessionClient> _persistentSessions = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _receiveTasks = new();

    private readonly int _listenPort;
    private readonly IEventSerializer _serializer;
    private readonly IEventAggregator? _eventAggregator;
    private readonly ResilientSessionOptions _sessionOptions;

    private ResilientTcpServer? _server;
    private CancellationTokenSource? _cts;

    public TCPEventTransport(
        int listenPort,
        IEventSerializer? serializer = null,
        IEventAggregator? eventAggregator = null,
        TimeSpan? heartbeatInterval = null,
        string? authenticationToken = null)
    {
        _listenPort = listenPort;
        _serializer = serializer ?? new JsonEventSerializer();
        _eventAggregator = eventAggregator;

        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        _sessionOptions = new ResilientSessionOptions
        {
            RequireAuthentication = authenticationToken is not null,
            AuthenticationToken = authenticationToken,
            HeartbeatInterval = interval,
            HeartbeatTimeout = TimeSpan.FromTicks(interval.Ticks * 2),
        };

        _eventAggregator?.RegisterEventType<PublishFailed>();
        _eventAggregator?.SubscribeToEventType(new PublishFailedHandler());
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("The transport is already listening.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _server = new ResilientTcpServer(_listenPort, _sessionOptions);
        _server.MessageReceived += HandleInboundMessageAsync;
        foreach (var pair in _persistentSessions)
        {
            _server.RegisterPersistentClient(pair.Key, pair.Value);
        }
        return _server.StartAsync(cancellationToken);
    }

    public async Task ConnectToPeerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
        }

        var key = GetSessionKey(host, port);
        var session = _persistentSessions.GetOrAdd(key, k =>
        {
            var persistent = new PersistentSessionClient(
                host,
                port,
                OnPersistentClientConnected,
                _sessionOptions,
                _eventAggregator);
            return persistent;
        });

        _server?.RegisterPersistentClient(key, session);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var payload = _serializer.Serialize(domainEvent);
        _server?.QueueBroadcast(payload);

        foreach (var session in _persistentSessions.Values)
        {
            await session.EnqueueEventAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : class, IDomainEvent
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var bag = _handlers.GetOrAdd(typeof(T), _ => new ConcurrentBag<Func<object, CancellationToken, Task>>());
        bag.Add(async (obj, ct) => await handler((T)obj, ct).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (var task in _receiveTasks.Values)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is AggregateException or TimeoutException)
            {
                var exOrflattened = ex is AggregateException aggEx ? aggEx.Flatten() : ex;
                await Console.Error.WriteLineAsync($"Persistent receive loop faulted: {exOrflattened}");
            }
        }

        _receiveTasks.Clear();

        foreach (var session in _persistentSessions.Values)
        {
            await session.DisposeAsync();
        }

        _persistentSessions.Clear();

        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }

        if (_cts is not null)
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private Task HandleInboundMessageAsync(string sessionKey, string payload, CancellationToken cancellationToken)
        => DispatchEventAsync(payload, cancellationToken);

    private Task DispatchEventAsync(string payload, CancellationToken cancellationToken)
    {
        Type? eventType;
        IDomainEvent? domainEvent;
        try
        {

            (eventType, domainEvent) = _serializer.Deserialize(payload);
        }
        catch (JsonException ex)
        {
            return Console.Error.WriteLineAsync($"Failed to deserialize event envelope: {ex}");
        }

        if (eventType is null)
        {
            return Console.Error.WriteLineAsync($"Unknown or missing event type.");
        }

        if (domainEvent is null)
        {
            return Console.Error.WriteLineAsync($"missing event.");
        }

        if (!_handlers.TryGetValue(eventType, out var handlers) || handlers.Count == 0)
        {
            return Console.Error.WriteLineAsync($"No handlers found for the event type {eventType}");
        }

        return InvokeHandlersAsync(handlers, domainEvent, cancellationToken);
    }

    private static Task InvokeHandlersAsync(IEnumerable<Func<object, CancellationToken, Task>> handlers, object domainEvent, CancellationToken cancellationToken)
    {
        var tasks = handlers.Select(handler => handler(domainEvent, cancellationToken));
        return Task.WhenAll(tasks);
    }

    private Task OnPersistentClientConnected(PersistentSessionClient session, TcpClient client, CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            var key = GetSessionKey(session.RemoteEndPoint.Host, session.RemoteEndPoint.Port);
            _server.RegisterPersistentClient(key, session);
        }

        var receiveTask = Task.Run(() => RunReceiveLoopAsync(session, client, cancellationToken), cancellationToken);
        _receiveTasks[client] = receiveTask;
        receiveTask.ContinueWith(_ =>
        {
            _receiveTasks.TryRemove(client, out _);
            client.Dispose();
        }, TaskContinuationOptions.ExecuteSynchronously);

        return Task.CompletedTask;
    }

    private async Task RunReceiveLoopAsync(PersistentSessionClient session, TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    break;
                }

                await HandleSessionFrameAsync(session, result.Frame!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutdown requested
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            await Console.Error.WriteLineAsync($"Persistent receive loop terminated: {ex}").ConfigureAwait(false);
        }
    }

    private async Task HandleSessionFrameAsync(PersistentSessionClient session, SessionFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Message when frame.Payload is not null && frame.Id is long messageId:
                await DispatchEventAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                session.RecordRemoteActivity();
                session.EnqueueControlMessage(SessionFrame.CreateAck(messageId));
                break;
            case SessionFrameKind.Ack when frame.Id is long ackId:
                session.Acknowledge(ackId);
                session.RecordRemoteActivity();
                break;
            case SessionFrameKind.Ping:
                session.EnqueueControlMessage(SessionFrame.CreatePong());
                session.RecordRemoteActivity();
                break;
            case SessionFrameKind.Pong:
                session.RecordRemoteActivity();
                break;
            case SessionFrameKind.Auth:
                session.RecordRemoteActivity();
                break;
        }
    }

    private static string GetSessionKey(string host, int port) => $"{host}:{port}";

}
