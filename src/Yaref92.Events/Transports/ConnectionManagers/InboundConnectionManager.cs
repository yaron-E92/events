using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports.ConnectionManagers;

public sealed class InboundConnectionManager : IAsyncDisposable
{
    private readonly ResilientSessionOptions _options;
    //private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _receiveFramesTasks = new();
    private static readonly JsonSerializerOptions EventEnvelopeSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<SessionKey, IInboundResilientConnection> _pendingInboundConnections = new();
    private readonly ConcurrentDictionary<SessionKey, IInboundResilientConnection> _registeredInboundConnections = new();
    private readonly ConcurrentDictionary<IPEndPoint, Guid> _anonymousSessionIds = new();
    private readonly CancellationTokenSource _cts = new();

    public SessionManager SessionManager { get; }

    //private Task? _acceptLoop;
    //private Task? _monitorLoop;

    public event Func<SessionKey, CancellationToken, Task>? SessionJoined;

    public event Func<SessionKey, CancellationToken, Task>? SessionLeft;

    public event Func<SessionKey, SessionFrame, CancellationToken, Task>? FrameReceived;

    public InboundConnectionManager(ResilientSessionOptions? options = null, SessionManager sessionManager)
    {
        SessionManager = sessionManager;
        _options = options ?? new ResilientSessionOptions();
    }

    public void RegisterPersistentSession(IResilientPeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _sessions.AddOrUpdate(session.Key,
            key => session,
            (key, existingSession) => existingSession.InboundConnection.Init())

        if (!_sessions.ContainsKey(session.Key))
        {
            _sessions[session.Key] = session;
        }
        session.AttachResilientConnection()
    }

    public IInboundResilientConnection GetOrCreateInboundConnection(
        SessionKey key,
        Func<SessionKey, ResilientSessionConnection> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(clientFactory);

        if (_sessions.TryGetValue(key, out var session) && session.InboundConnection is { } existingClient)
        {
            return existingClient;
        }

        var client = _pendingInboundConnections.GetOrAdd(key, static (sessionKey, factory) =>
        {
            var created = factory(sessionKey);
            ArgumentNullException.ThrowIfNull(created);
            return created;
        }, clientFactory);

        if (_sessions.TryGetValue(key, out var currentSession))
        {
            currentSession.AttachPersistentClient(client);
        }

        return client;
    }

    //public Task StartAsync(CancellationToken cancellationToken = default)
    //{
    //    if (_listener is not null)
    //    {
    //        throw new InvalidOperationException("Listener already started.");
    //    }

    //    cancellationToken.ThrowIfCancellationRequested();

    //    _listener = new TcpListener(IPAddress.Any, _port);
    //    _listener.Start();

    //    _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
    //    _monitorLoop = Task.Run(() => MonitorConnectionsAsync(_cts.Token), _cts.Token);

    //    return Task.CompletedTask;
    //}

    public void QueueBroadcast(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!TryExtractEventId(payload, out var eventId))
        {
            throw new InvalidOperationException("Broadcast payload is missing a valid event identifier.");
        }

        foreach (var session in _sessions.Values.Where(static session => session.HasAuthenticated))
        {
            session.Outbound.EnqueueEvent(eventId, payload);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        var tasks = _receiveFramesTasks.Values.ToArray();
        await Task.WhenAll(tasks)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} disposal failed: {ex}")
                .ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private static bool TryExtractEventId(string payload, out Guid eventId)
    {
        eventId = Guid.Empty;

        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(payload, EventEnvelopeSerializerOptions);
            if (envelope is { EventId: var id } && id != Guid.Empty)
            {
                eventId = id;
                return true;
            }
        }
        catch (JsonException)
        {
            // invalid payload
        }

