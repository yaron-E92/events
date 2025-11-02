using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Sessions.Events;

namespace Yaref92.Events.Transports;

public sealed class ResilientTcpServer : IAsyncDisposable, IAsyncEventHandler<SessionJoined>, IAsyncEventHandler<SessionLeft>
{
    private readonly int _port;
    private readonly ResilientSessionOptions _options;
    private readonly ConcurrentDictionary<SessionKey, SessionState> _sessionStates = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _clientTasks = new();
    private readonly ConcurrentDictionary<SessionKey, ResilientSessionClient> _pendingPersistentClients = new();
    private readonly CancellationTokenSource _cts = new();

    private Func<string, string, CancellationToken, Task>? _messageReceivedHandler;
    private Func<string, CancellationToken, Task>? _sessionJoinedHandler;
    private Func<string, CancellationToken, Task>? _sessionLeftHandler;

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _monitorLoop;

    public ConcurrentDictionary<SessionKey, SessionState> Sessions
    {
        get {
                ConcurrentDictionary<SessionKey, SessionState> copy = new();
                foreach (var kvp in _sessionStates)
                {
                    copy[kvp.Key] = kvp.Value;
                }
                return copy;
        }
    }

    public ResilientTcpServer(
        int port,
        ResilientSessionOptions? options = null,
        Func<string, string, CancellationToken, Task>? messageReceivedHandler = null)
    {
        _port = port;
        _options = options ?? new ResilientSessionOptions();
        _messageReceivedHandler = messageReceivedHandler;
    }

    public void RegisterPersistentClient(SessionKey key, ResilientSessionClient client)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (_sessionStates.TryGetValue(key, out var session))
        {
            session.AttachPersistentClient(client);
        }
        else
        {
            _pendingPersistentClients[key] = client;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("Server already started.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        _monitorLoop = Task.Run(() => MonitorSessionsAsync(_cts.Token), _cts.Token);

        return Task.CompletedTask;
    }

    public void QueueBroadcast(string payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        foreach (var session in _sessionStates.Values.Where(static session => session.HasAuthenticated))
        {
            session.EnqueueMessage(payload);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        var tasks = _clientTasks.Values.ToArray();
        await Task.WhenAll(tasks.Append(_acceptLoop ?? Task.CompletedTask).Append(_monitorLoop ?? Task.CompletedTask)).WaitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var session in _sessionStates.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _sessionStates.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(ResilientTcpServer)} disposal failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{nameof(AcceptLoopAsync)} failed: {ex}").ConfigureAwait(false);
                continue;
            }

            if (client is null)
            {
                continue;
            }

            var task = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            _clientTasks[client] = task;
            await task.ContinueWith(_ => _clientTasks.TryRemove(client, out _), TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];
        SessionState sessionState = null;
        SessionConnection connection = null;
        CancellationTokenSource connectionCts = null;

        try
        {
            var initialization = await InitializeSessionAsync(client, stream, lengthBuffer, serverToken).ConfigureAwait(false);
            if (!initialization.IsSuccess)
            {
                return;
            }


            sessionState = initialization.Session ?? throw new InvalidOperationException("Initialization succeeded without a sessionState.");
            connection = initialization.Connection ?? throw new InvalidOperationException("Initialization succeeded without a connection.");
            connectionCts = initialization.ConnectionCancellation ?? throw new InvalidOperationException("Initialization succeeded without a cancellation key source.");
            SessionJoined sessionJoined = new(sessionState.Key);
            await OnNextAsync(sessionJoined, serverToken).ConfigureAwait(false);

            if (_sessionJoinedHandler is not null)
            {
                try
                {
                    await _sessionJoinedHandler.Invoke(sessionState.Key, serverToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"{nameof(ResilientTcpServer)} sessionState join handler failed: {ex}").ConfigureAwait(false);
                }
            }

            var pendingFrame = initialization.PendingFrame;
            if (pendingFrame is not null)
            {
                await ProcessFrameAsync( initialization.Session ?? throw new InvalidOperationException("Initialization succeeded without a sessionState."), pendingFrame, (initialization.ConnectionCancellation ?? throw new InvalidOperationException("Initialization succeeded without a cancellation key source.")).Token).ConfigureAwait(false);
            }

            await ProcessIncomingFramesAsync( initialization.Session ?? throw new InvalidOperationException("Initialization succeeded without a sessionState."), stream, lengthBuffer, (initialization.ConnectionCancellation ?? throw new InvalidOperationException("Initialization succeeded without a cancellation key source.")).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException)
        {
            await Console.Error.WriteLineAsync($"{nameof(HandleClientAsync)} error: {ex}").ConfigureAwait(false);
        }
        finally
        {
            if (connectionCts is not null)
            {
                await connectionCts.CancelAsync().ConfigureAwait(false);
                connectionCts.Dispose();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            sessionState?.Detach();
            if (sessionState is not null && _sessionLeftHandler is not null)
            {
                try
                {
                    await _sessionLeftHandler.Invoke(sessionState.Key, serverToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"{nameof(ResilientTcpServer)} sessionState leave handler failed: {ex}").ConfigureAwait(false);
                }
            }
            client.Dispose();
        }
    }

    private async Task<SessionInitializationResult> InitializeSessionAsync(
        TcpClient client,
        NetworkStream stream,
        byte[] lengthBuffer,
        CancellationToken serverToken)
    {
        var firstFrameResult = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, serverToken).ConfigureAwait(false);
        if (!firstFrameResult.IsSuccess)
        {
            return SessionInitializationResult.Failed();
        }

        var firstFrame = firstFrameResult.Frame!;
        var (session, pendingFrame) = ResolveSession(client, firstFrame);
        if (session is null)
        {
            return SessionInitializationResult.Failed();
        }

        if (_pendingPersistentClients.TryRemove(session.Key, out var persistent))
        {
            session.AttachPersistentClient(persistent);
        }

        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var connection = await session.AttachAsync(client, connectionCts.Token, stream, RunSendLoopAsync).ConfigureAwait(false);

        return SessionInitializationResult.Success(session, connection, connectionCts, pendingFrame);
    }

    private (SessionState? Session, SessionFrame? PendingFrame) ResolveSession(TcpClient client, SessionFrame firstFrame)
    {
        if (SessionFrameContract.TryValidateAuthentication(firstFrame, _options, out var sessionKey))
        {
            var session = _sessionStates.GetOrAdd(sessionKey, key => new SessionState(key));
            session.RegisterAuthentication();
            return (session, null);
        }

        if (_options.RequireAuthentication)
        {
            return default;
        }

        var fallbackKey = CreateFallbackSessionKey(client);
        var existing = _sessionStates.GetOrAdd(fallbackKey, key => new SessionState(key));
        existing.RegisterAuthentication();
        return (existing, firstFrame);
    }

    private static SessionKey CreateFallbackSessionKey(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is IPEndPoint endPoint)
        {
            var port = endPoint.Port <= 0 ? 1 : endPoint.Port;
            return new SessionKey(Guid.Empty, endPoint.Address.ToString(), port);
        }

        var remoteText = client.Client.RemoteEndPoint?.ToString();
        if (!string.IsNullOrWhiteSpace(remoteText))
        {
            var separatorIndex = remoteText.LastIndexOf(':');
            if (separatorIndex > 0 && separatorIndex < remoteText.Length - 1 &&
                int.TryParse(remoteText[(separatorIndex + 1)..], out var port))
            {
                var host = remoteText[..separatorIndex];
                return new SessionKey(Guid.Empty, host, port <= 0 ? 1 : port);
            }

            return new SessionKey(Guid.Empty, remoteText, 1);
        }

        return new SessionKey(Guid.NewGuid(), "unknown", 1);
    }

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
            case SessionFrameKind.Ping:
                session.Touch();
                session.EnqueueControl(SessionFrame.CreatePong());
                break;
            case SessionFrameKind.Pong:
                session.Touch();
                break;
            case SessionFrameKind.Ack when frame.Id != Guid.Empty:
                var ackId = frame.Id;
                session.Acknowledge(ackId);
                session.PersistentClient?.Acknowledge(ackId);
                break;
            case SessionFrameKind.Event when frame.Payload is not null:
                session.Touch();

