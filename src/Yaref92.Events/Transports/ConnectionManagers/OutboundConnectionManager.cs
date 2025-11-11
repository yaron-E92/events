using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports.ConnectionManagers;

public sealed class OutboundConnectionManager : IAsyncDisposable
{
    private readonly ResilientSessionOptions _options;
    //private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _heartbeatTasks = new();
    private ConcurrentDictionary<TcpClient, Task> _sendTasks = new();
    private static readonly JsonSerializerOptions EventEnvelopeSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<SessionKey, IOutboundResilientConnection> _pendingPersistentClients = new();
    private readonly ConcurrentDictionary<IPEndPoint, Guid> _anonymousSessionIds = new();
    private readonly CancellationTokenSource _cts = new();

    public event Func<SessionKey, CancellationToken, Task>? SessionJoined;

    public event Func<SessionKey, CancellationToken, Task>? SessionLeft;

    public OutboundConnectionManager(ResilientSessionOptions? options = null, SessionManager sessionManager = null)
    {
        _options = options ?? new ResilientSessionOptions();
        SessionManager = sessionManager;
    }

    //public ConcurrentDictionary<SessionKey, IResilientPeerSession> Sessions
    //{
    //    get
    //    {
    //        ConcurrentDictionary<SessionKey, IResilientPeerSession> copy = new();
    //        foreach (var kvp in _sessions)
    //        {
    //            copy[kvp.Key] = kvp.Value;
    //        }

    //        return copy;
    //    }
    //}

    public SessionManager SessionManager { get; }