        return false;
    }

    internal async Task HandleConnectionAsync(TcpClient incomingTransientConnection, CancellationToken serverToken)
    {
        SessionKey sessionKey = ResolveSession
        SessionManager.GetOrGenerate();
        var stream = incomingTransientConnection.GetStream();
        var lengthBuffer = new byte[4];
        SessionState? sessionState = null;
        CancellationTokenSource? connectionCts = null;

        try
        {
            var initialization = await InitializeConnectionAsync(incomingTransientConnection, stream, lengthBuffer, serverToken).ConfigureAwait(false);
            if (!initialization.IsSuccess)
            {
                return;
            }

            sessionState = initialization.Session ??
                throw new InvalidOperationException("Initialization succeeded without a session state.");
            connectionCts = initialization.ConnectionCancellation ??
                throw new InvalidOperationException("Initialization succeeded without a cancellation source.");

            await OnSessionJoinedAsync(sessionState.Key, serverToken).ConfigureAwait(false);

            var pendingFrame = initialization.PendingFrame;
            if (pendingFrame is not null)
            {
                await ProcessFrameAsync(sessionState, pendingFrame, connectionCts.Token).ConfigureAwait(false);
            }
            _receiveFramesTasks[incomingTransientConnection] = Task.Run(() =>
                ProcessIncomingFramesAsync(sessionState, stream, lengthBuffer, connectionCts.Token), serverToken);
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException)
        {
            await Console.Error.WriteLineAsync($"{nameof(HandleConnectionAsync)} error: {ex}").ConfigureAwait(false);
        }
        finally
        {
            if (sessionState is not null)
            {
                await sessionState.CloseConnectionAsync().ConfigureAwait(false);
                await OnSessionLeftAsync(sessionState.Key, serverToken).ConfigureAwait(false);
            }

            incomingTransientConnection.Dispose();
        }
    }

    private async Task<ConnectionInitializationResult> InitializeConnectionAsync(
        TcpClient client,
        NetworkStream stream,
        byte[] lengthBuffer,
        CancellationToken serverToken)
    {
        var firstFrameResult = await SessionFrameIO.ReadFrameAsync(client.GetStream(), lengthBuffer, serverToken).ConfigureAwait(false);
        if (!firstFrameResult.IsSuccess)
        {
            return ConnectionInitializationResult.Failed();
        }

        var firstFrame = firstFrameResult.Frame!;
        var (session, pendingFrame) = ResolveSession(client, firstFrame);
        if (session is null)
        {
            return ConnectionInitializationResult.Failed();
        }

        if (_pendingInboundConnections.TryRemove(session.Key, out var persistent))
        {
            session.AttachPersistentClient(persistent);
        }

        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        await session.AttachAsync(client, stream, connectionCts, RunSendLoopAsync).ConfigureAwait(false);

        return ConnectionInitializationResult.Success(session, connectionCts, pendingFrame);
    }

    private (IResilientPeerSession? Session, SessionFrame? PendingFrame) ResolveSession(TcpClient client, SessionFrame firstFrame)
    {
        if (SessionFrameContract.TryValidateAuthentication(firstFrame, _options, out var sessionKey))
        {
            var token = firstFrame.Token;
            if (string.IsNullOrWhiteSpace(token))
            {
                return default;
            }

            if (!TryExtractSessionKey(token, out var parsedSessionKey))
            {
                if (_options.RequireAuthentication)
                {
                    return default;
                }

                parsedSessionKey = CreateFallbackSessionKey(client.Client.RemoteEndPoint);
            }

            var resolvedKey = parsedSessionKey ?? sessionKey;
            if (resolvedKey is null)
            {
                return default;
            }

            var session = _sessions.GetOrAdd(resolvedKey, key => new SessionState(key));
            session.RegisterAuthentication();
            return (session, null);
        }

        if (_options.RequireAuthentication)
        {
            return default;
        }

        var fallbackKey = CreateFallbackSessionKey(client.Client.RemoteEndPoint);
        var existing = _sessions.GetOrAdd(fallbackKey, key => new SessionState(key));
        existing.RegisterAuthentication();
        return (existing, firstFrame);
    }

    internal static bool TryExtractSessionKey(string token, out SessionKey sessionKey)
    {
        sessionKey = default!;
        var separatorIndex = token.LastIndexOf('-');
        var normalizedToken = separatorIndex > 0 ? token[..separatorIndex] : token;

        if (!SessionKey.TryParse(normalizedToken, out var parsed) || parsed is null)
        {
            return false;
        }

        sessionKey = parsed;
        return true;
    }

    internal SessionKey CreateFallbackSessionKey(EndPoint? endpoint) => SessionManager.CreateFallbackSessionKey(endpoint);

    private async Task ProcessIncomingFramesAsync(
        SessionState session,
        NetworkStream stream,
        byte[] lengthBuffer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                break;
            }

            await ProcessFrameAsync(session, result.Frame!, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessFrameAsync(SessionState session, SessionFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Ping: // RESPOND WITH PONG USING Publisher/Transport
                session.Touch();
                session.Outbound.EnqueueFrame(SessionFrame.CreatePong());
                break;
            case SessionFrameKind.Pong: // Just touch the session
                session.Touch();
                break;
            case SessionFrameKind.Ack when frame.Id != Guid.Empty: // Ensure that the outbound message for this session key is acknowledged and removed
                var ackId = frame.Id;
                if (session.Outbound.TryAcknowledge(ackId))
                {
                    session.Touch();
                }
                session.PersistentClient?.Acknowledge(ackId);
                break;
            case SessionFrameKind.Event when frame.Payload is not null: // Process event
                session.Touch();

                var messageId = frame.Id;
                if (messageId == Guid.Empty)
                {
                    break;
                }

                try
                {
                    var handler = FrameReceived;
                    if (handler is not null)
                    {
                        await handler(session.Key, frame, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} frame handler failed: {ex}")
                        .ConfigureAwait(false);
                }
                finally
                {
                    session.Outbound.EnqueueFrame(SessionFrame.CreateAck(messageId));
                    session.PersistentClient?.RecordRemoteActivity();
                }

                break;
        }
    }

    private static async Task RunSendLoopAsync(SessionState session, NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SessionFrame? frame = null;
            try
            {
                if (!session.Outbound.TryDequeue(out frame))
                {
                    await session.Outbound.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WriteFrameAsync(stream, frame, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                if (frame is not null)
                {
                    session.Outbound.Return(frame);
                }

                await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} send loop error: {ex}").ConfigureAwait(false);
                break;
            }
        }
    }

    private static async Task WriteFrameAsync(NetworkStream stream, SessionFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnSessionJoinedAsync(SessionKey key, CancellationToken cancellationToken)
    {
        var handler = SessionJoined;
        if (handler is null)
        {
            return;
        }

        try
        {
            await handler.Invoke(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} session join handler failed: {ex}")
                .ConfigureAwait(false);
        }
    }

    private async Task OnSessionLeftAsync(SessionKey key, CancellationToken cancellationToken)
    {
        var handler = SessionLeft;
        if (handler is null)
        {
            return;
        }

        try
        {
            await handler.Invoke(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} session leave handler failed: {ex}")
                .ConfigureAwait(false);
        }
    }

    internal readonly record struct ConnectionInitializationResult(
        bool IsSuccess,
        SessionState? Session,
        CancellationTokenSource? ConnectionCancellation,
        SessionFrame? PendingFrame)
    {
        public static ConnectionInitializationResult Success(
            SessionState session,
            CancellationTokenSource cancellation,
            SessionFrame? pendingFrame) => new(true, session, cancellation, pendingFrame);

        public static ConnectionInitializationResult Failed() => new(false, null, null, null);
    }
}
