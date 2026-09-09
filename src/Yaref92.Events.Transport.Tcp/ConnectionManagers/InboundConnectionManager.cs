using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Tcp.Abstractions;

namespace Yaref92.Events.Transport.Tcp.ConnectionManagers;

internal sealed partial class InboundConnectionManager : IInboundConnectionManager
{
    private readonly ConcurrentDictionary<TcpClient, Task<bool>> _receiveFramesTasks = new();
    private readonly ConcurrentDictionary<SessionKey, Lazy<Task>> _inboundInitialization = new();
    private readonly ConcurrentDictionary<SessionKey, Lazy<IInboundResilientConnection.SessionFrameReceivedHandler>> _sessionFrameHandlers = new();
    private readonly CancellationTokenSource _cts = new();

    public TcpSessionManager SessionManager { get; }
    public event Func<Guid, SessionKey, Task>? AckReceived;
    public event Func<SessionKey, Task>? PingReceived;

    public event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;

    internal event Func<IDomainEvent, SessionKey, Task>? EventReceived;

    event Func<IDomainEvent, SessionKey, Task> IInboundConnectionManager.EventReceived 
    {
        add => EventReceived += value;
        remove => EventReceived -= value;
    }

    private readonly IEventSerializer _serializer; // To deserialize incoming event frames
    private readonly Task? _monitorConnectionsLoop;
    private readonly PeerIngressLimiter _ingressLimiter;
    private readonly IngressDiagnostics _diagnostics;

    public InboundConnectionManager(
        TcpSessionManager sessionManager,
        IEventSerializer serializer,
        PeerIngressLimiter? ingressLimiter = null,
        IngressDiagnostics? diagnostics = null)
    {
        SessionManager = sessionManager;
        _serializer = serializer;
        _ingressLimiter = ingressLimiter ?? new PeerIngressLimiter(sessionManager.Options);
        _diagnostics = diagnostics ?? new IngressDiagnostics(null);
        _monitorConnectionsLoop = Task.Run(() => MonitorConnectionsAsync(_cts.Token), _cts.Token);
    }

    private async Task MonitorConnectionsAsync(CancellationToken monitorToken)
    {
        while (!monitorToken.IsCancellationRequested)
        {
            foreach (IInboundResilientConnection? inboundConnectionPastTimeout in SessionManager.AuthenticatedSessions.Concat(SessionManager.ValidAnonymousSessions)
                                                                                                           .DistinctBy(session => session.Key)
                                                                                                           .Select(session => (session as IResilientTcpSession).InboundConnection)
                                                                                                           .Where(inboundConnection => inboundConnection.IsPastTimeout))
            {
                _ = Task.Run(() => ReactToStaleConnection(inboundConnectionPastTimeout.SessionKey, monitorToken), monitorToken);
            }
            await Task.Delay(SessionManager.Options.HeartbeatInterval, monitorToken).ConfigureAwait(false);
        }
    }

    private async Task ReactToStaleConnection(SessionKey sessionKey, CancellationToken monitorToken)
    {
        IEventTransport.SessionInboundConnectionDroppedHandler? handler = SessionInboundConnectionDropped;
        if (handler is null)
        {
            return;
        }

        bool didManageToReconnect = await handler(sessionKey, monitorToken).ConfigureAwait(false);
        if (!didManageToReconnect)
        {
            // Connection could not be re-established
            // React to stale connection, e.g., notify session manager or log
            _diagnostics.Rejected("stale-session", sessionKey.Host);
        }
        // If connection was re-established, react accordingly...
        // ... but, technically nothing to do here since the session's InboundConnection would have been updated
    }

