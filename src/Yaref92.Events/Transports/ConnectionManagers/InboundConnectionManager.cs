using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports.ConnectionManagers;

internal sealed partial class InboundConnectionManager : IInboundConnectionManager
{
    private readonly ConcurrentDictionary<TcpClient, Task<bool>> _receiveFramesTasks = new();
    private static readonly JsonSerializerOptions EventEnvelopeSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _cts = new();

    public SessionManager SessionManager { get; }

    //public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;

    public event Func<SessionKey, CancellationToken, Task>? SessionLeft;

    public event Func<SessionKey, SessionFrame, CancellationToken, Task>? FrameReceived;

    public event Func<IDomainEvent, SessionKey, Task>? EventReceived;

    event Func<IDomainEvent, SessionKey, Task> IInboundConnectionManager.EventReceived 
    {
        add => EventReceived += value;
        remove => EventReceived -= value;
    }

    private readonly IEventSerializer _serializer; // To deserialize incoming event frames

    public InboundConnectionManager(SessionManager sessionManager, IEventSerializer serializer)
    {
        SessionManager = sessionManager;
        _serializer = serializer;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        var tasks = _receiveFramesTasks.Values.ToArray();
        await Task.WhenAll(tasks)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

        //foreach (var session in SessionManager.Sessions.Values)
        //{
        //    await session.DisposeAsync().ConfigureAwait(false);
        //}

        //_sessions.Clear();
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

    public async Task<ConnectionInitializationResult> HandleIncomingTransientConnectionAsync(TcpClient incomingTransientConnection, CancellationToken serverToken)
    {
        var lengthBuffer = new byte[4];

        try
        {
            // During initialization, any pending authentication frame is processed. Even if auth is not required, this ensures proper session setup.
            ConnectionInitializationResult initialization = await InitializeConnectionAsync(incomingTransientConnection, lengthBuffer, serverToken).ConfigureAwait(false);

            if (initialization.IsSuccess && initialization.ConnectionCancellation is null)
            {
                throw new InvalidOperationException("Initialization succeeded without a cancellation source.");
            }

            return initialization;
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException)
        {
            await Console.Error.WriteLineAsync($"{nameof(HandleIncomingTransientConnectionAsync)} error: {ex}").ConfigureAwait(false);
        }

        return ConnectionInitializationResult.Failed();
    }

    private async Task<ConnectionInitializationResult> InitializeConnectionAsync(
        TcpClient transientConnection,
        byte[] lengthBuffer,
        CancellationToken serverToken)
    {
        NetworkStream stream = transientConnection.GetStream();
        var authFrameResult = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, serverToken).ConfigureAwait(false);
        if (!authFrameResult.IsSuccess)
        {
            return ConnectionInitializationResult.Failed();
        }

        var authFrame = authFrameResult.Frame!;
        IResilientPeerSession session;
        try
        {
            session = SessionManager.ResolveSession(transientConnection.Client.RemoteEndPoint, authFrame);
            session.FrameReceived += OnFrameReceivedAsync; // When the InboundConnectionManager of the session receives a frame, we need to hook into the frame received event
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            // TODO Log failure due to authentication invalidation
            return ConnectionInitializationResult.Failed();
        }
        if (session is null)
        {
            // TODO Log failure due to session resolution failure
            return ConnectionInitializationResult.Failed();
        }

        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        await session.InboundConnection.AttachTransientConnection(transientConnection, connectionCts).ConfigureAwait(false);

        return ConnectionInitializationResult.Success(session, connectionCts);
    }

    private async Task OnFrameReceivedAsync(SessionFrame frame, SessionKey sessionKey, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Event when frame.Payload is not null:
                IDomainEvent? domainEvent = _serializer.Deserialize(frame.Payload).domainEvent;

                if (domainEvent is not null)
                {
                    await EventReceived?.Invoke(domainEvent, sessionKey)!;
                }
                break;
            case SessionFrameKind.Ack when frame.Id != Guid.Empty:
                Acknowledge(frame.Id);
                break;
            case SessionFrameKind.Ping:
                EnqueueFrame(SessionFrame.CreatePong());
                break;
        }
    }

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

    //internal SessionKey CreateFallbackSessionKey(EndPoint? endpoint) => SessionManager.CreateFallbackSessionKey(endpoint);

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

    //private static async Task RunReceiveLoopAsync(IResilientPeerSession session, NetworkStream stream, CancellationToken cancellationToken)
    //{
    //    while (!cancellationToken.IsCancellationRequested)
    //    {
    //        SessionFrame? frame = null;
    //        try
    //        {
    //            if (!session.OutboundBuffer.TryDequeue(out frame))
    //            {
    //                await session.OutboundBuffer.WaitAsync(cancellationToken).ConfigureAwait(false);
    //                continue;
    //            }

    //            await session.InboundConnection.(stream, frame, cancellationToken).ConfigureAwait(false);
    //        }
    //        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    //        {
    //            break;
    //        }
    //        catch (Exception ex) when (ex is IOException or SocketException)
    //        {
    //            if (frame is not null)
    //            {
    //                session.OutboundBuffer.Return(frame);
    //            }

    //            await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} send loop error: {ex}").ConfigureAwait(false);
    //            break;
    //        }
    //    }
    //}

    private static async Task WriteFrameAsync(NetworkStream stream, SessionFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    //private async Task OnSessionJoinedAsync(SessionKey key, CancellationToken cancellationToken)
    //{
    //    var handler = SessionConnectionAccepted;
    //    if (handler is null)
    //    {
    //        return;
    //    }

    //    try
    //    {
    //        await handler.Invoke(key, cancellationToken).ConfigureAwait(false);
    //    }
    //    catch (Exception ex)
    //    {
    //        await Console.Error.WriteLineAsync($"{nameof(InboundConnectionManager)} session join handler failed: {ex}")
    //            .ConfigureAwait(false);
    //    }
    //}

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

    public void RegisterIncomingSessionConnection(SessionKey sessionKey)
    {
        throw new NotImplementedException();
    }
}