                var messageId = frame.Id;
                if (messageId == Guid.Empty)
                {
                    break;
                }

                try
                {
                    var handler = _messageReceivedHandler;
                    if (handler is not null)
                    {
                        await handler(session.Key, frame.Payload, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"{nameof(ResilientTcpServer)} handler failed: {ex}").ConfigureAwait(false);
                }
                finally
                {
                    session.EnqueueControl(SessionFrame.CreateAck(messageId));
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
                if (!session.TryDequeueOutbound(out frame))
                {
                    await session.WaitForOutboundAsync(cancellationToken).ConfigureAwait(false);
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
                    session.ReturnToQueue(frame);
                }

                await Console.Error.WriteLineAsync($"{nameof(ResilientTcpServer)} send loop error: {ex}").ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task MonitorSessionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SessionFrameContract.GetHeartbeatInterval(_options), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var now = DateTime.UtcNow;
            var timeout = SessionFrameContract.GetHeartbeatTimeout(_options);
            foreach (var session in _sessionStates.Values.Where(session => session.IsExpired(now, timeout)))
            {
                await session.CloseConnectionAsync().ConfigureAwait(false);
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

    public Task OnNextAsync(MessageReceived domainEvent, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task OnNextAsync(SessionJoined domainEvent, CancellationToken cancellationToken = default)
    {
        var sessionKey = domainEvent.SessionKey;
        if (!SessionKey.IsNullOrInvalid(sessionKey))
        {
            _activeSessions[sessionKey] = 0;
        }

        if (_sessionStates.TryGetValue(sessionKey, out var session))
        {
            _listener.RegisterPersistentSession(session);
        }

        return Task.CompletedTask;
    }

    public Task OnNextAsync(SessionLeft domainEvent, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    private readonly record struct SessionInitializationResult(
        bool IsSuccess,
        SessionState? Session,
        SessionConnection? Connection,
        CancellationTokenSource? ConnectionCancellation,
        SessionFrame? PendingFrame)
    {
        public static SessionInitializationResult Success(
            SessionState session,
            SessionConnection connection,
            CancellationTokenSource cancellation,
            SessionFrame? pendingFrame) => new(true, session, connection, cancellation, pendingFrame);

        public static SessionInitializationResult Failed() => new(false, null, null, null, null);
    }

    public sealed class SessionState : IAsyncDisposable
    {
        private readonly ConcurrentQueue<SessionFrame> _outbound = new();
        private readonly ConcurrentDictionary<Guid, SessionFrame> _inflight = new();
        private readonly SemaphoreSlim _sendSignal = new(0);
        private readonly object _lock = new();

        private SessionConnection? _connection;
        private ResilientSessionClient? _persistentClient;
        private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;

        public SessionState(SessionKey key)
        {
            Key = key;
        }

        public SessionKey Key { get; }

        public bool HasAuthenticated { get; private set; }

        public ResilientSessionClient? PersistentClient => _persistentClient;

        public void RegisterAuthentication()
        {
            HasAuthenticated = true;
            Touch();
        }

        public void AttachPersistentClient(ResilientSessionClient client)
        {
            _persistentClient = client ?? throw new ArgumentNullException(nameof(client));
        }

        public void EnqueueMessage(string payload)
        {
            Guid messageId = Guid.NewGuid();
            var frame = SessionFrame.CreateMessage(messageId, payload);
            _outbound.Enqueue(frame);
            _sendSignal.Release();
        }

        public void EnqueueControl(SessionFrame frame)
        {
            _outbound.Enqueue(frame);
            _sendSignal.Release();
        }

        public bool TryDequeueOutbound(out SessionFrame frame)
        {
            if (_outbound.TryDequeue(out frame))
            {
                if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty)
                {
                    _inflight[frame.Id] = frame;
                }

                return true;
            }

            return false;
        }

        public Task WaitForOutboundAsync(CancellationToken cancellationToken)
        {
            return _sendSignal.WaitAsync(cancellationToken);
        }

        public void ReturnToQueue(SessionFrame frame)
        {
            if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty)
            {
                _inflight.TryRemove(frame.Id, out _);
            }

            _outbound.Enqueue(frame);
            _sendSignal.Release();
        }

        public void Acknowledge(Guid messageId)
        {
            if (_inflight.TryRemove(messageId, out _))
            {
                Touch();
            }
        }

        public void Touch()
        {
            Volatile.Write(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            _persistentClient?.RecordRemoteActivity();
        }

        public bool IsExpired(DateTime utcNow, TimeSpan timeout)
        {
            var ticks = Volatile.Read(ref _lastHeartbeatTicks);
            var last = new DateTime(ticks, DateTimeKind.Utc);
            return utcNow - last > timeout;
        }

        public async Task<SessionConnection> AttachAsync(TcpClient client, CancellationToken serverToken, NetworkStream stream, Func<SessionState, NetworkStream, CancellationToken, Task> sendLoopFactory)
        {
            SessionConnection? previous;
            lock (_lock)
            {
                previous = _connection;
                _connection = null;
            }

            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            RequeueInFlight();

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            var sendTask = Task.Run(() => sendLoopFactory(this, stream, linkedCts.Token), linkedCts.Token);
            var connection = new SessionConnection(client, stream, linkedCts, sendTask);

            lock (_lock)
            {
                _connection = connection;
            }

            Touch();
            _persistentClient?.RecordRemoteActivity();
            return connection;
        }

        public void Detach()
        {
            lock (_lock)
            {
                _connection = null;
            }

            RequeueInFlight();
        }

        public async Task CloseConnectionAsync()
        {
            SessionConnection? connection;
            lock (_lock)
            {
                connection = _connection;
                _connection = null;
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                RequeueInFlight();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseConnectionAsync().ConfigureAwait(false);
            _sendSignal.Dispose();
        }

        private void RequeueInFlight()
        {
            var frames = _inflight.Values.OrderBy(frame => frame.Id).ToList();
            _inflight.Clear();
            if (frames.Count == 0)
            {
                return;
            }

            foreach (var frame in frames)
            {
                _outbound.Enqueue(frame);
            }

            _sendSignal.Release(frames.Count);
        }
    }

    public sealed class SessionConnection : IAsyncDisposable
    {
        public SessionConnection(TcpClient client, NetworkStream stream, CancellationTokenSource cancellation, Task sendTask)
        {
            Client = client;
            Stream = stream;
            Cancellation = cancellation;
            SendTask = sendTask;
        }

        public TcpClient Client { get; }

        public NetworkStream Stream { get; }

        public CancellationTokenSource Cancellation { get; }

        public Task SendTask { get; }

        public async ValueTask DisposeAsync()
        {
            await Cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await SendTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                await Console.Error.WriteLineAsync($"{nameof(SessionConnection)} send loop closed with {ex}").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on disposal
            }

            await Stream.DisposeAsync().ConfigureAwait(false);
            Client.Dispose();
            Cancellation.Dispose();
        }
    }
}