    public IOutboundResilientConnection GetOrCreateResilientConnection(
        SessionKey key,
        Func<SessionKey, ResilientSessionConnection> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(clientFactory);

        IResilientPeerSession session = SessionManager.GetOrGenerate(key);

        if (session.OutboundConnection is { } existingClient)
        {
            return existingClient;
        }

        var connection = session.OutboundConnection ?? _pendingPersistentClients.GetOrAdd(key, static (sessionKey, factory) =>
        {
            var created = factory(sessionKey);
            ArgumentNullException.ThrowIfNull(created);
            return created;
        }, clientFactory);

        session.AttachResilientConnection((ResilientSessionConnection) connection);

        return connection;
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

        foreach (IResilientPeerSession session in SessionManager.AuthenticatedSessions)
        {
            session.EnqueueEvent(eventId, payload);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        var tasks = _heartbeatTasks.Values.ToArray();
        await Task.WhenAll(tasks)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var outboundConnection in SessionManager.OutboundConnections.Cast<ResilientSessionConnection>())
        {
            await outboundConnection.DisposeAsync().ConfigureAwait(false);
        }
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

    internal async Task HandleConnectionAsync(TcpClient outgoingTransientConnection, CancellationToken serverToken)
    {
        var stream = outgoingTransientConnection.GetStream();
        var lengthBuffer = new byte[4];
        IResilientPeerSession? session = null;
        CancellationTokenSource? connectionCts = null;

        try
        {
            var initialization = await InitializeConnectionAsync(outgoingTransientConnection, stream, lengthBuffer, serverToken).ConfigureAwait(false);
            if (!initialization.IsSuccess)
            {
                return;
            }

            session = initialization.Session ??
                throw new InvalidOperationException("Initialization succeeded without a outboundConnection state.");
            connectionCts = initialization.ConnectionCancellation ??
                throw new InvalidOperationException("Initialization succeeded without a cancellation source.");

            await OnSessionJoinedAsync(session.Key, serverToken).ConfigureAwait(false);

            _heartbeatTasks[outgoingTransientConnection] = Task.Run(() =>
                RunHeartbeatLoopAsync(session, stream, lengthBuffer, connectionCts.Token), serverToken);

            _sendTasks[outgoingTransientConnection] = Task.Run(() =>
                RunSendLoopAsync(session, connectionCts.Token), serverToken);
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
            if (session is not null)
            {
                await session.CloseConnectionAsync().ConfigureAwait(false);
                await OnSessionLeftAsync(session.Key, serverToken).ConfigureAwait(false);
            }

            outgoingTransientConnection.Dispose();
        }
    }

    private async Task<ConnectionInitializationResult> InitializeConnectionAsync(
        TcpClient client,
        NetworkStream stream,
        byte[] lengthBuffer,
        CancellationToken serverToken)
    {
        var session = ResolveSession(client);
        if (session is null)
        {
            return ConnectionInitializationResult.Failed();
        }

        if (_pendingPersistentClients.TryRemove(session.Key, out var persistent))
        {
            session.AttachPersistentClient(persistent);
        }

        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        await session.AttachAsync(client, stream, connectionCts, RunSendLoopAsync).ConfigureAwait(false);

        return ConnectionInitializationResult.Success(session, connectionCts, null);
    }

    private IResilientPeerSession ResolveSession(TcpClient client)
    {
        throw new NotImplementedException();
    }

    private (SessionState? Session, SessionFrame? PendingFrame) ResolveSession(TcpClient client, SessionFrame firstFrame)
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

    internal SessionKey CreateFallbackSessionKey(EndPoint? endpoint)
    {
        if (endpoint is IPEndPoint ipEndPoint)
        {
            IPEndPoint key = new(ipEndPoint.Address, ipEndPoint.Port);
            var identifier = _anonymousSessionIds.GetOrAdd(key, static _ => Guid.NewGuid());
            var host = key.Address.ToString();
            return new SessionKey(identifier, host, key.Port);
        }

        var fallbackHost = endpoint switch
        {
            DnsEndPoint dns when !string.IsNullOrWhiteSpace(dns.Host) => dns.Host,
            _ => IPAddress.Any.ToString(),
        };

        var fallbackPort = endpoint switch
        {
            DnsEndPoint dns when dns.Port > 0 => dns.Port,
            _ => _port,
        };
        //IPEndPoint ipKey =  new IPEndPoint(new IpAddress(fallbackHost), fallbackPort);
        //Guid sessionId = _anonymousSessionIds.GetOrAdd(ipKey, static _ => Guid.NewGuid());

        return new SessionKey(Guid.NewGuid(), fallbackHost, fallbackPort);
    }

    private static async Task RunHeartbeatLoopAsync(IResilientPeerSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SessionFrame? frame = null;
            try
            {
                

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
                    session.OutboundBuffer.Return(frame);
                }

                await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} send loop error: {ex}").ConfigureAwait(false);
                break;
            }
        }
    }

    private static async Task RunSendLoopAsync(IResilientPeerSession session, CancellationToken cancellationToken)//touched
    {
        SessionFrame? frame;
        while (!cancellationToken.IsCancellationRequested)
        {
            frame = null;
            try
            {
                if (!session.OutboundBuffer.TryDequeue(out frame))
                {
                    await session.OutboundBuffer.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                session.OutboundConnection.EnqueueFrame(frame);
                Task<AcknowledgementState> ackTask = Task.Run(() => session.InboundConnection.WaitForAck(frame.Id, cancellationToken));
                _ = ackTask.ContinueWith(task =>
                {
                    AcknowledgementState acknowledgementState = AcknowledgementState.None;
        
                    if (task.IsCompletedSuccessfully)
                    {
                        acknowledgementState = task.Result;
                    }
                    _ = ProcessAcknowledgementState(session, acknowledgementState, frame).ConfigureAwait(false);
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} send loop error: {ex}").ConfigureAwait(false);
                break;
            }
        }
    }

    private static async Task ProcessAcknowledgementState(IResilientPeerSession session, AcknowledgementState acknowledgementState, SessionFrame? frame)
    {
        if (acknowledgementState != AcknowledgementState.Acknowledged && frame is not null)
        {
            session.OutboundBuffer.Return(frame);
            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} send loop did not receive ack for frame {frame}").ConfigureAwait(false);
        }
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
            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} outboundConnection join handler failed: {ex}")
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
            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} outboundConnection leave handler failed: {ex}")
                .ConfigureAwait(false);
        }
    }

    internal void EnqueueAck(Guid eventId, SessionKey sessionKey)
    {
        var session = SessionManager.GetOrGenerate(sessionKey);
        session.OutboundBuffer.EnqueueFrame(SessionFrame.CreateAck(eventId));
        session.OutboundConnection.EnqueueFrame(SessionFrame.CreateAck(eventId));
    }

    internal readonly record struct ConnectionInitializationResult(
        bool IsSuccess,
        IResilientPeerSession? Session,
        CancellationTokenSource? ConnectionCancellation,
        SessionFrame? PendingFrame)
    {
        public static ConnectionInitializationResult Success(
            IResilientPeerSession session,
            CancellationTokenSource cancellation,
            SessionFrame? pendingFrame) => new(true, session, cancellation, pendingFrame);

        public static ConnectionInitializationResult Failed() => new(false, null, null, null);
    }
}
