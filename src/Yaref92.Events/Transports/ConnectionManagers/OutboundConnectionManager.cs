using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports.ConnectionManagers;

internal sealed class OutboundConnectionManager(SessionManager sessionManager) : IOutboundConnectionManager
{
    //private readonly ConcurrentDictionary<TcpClient, Task> _heartbeatTasks = new();
    //private readonly ConcurrentDictionary<TcpClient, Task> _sendTasks = new();

    private readonly CancellationTokenSource _cts = new();

    public SessionManager SessionManager { get; } = sessionManager;

    public void QueueEventBroadcast(Guid eventId, string eventEnvelopeJson)
    {
        ArgumentNullException.ThrowIfNull(eventEnvelopeJson);

        foreach (IOutboundResilientConnection? connection in SessionManager.AuthenticatedSessions
                                                                           .Concat(SessionManager.ValidAnonymousSessions)
                                                                           .DistinctBy(session => session.Key)
                                                                           .Select(session => session.OutboundConnection))
        {
            connection.EnqueueFrame(SessionFrame.CreateEventFrame(eventId, eventEnvelopeJson));
        }
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        foreach (var outboundConnection in SessionManager.AuthenticatedSessions.Select(session => session.OutboundConnection).Cast<ResilientOutboundConnection>())
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
            await Console.Error.WriteLineAsync($"{nameof(OutboundConnectionManager)} disposal failed: {ex}")
                .ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    //internal async Task HandleConnectionAsync(TcpClient outgoingTransientConnection, CancellationToken serverToken)
    //{
    //    var stream = outgoingTransientConnection.GetStream();
    //    var lengthBuffer = new byte[4];
    //    IResilientPeerSession? session = null;
    //    CancellationTokenSource? connectionCts = null;

    //    try
    //    {
    //        var initialization = await InitializeConnectionAsync(outgoingTransientConnection, stream, lengthBuffer, serverToken).ConfigureAwait(false);
    //        if (!initialization.IsSuccess)
    //        {
    //            return;
    //        }

    //        session = initialization.Session ??
    //            throw new InvalidOperationException("Initialization succeeded without a outboundConnection state.");
    //        connectionCts = initialization.ConnectionCancellation ??
    //            throw new InvalidOperationException("Initialization succeeded without a cancellation source.");

    //        //await OnSessionJoinedAsync(session.Key, serverToken).ConfigureAwait(false);

    //        //_heartbeatTasks[outgoingTransientConnection] = Task.Run(() =>
    //        //    RunHeartbeatLoopAsync(session, connectionCts.Token), serverToken);

    //        _sendTasks[outgoingTransientConnection] = Task.Run(() =>
    //            RunSendLoopAsync(session, connectionCts.Token), serverToken);
    //        _ = _sendTasks[outgoingTransientConnection].ContinueWith(task => _sendTasks.TryRemove(outgoingTransientConnection, out _), TaskContinuationOptions.ExecuteSynchronously);
    //    }
    //    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
    //    {
    //        // shutting down
    //    }
    //    catch (Exception ex) when (ex is IOException or SocketException or JsonException)
    //    {
    //        await Console.Error.WriteLineAsync($"{nameof(HandleConnectionAsync)} error: {ex}").ConfigureAwait(false);
    //    }
    //    finally
    //    {
    //        outgoingTransientConnection.Dispose();
    //    }
    //}

    //private async Task<ConnectionInitializationResult> InitializeConnectionAsync(
    //    TcpClient client,
    //    NetworkStream stream,
    //    byte[] lengthBuffer,
    //    CancellationToken serverToken)
    //{
    //    var session = ResolveSession(client);
    //    if (session is null)
    //    {
    //        return ConnectionInitializationResult.Failed();
    //    }

    //    if (_pendingPersistentClients.TryRemove(session.Key, out var persistent))
    //    {
    //        session.OutboundConnection.AttachPersistentClient(persistent);
    //    }

    //    var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
    //    await session.AttachAsync(client, stream, connectionCts, RunSendLoopAsync).ConfigureAwait(false);

    //    return ConnectionInitializationResult.Success(session, connectionCts, null);
    //}

    //private static async Task RunSendLoopAsync(IResilientPeerSession session, CancellationToken cancellationToken)//touched
    //{
    //    while (!cancellationToken.IsCancellationRequested)
    //    {
    //        try
    //        {
    //            if (!session.OutboundBuffer.TryDequeue(out SessionFrame? frame))
    //            {
    //                await session.OutboundBuffer.WaitAsync(cancellationToken).ConfigureAwait(false);
    //                continue;
    //            }

    //            session.OutboundConnection.EnqueueFrame(frame);
    //            WaitForAckIfEventFrame(session, frame, cancellationToken);
    //        }
    //        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    //        {
    //            break;
    //        }
    //        catch (Exception ex) when (ex is IOException or SocketException)
    //        {
    //            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} send loop error: {ex}").ConfigureAwait(false);
    //            break;
    //        }
    //    }
    //}

    private static void WaitForAckIfEventFrame(IResilientPeerSession session, SessionFrame frame, CancellationToken cancellationToken)
    {
        if (frame is {Kind: SessionFrameKind.Event} eventFrame)
        {
            Task<AcknowledgementState> ackTask = Task.Run(() => session.InboundConnection.WaitForAck(eventFrame.Id, cancellationToken));
            _ = ackTask.ContinueWith(task =>
            {
                AcknowledgementState acknowledgementState = AcknowledgementState.None;

                if (task.IsCompletedSuccessfully)
                {
                    acknowledgementState = task.Result;
                }
                _ = ProcessAcknowledgementState(session, acknowledgementState, eventFrame).ConfigureAwait(false);
            }, TaskContinuationOptions.ExecuteSynchronously); 
        }
    }

    private static async Task ProcessAcknowledgementState(IResilientPeerSession session, AcknowledgementState acknowledgementState, SessionFrame? frame)
    {
        if (acknowledgementState != AcknowledgementState.Acknowledged && frame is not null)
        {
            session.OutboundBuffer.Return(frame);
            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} send loop did not receive ack for frame {frame}").ConfigureAwait(false);
        }
        else if (acknowledgementState == AcknowledgementState.Acknowledged && frame is {Kind: SessionFrameKind.Event} eventFrame)
        {
            session.OutboundBuffer.TryAcknowledge(eventFrame.Id);
        }
    }