    public async Task<ConnectionInitializationResult> HandleIncomingTransientConnectionAsync(TcpClient incomingTransientConnection, CancellationToken serverToken)
    {
        var lengthBuffer = new byte[4];
        var initializationSucceeded = false;

        try
        {
            // During initialization, any pending authentication frame is processed. Even if auth is not required, this ensures proper session setup.
            ConnectionInitializationResult initialization = await InitializeConnectionAsync(incomingTransientConnection, lengthBuffer, serverToken).ConfigureAwait(false);

            if (initialization.IsSuccess && initialization.ConnectionCancellation is null)
            {
                throw new InvalidOperationException("Initialization succeeded without a cancellation source.");
            }

            initializationSucceeded = initialization.IsSuccess;

            return initialization;
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or NotSupportedException)
        {
            _diagnostics.Rejected("connection-error", GetPeer(incomingTransientConnection), exception: ex);
        }
        finally
        {
            if (!initializationSucceeded)
            {
                incomingTransientConnection.Dispose();
            }
        }

        return ConnectionInitializationResult.Failed();
    }

    private async Task<ConnectionInitializationResult> InitializeConnectionAsync(
        TcpClient transientIncomingConnection,
        byte[] lengthBuffer,
        CancellationToken serverToken)
    {
        NetworkStream stream = transientIncomingConnection.GetStream();
        var remoteEndPoint = transientIncomingConnection.Client.RemoteEndPoint;
        var peer = GetPeer(transientIncomingConnection);
        var authFrameResult = await SessionFrameIO.ReadFrameAsync(
            stream,
            lengthBuffer,
            serverToken,
            SessionManager.Options.MaxFrameBytes).ConfigureAwait(false);
        if (!authFrameResult.IsSuccess)
        {
            _diagnostics.Rejected("invalid-initial-frame", peer, authFrameResult.Failure.ToString());
            return ConnectionInitializationResult.Failed();
        }
        if (!_ingressLimiter.TryAcquire(remoteEndPoint, out peer))
        {
            _diagnostics.Rejected("peer-rate-limit", peer);
            return ConnectionInitializationResult.Failed();
        }

        var initialFrame = authFrameResult.Frame!;
        var shouldReplayInitialFrame = false;
        IResilientPeerSession session;
        try
        {
            if (initialFrame.Kind == SessionFrameKind.Auth)
            {
                session = SessionManager.ResolveSession(remoteEndPoint, initialFrame);
            }
            else if (!SessionManager.Options.RequireAuthentication)
            {
                session = SessionManager.ResolveFallbackSession(remoteEndPoint);
                shouldReplayInitialFrame = true;
            }
            else
            {
                _diagnostics.Rejected("authentication-required", peer);
                return ConnectionInitializationResult.Failed();
            }
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            _diagnostics.Rejected("authentication-failed", peer, exception: ex);
            return ConnectionInitializationResult.Failed();
        }

        if (session is null)
        {
            _diagnostics.Rejected("session-resolution-failed", peer);
            return ConnectionInitializationResult.Failed();
        }

        session.Touch();
        EnsureFrameHandlerSubscribed(session);

        await EnsureInboundConnectionInitializedAsync(session, serverToken).ConfigureAwait(false);

        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        IInboundResilientConnection inboundConnection = (session as IResilientTcpSession).InboundConnection;
        if (inboundConnection is Connections.ResilientInboundConnection resilientInboundConnection)
        {
            resilientInboundConnection.ConfigureIngress(remoteEndPoint, _ingressLimiter, _diagnostics);
        }
        await inboundConnection.AttachTransientConnection(transientIncomingConnection, connectionCts).ConfigureAwait(false);

        if (shouldReplayInitialFrame)
        {
            await OnFrameReceivedAsync(initialFrame, session.Key, serverToken).ConfigureAwait(false);
        }

        return ConnectionInitializationResult.Success(session, connectionCts);
    }

    private static string GetPeer(TcpClient client)
    {
        return client.Client.RemoteEndPoint is System.Net.IPEndPoint remoteEndPoint
            ? remoteEndPoint.Address.ToString()
            : "unknown";
    }

