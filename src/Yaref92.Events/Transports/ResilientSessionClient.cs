using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports;

public sealed class ResilientSessionClient : IAsyncDisposable
{
    private const string OutboxFileName = "outbox.json";
    private static readonly SemaphoreSlim OutboxFileLock = new(1, 1);
    private static readonly JsonSerializerOptions OutboxSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResilientSessionOptions _options;
    private readonly string? _authenticationSecret;
    private readonly IEventAggregator? _eventAggregator;

    private readonly ConcurrentQueue<SessionFrame> _controlQueue = new();
    private readonly ConcurrentQueue<SessionFrame> _eventQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private readonly Dictionary<Guid, OutboxEntry> _outboxEntries = new();
    private readonly string _sessionToken;
    private readonly string _outboxPath;

    private long _lastRemoteActivityTicks;
    private bool _initialized;

    private readonly TaskCompletionSource _firstConnectionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _cts = new();
    private Task? _runTask;
    private readonly object _runLock = new();
    private CancellationTokenSource? _activeConnectionCts;

    public delegate ValueTask SessionFrameReceivedHandler(ResilientSessionClient client, SessionFrame frame, CancellationToken cancellationToken);

    public delegate ValueTask SessionConnectionEstablishedHandler(ResilientSessionClient client, CancellationToken cancellationToken);

    public ResilientSessionClient(
        Guid userId,
        string host,
        int port,
        ResilientSessionOptions? options = null,
        IEventAggregator? eventAggregator = null)
        : this(new(userId, host, port), options, eventAggregator)
    {
    }

    public ResilientSessionClient(
        Guid userId,
        string host,
        int port,
        SessionFrameReceivedHandler frameHandler,
        ResilientSessionOptions? options = null,
        IEventAggregator? eventAggregator = null)
        : this(new(userId, host, port), options, eventAggregator)
    {
        ArgumentNullException.ThrowIfNull(frameHandler);
        FrameReceived += frameHandler;
    }

    public ResilientSessionClient(
        string host,
        int port,
        SessionFrameReceivedHandler frameHandler,
        ResilientSessionOptions? options = null,
        IEventAggregator? eventAggregator = null)
        : this(Guid.NewGuid(), host, port, frameHandler, options, eventAggregator)
    {
    }

    public ResilientSessionClient(
        SessionKey sessionKey,
        ResilientSessionOptions? options = null,
        IEventAggregator? eventAggregator = null)
    {
        _options = options ?? new ResilientSessionOptions();
        _authenticationSecret = _options.AuthenticationToken;
        _eventAggregator = eventAggregator;
        SessionKey = sessionKey;
        _sessionToken = SessionFrameContract.CreateSessionToken(SessionKey, _options, _authenticationSecret);
        _outboxPath = Path.Combine(AppContext.BaseDirectory, OutboxFileName);
        _lastRemoteActivityTicks = DateTime.UtcNow.Ticks;
    }

    public DnsEndPoint RemoteEndPoint => new(SessionKey.Host, SessionKey.Port);
    public string SessionToken => _sessionToken;

    public SessionKey SessionKey { get; }

    public event SessionFrameReceivedHandler? FrameReceived;

    public event SessionConnectionEstablishedHandler? ConnectionEstablished;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        StartRunLoop();
        await _firstConnectionCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> EnqueueEventAsync(string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        Guid messageId;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            messageId = Guid.NewGuid();
            var entry = new OutboxEntry(messageId, payload)
            {
                IsQueued = true,
            };
            _outboxEntries[messageId] = entry;
            _eventQueue.Enqueue(SessionFrame.CreateMessage(messageId, payload));
        }
        finally
        {
            _stateLock.Release();
        }

