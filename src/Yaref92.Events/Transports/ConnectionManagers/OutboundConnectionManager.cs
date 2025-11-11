using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports.ConnectionManagers;

internal sealed class OutboundConnectionManager : IOutboundConnectionManager
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

    public void QueueEventBroadcast(Guid eventId, string eventEnvelopeJson)
    {
        ArgumentNullException.ThrowIfNull(eventEnvelopeJson);

        foreach (IResilientPeerSession session in SessionManager.AuthenticatedSessions)
        {
            session.OutboundConnection.EnqueueFrame(SessionFrame.CreateEventFrame(eventId, eventEnvelopeJson));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        var tasks = _heartbeatTasks.Values.ToArray();
        await Task.WhenAll(tasks)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var outboundConnection in SessionManager.AuthenticatedSessions.Select(session => session.OutboundConnection).Cast<ResilientSessionConnection>())
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

    //private (IResilientPeerSession Session, SessionFrame? PendingFrame) ResolveSession(TcpClient client, SessionFrame firstFrame)
    //{
    //    if (SessionFrameContract.TryValidateAuthentication(firstFrame, _options, out var sessionKey))
    //    {
    //        var token = firstFrame.Token;
    //        if (string.IsNullOrWhiteSpace(token))
    //        {
    //            return default;
    //        }

    //        if (!TryExtractSessionKey(token, out var parsedSessionKey))
    //        {
    //            if (_options.RequireAuthentication)
    //            {
    //                return default;
    //            }

    //            parsedSessionKey = CreateFallbackSessionKey(client.Client.RemoteEndPoint);
    //        }

    //        var resolvedKey = parsedSessionKey ?? sessionKey;
    //        if (resolvedKey is null)
    //        {
    //            return default;
    //        }

    //        IResilientPeerSession session = SessionManager.GetOrGenerate(resolvedKey);
    //        session.RegisterAuthentication();
    //        return (session, null);
    //    }

    //    if (_options.RequireAuthentication)
    //    {
    //        return default;
    //    }

    //    var fallbackKey = CreateFallbackSessionKey(client.Client.RemoteEndPoint);
    //    var existing = _sessions.GetOrAdd(fallbackKey, key => new SessionState(key));
    //    existing.RegisterAuthentication();
    //    return (existing, firstFrame);
    //}

    //internal static bool TryExtractSessionKey(string token, out SessionKey sessionKey)
    //{
    //    sessionKey = default!;
    //    var separatorIndex = token.LastIndexOf('-');
    //    var normalizedToken = separatorIndex > 0 ? token[..separatorIndex] : token;

    //    if (!SessionKey.TryParse(normalizedToken, out var parsed) || parsed is null)
    //    {
    //        return false;
    //    }

    //    sessionKey = parsed;
    //    return true;
    //}

    //internal SessionKey CreateFallbackSessionKey(EndPoint? endpoint)
    //{
    //    return SessionManager.CreateFallbackSessionKey(endpoint);
    //}

    private static async Task RunHeartbeatLoopAsync(IResilientPeerSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SessionFrame? frame = null;
            try
            {
                session.OutboundConnection.EnqueueFrame(SessionFrame.CreatePing());
                //await session.InboundConnection.WaitForPong();
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

    public Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task ConnectAsync(SessionKey sessionKey, CancellationToken cancellationToken)
    {
        var outboundConnection = SessionManager.GetOrGenerate(sessionKey).OutboundConnection;
        return outboundConnection.InitAsync(cancellationToken);
    }

    public void SendAck(Guid eventId, SessionKey sessionKey)
    {
        SessionManager.GetOrGenerate(sessionKey)
            .OutboundConnection
            .EnqueueFrame(SessionFrame.CreateAck(eventId));
    }
}
