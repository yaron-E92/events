using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Yaref92.Events.Transports;

internal sealed class ResilientTcpServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly ResilientSessionOptions _options;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _clientTasks = new();
    private readonly ConcurrentDictionary<string, PersistentSessionClient> _pendingPersistentClients = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _monitorLoop;

    public ResilientTcpServer(int port, ResilientSessionOptions? options = null)
    {
        _port = port;
        _options = options ?? new ResilientSessionOptions();
    }

    public event Func<string, string, CancellationToken, Task>? MessageReceived;

    public void RegisterPersistentClient(string token, PersistentSessionClient client)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (_sessions.TryGetValue(token, out var session))
        {
            session.AttachPersistentClient(client);
        }
        else
        {
            _pendingPersistentClients[token] = client;
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

        foreach (var session in _sessions.Values)
        {
            if (!session.HasAuthenticated)
            {
                continue;
            }

            session.EnqueueMessage(payload);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();
        _listener?.Stop();

        var tasks = _clientTasks.Values.ToArray();
        await Task.WhenAll(tasks.Append(_acceptLoop ?? Task.CompletedTask).Append(_monitorLoop ?? Task.CompletedTask)).WaitAsync(cancellationToken).ConfigureAwait(false);

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
            task.ContinueWith(_ => _clientTasks.TryRemove(client, out _), TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];
        SessionState? session = null;
        SessionConnection? connection = null;
        CancellationTokenSource? connectionCts = null;

        try
        {
            var firstFrameResult = await ReadFrameAsync(stream, lengthBuffer, serverToken).ConfigureAwait(false);
            if (!firstFrameResult.Success)
            {
                return;
            }

            var firstFrame = firstFrameResult.Frame!;
            SessionFrame? pendingFrame = null;
            if (firstFrame.Kind == SessionFrameKind.Auth)
            {
                var token = firstFrame.Token;
                if (string.IsNullOrWhiteSpace(token))
                {
                    return;
                }

                if (_options.RequireAuthentication && !IsTokenAccepted(token, firstFrame.Payload))
                {
                    return;
                }

                session = _sessions.GetOrAdd(token, key => new SessionState(key, _options));
                session.RegisterAuthentication(firstFrame.Payload);
            }
            else if (_options.RequireAuthentication)
            {
                return;
            }
            else
            {
                var remoteKey = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString("N");
                session = _sessions.GetOrAdd(remoteKey, key => new SessionState(key, _options));
                pendingFrame = firstFrame;
                session.RegisterAuthentication(null);
            }

            if (session is null)
            {
                return;
            }

            if (_pendingPersistentClients.TryRemove(session.Key, out var persistent))
            {
                session.AttachPersistentClient(persistent);
            }

            connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            connection = await session.AttachAsync(client, connectionCts.Token, stream, RunSendLoopAsync).ConfigureAwait(false);

            if (pendingFrame is not null)
            {
                await ProcessFrameAsync(session, pendingFrame, connectionCts.Token).ConfigureAwait(false);
            }

            while (!connectionCts.Token.IsCancellationRequested)
            {
                var result = await ReadFrameAsync(stream, lengthBuffer, connectionCts.Token).ConfigureAwait(false);
                if (!result.Success)
                {
                    break;
                }

                await ProcessFrameAsync(session, result.Frame!, connectionCts.Token).ConfigureAwait(false);
            }
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

            session?.Detach();
            client.Dispose();
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
            case SessionFrameKind.Ack:
                if (frame.Id is long ackId)
                {
                    session.Acknowledge(ackId);
                    session.PersistentClient?.Acknowledge(ackId);
                }
                break;
            case SessionFrameKind.Message:
                if (frame.Payload is null)
                {
                    break;
                }

                session.Touch();
                if (frame.Id is long messageId)
                {
                    try
                    {
                        var handler = MessageReceived;
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
                }
                break;
        }
    }

    private bool IsTokenAccepted(string token, string? secret)
    {
        if (!_options.RequireAuthentication)
        {
            return true;
        }

        if (string.IsNullOrEmpty(_options.AuthenticationToken))
        {
            return true;
        }

        return string.Equals(_options.AuthenticationToken, secret ?? token, StringComparison.Ordinal);
    }

    private async Task RunSendLoopAsync(SessionState session, NetworkStream stream, CancellationToken cancellationToken)
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
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var now = DateTime.UtcNow;
            foreach (var session in _sessions.Values)
            {
                if (session.IsExpired(now, _options.HeartbeatTimeout))
                {
                    await session.CloseConnectionAsync().ConfigureAwait(false);
                }
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

    private static async Task<FrameReadResult> ReadFrameAsync(NetworkStream stream, byte[] lengthBuffer, CancellationToken cancellationToken)
    {
        var lengthStatus = await TryReadAsync(stream, lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (!lengthStatus)
        {
            return FrameReadResult.Failed();
        }

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0)
        {
            return FrameReadResult.Failed();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var payloadStatus = await TryReadAsync(stream, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            if (!payloadStatus)
            {
                return FrameReadResult.Failed();
            }

            var json = Encoding.UTF8.GetString(buffer, 0, length);
            var frame = JsonSerializer.Deserialize<SessionFrame>(json, SessionFrameSerializer.Options);
            return frame is null
                ? FrameReadResult.Failed()
                : FrameReadResult.Success(frame);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> TryReadAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private readonly record struct FrameReadResult(bool Success, SessionFrame? Frame)
    {
        public static FrameReadResult Success(SessionFrame frame) => new(true, frame);

        public static FrameReadResult Failed() => new(false, null);
    }

    private sealed class SessionState : IAsyncDisposable
    {
        private readonly ConcurrentQueue<SessionFrame> _outbound = new();
        private readonly ConcurrentDictionary<long, SessionFrame> _inflight = new();
        private readonly SemaphoreSlim _sendSignal = new(0);
        private readonly object _lock = new();

        private SessionConnection? _connection;
        private PersistentSessionClient? _persistentClient;
        private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
        private long _nextMessageId;

        public SessionState(string key, ResilientSessionOptions options)
        {
            Key = key;
            Options = options;
        }

        public string Key { get; }

        public ResilientSessionOptions Options { get; }

        public bool HasAuthenticated { get; private set; }

        public PersistentSessionClient? PersistentClient => _persistentClient;

        public void RegisterAuthentication(string? secret)
        {
            HasAuthenticated = true;
            Touch();
        }

        public void AttachPersistentClient(PersistentSessionClient client)
        {
            _persistentClient = client ?? throw new ArgumentNullException(nameof(client));
        }

        public void EnqueueMessage(string payload)
        {
            var id = Interlocked.Increment(ref _nextMessageId);
            var frame = SessionFrame.CreateMessage(id, payload);
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
                if (frame.Kind == SessionFrameKind.Message && frame.Id is long id)
                {
                    _inflight[id] = frame;
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
            if (frame.Kind == SessionFrameKind.Message && frame.Id is long id)
            {
                _inflight.TryRemove(id, out _);
            }

            _outbound.Enqueue(frame);
            _sendSignal.Release();
        }

        public void Acknowledge(long messageId)
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

    private sealed class SessionConnection : IAsyncDisposable
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

            Stream.Dispose();
            Client.Dispose();
            Cancellation.Dispose();
        }
    }
}