        _sendSignal.Release();
        SchedulePersist();
        return messageId;
    }

    public void EnqueueControlMessage(SessionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        _controlQueue.Enqueue(frame);
        _sendSignal.Release();
    }

    public void Acknowledge(Guid messageId)
    {
        var removed = false;
        _stateLock.Wait();
        try
        {
            removed = _outboxEntries.Remove(messageId);
        }
        finally
        {
            _stateLock.Release();
        }

        if (removed)
        {
            SchedulePersist();
        }
    }

    public void RecordRemoteActivity()
    {
        Volatile.Write(ref _lastRemoteActivityTicks, DateTime.UtcNow.Ticks);
    }

    public async Task AbortActiveConnectionAsync()
    {
        var connectionCts = Volatile.Read(ref _activeConnectionCts);
        if (connectionCts is null)
        {
            return;
        }

        try
        {
            await connectionCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // connection already torn down
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                await Console.Error.WriteLineAsync($"{nameof(ResilientSessionClient)} CTS already disposed");
                _cts = null!;
            }
        }

        Task? runTask;
        lock (_runLock)
        {
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
        }

        _sendSignal.Dispose();
        _stateLock.Dispose();
        _cts?.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await LoadOutboxAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void StartRunLoop()
    {
        lock (_runLock)
        {
            if (_runTask is null)
            {
                _runTask = Task.Run(() => RunAsync(_cts.Token));
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionOnceAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!_firstConnectionCompletion.Task.IsCompleted)
                {
                    _firstConnectionCompletion.TrySetCanceled(cancellationToken);
                }
                break;
            }
            catch (Exception ex)
            {
                if (!_firstConnectionCompletion.Task.IsCompleted)
                {
                    _firstConnectionCompletion.TrySetException(ex);
                }

                attempt++;
                var delay = GetBackoffDelay(attempt);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void SignalFirstConnectionSuccess()
    {
        if (_firstConnectionCompletion.Task.IsCompleted)
        {
            return;
        }

        _firstConnectionCompletion.TrySetResult();
    }

    private static void ThrowIfConnectionFailed(Task completedTask)
    {
        if (!completedTask.IsFaulted)
        {
            return;
        }

        throw completedTask.Exception?.GetBaseException()
            ?? new IOException("Persistent session terminated unexpectedly.");
    }

    private static void ThrowIfSessionEndedUnexpectedly(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        throw new IOException("Persistent session terminated unexpectedly.");
    }

    private async Task RunConnectionOnceAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(SessionKey.Host, SessionKey.Port, cancellationToken).ConfigureAwait(false);
        ConfigureClient(client);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionToken = connectionCts.Token;
        Volatile.Write(ref _activeConnectionCts, connectionCts);

        try
        {
            Volatile.Write(ref _lastRemoteActivityTicks, DateTime.UtcNow.Ticks);

            SignalFirstConnectionSuccess();

            await ReplayPendingEntriesAsync(connectionToken).ConfigureAwait(false);

            var authFrame = SessionFrameContract.CreateAuthFrame(_sessionToken, _options, _authenticationSecret);
            await WriteFrameAsync(client.GetStream(), authFrame, connectionToken).ConfigureAwait(false);

            await RaiseConnectionEstablishedAsync(connectionToken).ConfigureAwait(false);

            var sendTask = RunSendLoopAsync(client, connectionToken);
            var heartbeatTask = RunHeartbeatLoopAsync(connectionToken);
            var receiveTask = RunReceiveLoopAsync(client, connectionToken);

            var completed = await Task.WhenAny(sendTask, heartbeatTask, receiveTask).ConfigureAwait(false);
            await connectionCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(sendTask, heartbeatTask, receiveTask).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                if (!_cts.IsCancellationRequested)
                {
                    throw;
                }
            }

            ThrowIfConnectionFailed(completed);
            ThrowIfSessionEndedUnexpectedly(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _activeConnectionCts, null);
        }
    }

    private async Task RunSendLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        while (!cancellationToken.IsCancellationRequested)
        {
            SessionFrame? frame;
            if (!_controlQueue.TryDequeue(out frame) && !_eventQueue.TryDequeue(out frame))
            {
                await _sendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty && !TryMarkEventDequeued(frame.Id))
            {
                continue;
            }

            try
            {
                await WriteFrameAsync(stream, frame, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                NotifySendFailure(ex);
                throw;
            }
            catch (SocketException ex)
            {
                NotifySendFailure(ex);
                throw;
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var heartbeatInterval = SessionFrameContract.GetHeartbeatInterval(_options);
        var timeout = SessionFrameContract.GetHeartbeatTimeout(_options);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
            EnqueueControlMessage(SessionFrame.CreatePing());

            var lastTicks = Volatile.Read(ref _lastRemoteActivityTicks);
            var lastActivity = new DateTime(lastTicks, DateTimeKind.Utc);
            if (DateTime.UtcNow - lastActivity > timeout)
            {
                throw new IOException("Heartbeat timed out.");
            }
        }
    }

    private async Task RunReceiveLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        while (!cancellationToken.IsCancellationRequested)
        {
            SessionFrameIO.FrameReadResult result;
            try
            {
                result = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!result.IsSuccess || result.Frame is null)
            {
                break;
            }

            await HandleInboundFrameAsync(result.Frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleInboundFrameAsync(SessionFrame frame, CancellationToken cancellationToken)
    {
        if (ShouldRecordRemoteActivity(frame.Kind))
        {
            RecordRemoteActivity();
        }

        await RaiseFrameReceivedAsync(frame, cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        switch (frame.Kind)
        {
            case SessionFrameKind.Ack when frame.Id != Guid.Empty:
                Acknowledge(frame.Id);
                break;
            case SessionFrameKind.Ping:
                EnqueueControlMessage(SessionFrame.CreatePong());
                break;
        }
    }

    private static bool ShouldRecordRemoteActivity(SessionFrameKind kind)
    {
        return kind switch
        {
            SessionFrameKind.Event => true,
            SessionFrameKind.Ack => true,
            SessionFrameKind.Ping => true,
            SessionFrameKind.Pong => true,
            SessionFrameKind.Auth => true,
            _ => false,
        };
    }

    private async ValueTask RaiseFrameReceivedAsync(SessionFrame frame, CancellationToken cancellationToken)
    {
        var handlers = FrameReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (SessionFrameReceivedHandler handler in handlers.GetInvocationList())
        {
            await handler(this, frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RaiseConnectionEstablishedAsync(CancellationToken cancellationToken)
    {
        var handlers = ConnectionEstablished;
        if (handlers is null)
        {
            return;
        }

        foreach (SessionConnectionEstablishedHandler handler in handlers.GetInvocationList())
        {
            await handler(this, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReplayPendingEntriesAsync(CancellationToken cancellationToken)
    {
        List<SessionFrame> frames;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            frames = _outboxEntries.Values
                .Where(entry => !entry.IsQueued)
                .OrderBy(entry => entry.MessageId)
                .Select(entry =>
                {
                    entry.IsQueued = true;
                    return SessionFrame.CreateMessage(entry.MessageId, entry.Payload);
                })
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }

        foreach (var frame in frames)
        {
            _eventQueue.Enqueue(frame);
            _sendSignal.Release();
        }
    }

    private void NotifySendFailure(Exception exception)
    {
        Console.Error.WriteLine($"{nameof(ResilientSessionClient)} send failed for {RemoteEndPoint}: {exception}");

        if (_eventAggregator is null)
        {
            return;
        }

        try
        {
            var publishFailed = new PublishFailed(RemoteEndPoint, exception);
            var publishTask = _eventAggregator.PublishEventAsync(publishFailed);
            publishTask.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    Console.Error.WriteLine($"{nameof(ResilientSessionClient)} failed to publish {nameof(PublishFailed)}: {t.Exception.Flatten()}");
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (Exception aggregatorException)
        {
            Console.Error.WriteLine($"{nameof(ResilientSessionClient)} threw while publishing {nameof(PublishFailed)}: {aggregatorException}");
        }
    }

    private bool TryMarkEventDequeued(Guid messageId)
    {
        _stateLock.Wait();
        try
        {
            if (_outboxEntries.TryGetValue(messageId, out var entry))
            {
                entry.IsQueued = false;
                return true;
            }
        }
        finally
        {
            _stateLock.Release();
        }

        return false;
    }

    private async Task LoadOutboxAsync(CancellationToken cancellationToken)
    {
        await OutboxFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_outboxPath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(_outboxPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var model = JsonSerializer.Deserialize<OutboxFileModel>(json, OutboxSerializerOptions);
            if (model?.Sessions is null)
            {
                return;
            }

            if (model.Sessions.TryGetValue(SessionKey, out var entries))
            {
                foreach (var entry in entries)
                {
                    var outboxEntry = new OutboxEntry(entry.Id, entry.Payload);
                    _outboxEntries[entry.Id] = outboxEntry;
                }
            }
        }
        finally
        {
            OutboxFileLock.Release();
        }
    }

    private void SchedulePersist()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PersistOutboxAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{nameof(ResilientSessionClient)} failed to persist outbox: {ex}");
            }
        });
    }

    private async Task PersistOutboxAsync(CancellationToken cancellationToken)
    {
        List<StoredOutboxEntry> snapshot;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _outboxEntries.Values
                .OrderBy(entry => entry.MessageId)
                .Select(entry => new StoredOutboxEntry(entry.MessageId, entry.Payload))
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }

        await OutboxFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OutboxFileModel model;
            if (File.Exists(_outboxPath))
            {
                var json = await File.ReadAllTextAsync(_outboxPath, cancellationToken).ConfigureAwait(false);
                model = string.IsNullOrWhiteSpace(json)
                    ? new OutboxFileModel()
                    : JsonSerializer.Deserialize<OutboxFileModel>(json, OutboxSerializerOptions) ?? new OutboxFileModel();
            }
            else
            {
                model = new OutboxFileModel();
            }

            if (snapshot.Count == 0)
            {
                model.Sessions.Remove(SessionKey);
            }
            else
            {
                model.Sessions[SessionKey] = snapshot;
            }

            var output = JsonSerializer.Serialize(model, OutboxSerializerOptions);
            await File.WriteAllTextAsync(_outboxPath, output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OutboxFileLock.Release();
        }
    }

    private static void ConfigureClient(TcpClient client)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    private TimeSpan GetBackoffDelay(int attempt)
    {
        var initial = _options.BackoffInitialDelay <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : _options.BackoffInitialDelay;
        var max = _options.BackoffMaxDelay <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(30)
            : _options.BackoffMaxDelay;

        var multiplier = Math.Min( Math.Pow(2, Math.Max(0, attempt - 1)), max / initial);
        var candidate = TimeSpan.FromMilliseconds(initial.TotalMilliseconds * multiplier);
        return candidate > max ? max : candidate;
    }

    private static async Task WriteFrameAsync(NetworkStream stream, SessionFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private sealed class OutboxEntry
    {
        public OutboxEntry(Guid messageId, string payload)
        {
            MessageId = messageId;
            Payload = payload;
        }

        public Guid MessageId { get; }

        public string Payload { get; }

        public bool IsQueued { get; set; }
    }

    private sealed record StoredOutboxEntry(Guid Id, string Payload);

    private sealed class OutboxFileModel
    {
        public Dictionary<SessionKey, List<StoredOutboxEntry>> Sessions { get; set; } = new();
    }
}