    internal void EnqueueAck(Guid eventId, SessionKey sessionKey)
    {
        var session = SessionManager.GetOrGenerate(sessionKey);
        session.OutboundBuffer.EnqueueFrame(SessionFrame.CreateAck(eventId));
    }

    public Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken)
    {
        SessionKey sessionKey = new(userId, host, port)
        {
            IsAnonymousKey = userId == Guid.Empty,
        };
        if (sessionKey.IsAnonymousKey)
        {
            SessionManager.HydrateAnonymousSessionId(sessionKey, new DnsEndPoint(host, port));
        }
        return ConnectAsync(sessionKey, cancellationToken);
    }

    public Task ConnectAsync(SessionKey sessionKey, CancellationToken cancellationToken)
    {
        var outboundConnection = SessionManager.GetOrGenerate(sessionKey).OutboundConnection;
        return outboundConnection.InitAsync(cancellationToken);
    }

    public async Task<bool> TryReconnectAsync(SessionKey sessionKey, CancellationToken token)
    {
        var outboundConnection = SessionManager.GetOrGenerate(sessionKey).OutboundConnection;
        return await outboundConnection.RefreshConnectionAsync(token).ConfigureAwait(false);
    }

    public void SendAck(Guid eventId, SessionKey sessionKey)
    {
        SessionManager.GetOrGenerate(sessionKey)
            .OutboundConnection
            .EnqueueFrame(SessionFrame.CreateAck(eventId));
    }

    public Task OnAckReceived(Guid eventId, SessionKey sessionKey)
    {
        SessionManager.GetOrGenerate(sessionKey).OutboundConnection.OnAckReceived(eventId);
        return Task.CompletedTask;
    }
}