    private void EnsureFrameHandlerSubscribed(IResilientPeerSession session)
    {
        Lazy<IInboundResilientConnection.SessionFrameReceivedHandler> subscription = _sessionFrameHandlers.GetOrAdd(
            session.Key,
            _ => new Lazy<IInboundResilientConnection.SessionFrameReceivedHandler>(
                () => AttachFrameHandler(session),
                LazyThreadSafetyMode.ExecutionAndPublication));

        _ = subscription.Value;
    }

    private IInboundResilientConnection.SessionFrameReceivedHandler AttachFrameHandler(IResilientPeerSession session)
    {
        (session as IResilientTcpSession).FrameReceived += OnFrameReceivedAsync;

        if (session is ResilientTcpPeerSession resilientSession)
        {
            resilientSession.Disposed += OnSessionDisposed;
        }

        return OnFrameReceivedAsync;
    }

    private void OnSessionDisposed(ResilientTcpPeerSession session)
    {
        if (_sessionFrameHandlers.TryRemove(session.Key, out Lazy<IInboundResilientConnection.SessionFrameReceivedHandler>? subscription)
            && subscription.IsValueCreated)
        {
            (session as IResilientTcpSession).FrameReceived -= subscription.Value;
        }

        session.Disposed -= OnSessionDisposed;
    }

    private async Task EnsureInboundConnectionInitializedAsync(IResilientPeerSession session, CancellationToken serverToken)
    {
        Lazy<Task> initializer = _inboundInitialization.GetOrAdd(
            session.Key,
            _ => new Lazy<Task>(() => (session as IResilientTcpSession).InboundConnection.InitAsync(serverToken), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            await initializer.Value.ConfigureAwait(false);
        }
        catch
        {
            _inboundInitialization.TryRemove(session.Key, out _);
            throw;
        }
    }

    private async Task OnFrameReceivedAsync(SessionFrame frame, SessionKey sessionKey, CancellationToken cancellationToken)
    {
        SessionManager.TouchSession(sessionKey);
        switch (frame.Kind)
        {
            case SessionFrameKind.Event when frame.Payload is not null:
                await OnEventFrameReceievedAsync(frame, sessionKey);
                break;
            case SessionFrameKind.Ack when frame.Id != Guid.Empty:
                Func<Guid, SessionKey, Task>? ackReceived = AckReceived;
                if (ackReceived is not null)
                {
                    await ackReceived(frame.Id, sessionKey).ConfigureAwait(false);
                }
                break;
            case SessionFrameKind.Ping: // RESPOND WITH PONG USING Publisher/Transport
                Func<SessionKey, Task>? pingReceived = PingReceived;
                if (pingReceived is not null)
                {
                    await pingReceived(sessionKey).ConfigureAwait(false);
                }
                break;
            case SessionFrameKind.Pong: // Just touch the session, which already happened
                break;
        }
    }

    private async Task OnEventFrameReceievedAsync(SessionFrame frame, SessionKey sessionKey)
    {
        var sessionWithValidAuthentication = SessionManager.AuthenticatedSessions.Concat(SessionManager.ValidAnonymousSessions)
                      .DistinctBy(session => session.Key)
                      .FirstOrDefault(session => session.Key == sessionKey);
        if (sessionWithValidAuthentication == null)
        {
            _diagnostics.Rejected("invalid-session", sessionKey.Host);
            return;
        }
        sessionWithValidAuthentication.Touch();

        IDomainEvent? domainEvent;
        try
        {
            domainEvent = _serializer.Deserialize(frame.Payload!).domainEvent;
        }
        catch (Exception ex)
        {
            throw new IngressProtocolException("event-deserialization", ex);
        }

        if (domainEvent is not null)
        {
            Func<IDomainEvent, SessionKey, Task>? eventReceived = EventReceived;
            if (eventReceived is not null)
            {
                await eventReceived(domainEvent, sessionKey).ConfigureAwait(false);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        Task[] tasks = [.._receiveFramesTasks.Values.Cast<Task>(), _monitorConnectionsLoop ?? Task.CompletedTask];
        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Internal loops are expected to observe manager shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Rejected("manager-disposal-error", "unknown", exception: ex);
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
