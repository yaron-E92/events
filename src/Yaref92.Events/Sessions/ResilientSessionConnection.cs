using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Caching;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

using static Yaref92.Events.Abstractions.IInboundResilientConnection;

namespace Yaref92.Events.Transports;

internal sealed partial class ResilientSessionConnection : IAsyncDisposable, IOutboundResilientConnection, IInboundResilientConnection
{
    private const string OutboxFileName = "outbox.json";
    private static readonly SemaphoreSlim OutboxFileLock = new(1, 1);
    private static readonly JsonSerializerOptions OutboxSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResilientSessionOptions _options;
    private readonly string? _authenticationSecret;
    private readonly IEventAggregator? _eventAggregator;

    private readonly ConcurrentQueue<SessionFrame> _controlQueue = new();
    private readonly ConcurrentQueue<SessionFrame> _eventQueue = new();
    private readonly ConcurrentDictionary<Guid, AcknowledgementState> _acknowledgedEventIds = new();
    private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private readonly Dictionary<Guid, OutboxEntry> _outboxEntries = [];
    private readonly string _sessionToken;
    private long _lastRemoteActivityTicks;
    private bool _outboxLoaded;

    private readonly TaskCompletionSource _firstConnectionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _cts = new();
    private static readonly LingerOption _lingerOption = new(true, 5);
    private CancellationTokenSource? _activeOutboundConnectionCts;

    private Task? _runInboundTask;
    private Task? _runOutboundTask;
    private readonly object _runLock = new();
    private readonly SemaphoreSlim _transientConnectionSemaphore = new(0, 1);
    private Task _sendLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private Task _transientReceiveLoop = Task.CompletedTask;
    private CancellationTokenSource _incomingConnectionCts;
    private TcpClient _transientConnection;

    public delegate Task SessionConnectionEstablishedHandler(ResilientSessionConnection client, CancellationToken cancellationToken);

    public ResilientSessionConnection(
        Guid userId,
        string host,
        int port,
        ResilientSessionOptions? options = null,
        IEventAggregator? eventAggregator = null)
        : this(new(userId, host, port), options, eventAggregator)
    {
    }

    internal ResilientSessionConnection(
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

    internal ResilientSessionConnection(
        string host,
        int port,
        SessionFrameReceivedHandler frameHandler,
        ResilientSessionOptions? options = null,
        IEventAggregator? eventAggregator = null)
        : this(Guid.Empty, host, port, frameHandler, options, eventAggregator)
    {
    }

    internal ResilientSessionConnection(
        SessionKey sessionKey,
        ResilientSessionOptions? options = null,
        IEventAggregator? eventAggregator = null)
    {
        _options = options ?? new ResilientSessionOptions();
        _authenticationSecret = _options.AuthenticationToken;
        _eventAggregator = eventAggregator;
        SessionKey = sessionKey;
        _sessionToken = SessionFrameContract.CreateSessionToken(SessionKey, _options, _authenticationSecret);
        OutboxPath = Path.Combine(AppContext.BaseDirectory, OutboxFileName);
        _lastRemoteActivityTicks = DateTime.UtcNow.Ticks;
    }

    Task IInboundResilientConnection.InitAsync(CancellationToken cancellationToken)
    {
        StartRunInboundLoop();
        return Task.CompletedTask;
    }

    async Task IOutboundResilientConnection.InitAsync(CancellationToken cancellationToken)
    {
        await EnsureOutboxLoadedAsync(cancellationToken).ConfigureAwait(false);
        StartRunOutboundLoop();
        await _firstConnectionCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels any existing incoming connection and attaches the new transient connection for receiving frames.
    /// It is done in a thread-safe manner.
    /// </summary>
    /// <param name="transientConnection"></param>
    /// <param name="incomingConnectionCts"></param>
    /// <remarks>
    /// The resilient connection has a receive loop that waits for transient connections to be attached.
    /// When a new transient connection is attached, the receive loop starts processing frames from it.
    /// </remarks>
    async Task IInboundResilientConnection.AttachTransientConnection(TcpClient transientConnection, CancellationTokenSource incomingConnectionCts)
    {
        await _incomingConnectionCts?.CancelAsync()!;
        _incomingConnectionCts = incomingConnectionCts;
        _transientConnection = transientConnection;
        _transientConnectionSemaphore.Release();
    }

    public DnsEndPoint RemoteEndPoint => new(SessionKey.Host, SessionKey.Port);
    public string SessionToken => _sessionToken;

    public SessionKey SessionKey { get; }
    public string OutboxPath { get; private set; }

    public ConcurrentQueue<SessionFrame> ControlQueue => _controlQueue;

    public ConcurrentQueue<SessionFrame> EventQueue => _eventQueue;

    private event SessionFrameReceivedHandler? FrameReceived;

    event SessionFrameReceivedHandler? IInboundResilientConnection.FrameReceived
    {
        add => FrameReceived += value;
        remove => FrameReceived -= value;
    }

    public event SessionConnectionEstablishedHandler? ConnectionEstablished;

    //public async Task<Guid> EnqueueEventAsync(string payload, CancellationToken cancellationToken)
    //{
    //    ArgumentNullException.ThrowIfNull(payload);

    //    Guid eventId;
    //    await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    //    try
    //    {
    //        eventId = Guid.NewGuid();
    //        var entry = new OutboxEntry(eventId, payload)
    //        {
    //            IsQueued = true,
    //        };
    //        _outboxEntries[eventId] = entry;
    //        EventQueue.Enqueue(SessionFrame.CreateEventFrame(eventId, payload));
    //    }
    //    finally
    //    {
    //        _stateLock.Release();
    //    }

    //    _sendSignal.Release();
    //    _ = SchedulePersist();
    //    return eventId;
    //}

    public void EnqueueFrame(SessionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        switch (frame.Kind)
        {
            case SessionFrameKind.Auth:
                break;
            case SessionFrameKind.Ping:
            case SessionFrameKind.Pong:
            case SessionFrameKind.Ack:
                ControlQueue.Enqueue(frame);
                break;
            case SessionFrameKind.Event:
                EventQueue.Enqueue(frame);
                break;
            default:
                break;
        }
        _sendSignal.Release();
    }

    public void Acknowledge(Guid messageId)
    {
        var removed = false;
        _stateLock.Wait();
        try
        {
            removed = _outboxEntries.Remove(messageId);
            _acknowledgedEventIds[messageId] = AcknowledgementState.Acknowledged;
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
        var connectionCts = Volatile.Read(ref _activeOutboundConnectionCts);
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
                await Console.Error.WriteLineAsync($"{nameof(ResilientSessionConnection)} CTS already disposed");
                _cts = null!;
            }
        }

        Task[] runTasks = new Task[2];
        lock (_runLock)
        {
            runTasks = [_runOutboundTask ?? Task.CompletedTask, _runInboundTask ?? Task.CompletedTask];
        }

        if (runTasks is not null)
        {
            try
            {
                await Task.WhenAll(runTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
        }

        await SchedulePersist();

        _sendSignal.Dispose();
        _stateLock.Dispose();
        _cts?.Dispose();
    }

    private async Task EnsureOutboxLoadedAsync(CancellationToken cancellationToken)
    {
        if (_outboxLoaded)
        {
            return;
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_outboxLoaded)
            {
                return;
            }

            await LoadOutboxAsync(cancellationToken).ConfigureAwait(false);
            _outboxLoaded = true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void StartRunInboundLoop()
    {
        lock (_runLock)
        {
            _runInboundTask ??= Task.Run(() => RunInboundAsync(_cts.Token));
        }
    }

    private async Task RunInboundAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _transientConnectionSemaphore.WaitAsync(cancellationToken);
            await _transientReceiveLoop;
            _transientReceiveLoop = Task.Run(() => RunTransientConnectionReceiveLoopAsync(_transientConnection, _incomingConnectionCts.Token), _incomingConnectionCts.Token);
            //_transientReceiveLoop.ContinueWith(SessionLeft) // Figure out how to notify session left
        }
    }

    private void StartRunOutboundLoop()
    {
        lock (_runLock)
        {
            _runOutboundTask ??= Task.Run(() => RunOutboundAsync(_cts.Token));
        }
    }

    private async Task RunOutboundAsync(CancellationToken cancellationToken)//Touched
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using TcpClient transientOutboundConnection = await ActivateTcpConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ActivateOutboundConnectionLoops(transientOutboundConnection, cancellationToken).ConfigureAwait(false);
                await RunUntilCancellationOrDisconnectAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<TcpClient> ActivateTcpConnectionAsync(CancellationToken cancellationToken)//Touched
    {
        TcpClient client = new();
        ConfigureClient(client);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionToken = connectionCts.Token;
        Volatile.Write(ref _activeOutboundConnectionCts, connectionCts);
        try
        {
            await client.ConnectAsync(SessionKey.Host, SessionKey.Port, connectionToken).ConfigureAwait(false);
            Volatile.Write(ref _lastRemoteActivityTicks, DateTime.UtcNow.Ticks);

            SignalFirstConnectionSuccess();

            var authFrame = SessionFrameContract.CreateAuthFrame(_sessionToken, _options, _authenticationSecret);
            await WriteFrameAsync(client.GetStream(), authFrame, connectionToken).ConfigureAwait(false);

            await RaiseConnectionEstablishedAsync(connectionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            client.Close();
            throw new TcpConnectionDisconnectedException();
        }
        return client;
    }

    private void SignalFirstConnectionSuccess()
    {
        if (_firstConnectionCompletion.Task.IsCompleted)
        {
            return;
        }

        _firstConnectionCompletion.TrySetResult();
    }

    private async Task ActivateOutboundConnectionLoops(TcpClient tcpConnection, CancellationToken connectionToken)//touched
    {
        await ReplayPendingEntriesAsync(connectionToken).ConfigureAwait(false);

        await _stateLock.WaitAsync(connectionToken).ConfigureAwait(false);

        try
        {
            _sendLoop = Task.Run(() => RunSendLoopAsync(tcpConnection, connectionToken), connectionToken);
            _heartbeatLoop = Task.Run(() => RunHeartbeatLoopAsync(connectionToken), connectionToken);
            //_transientReceiveLoop = Task.Run(() => RunTransientConnectionReceiveLoopAsync(transientOutboundConnection, connectionToken), connectionToken);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task RunUntilCancellationOrDisconnectAsync(CancellationToken cancellationToken)//touched
    {
        try
        {
            var completed = await Task.WhenAny(_sendLoop, _heartbeatLoop).ConfigureAwait(false);
            _activeOutboundConnectionCts?.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(_sendLoop, _heartbeatLoop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                if (_activeOutboundConnectionCts?.IsCancellationRequested is not true)
                {
                    throw;
                }
            }

            ThrowIfConnectionFailed(completed);
            ThrowIfSessionEndedUnexpectedly(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _activeOutboundConnectionCts, null);
        }
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
        ConfigureClient(client);
        await client.ConnectAsync(SessionKey.Host, SessionKey.Port, cancellationToken).ConfigureAwait(false);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionToken = connectionCts.Token;
        Volatile.Write(ref _activeOutboundConnectionCts, connectionCts);
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
            var receiveTask = RunTransientConnectionReceiveLoopAsync(client, connectionToken);

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
            Volatile.Write(ref _activeOutboundConnectionCts, null);
        }
    }

    private async Task RunSendLoopAsync(TcpClient client, CancellationToken cancellationToken)//touched
    {
        var stream = client.GetStream();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!client.Connected)
            {
                break;
            }
            SessionFrame? frame;
            if (!ControlQueue.TryDequeue(out frame) && !EventQueue.TryDequeue(out frame))
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
            catch  (Exception ex) when (ex is IOException or SocketException)
            {
                NotifySendFailure(ex);
                if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty)
                {
                    _acknowledgedEventIds[frame.Id] = AcknowledgementState.SendingFailed;
                }
                //throw;
            }
        }

        if (!client.Connected)
        {
            throw new TcpConnectionDisconnectedException();
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)//touched
    {
        var heartbeatInterval = SessionFrameContract.GetHeartbeatInterval(_options);
        var timeout = SessionFrameContract.GetHeartbeatTimeout(_options);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
            EnqueueFrame(SessionFrame.CreatePing());

            var lastTicks = Volatile.Read(ref _lastRemoteActivityTicks);
            var lastActivity = new DateTime(lastTicks, DateTimeKind.Utc);
            if (DateTime.UtcNow - lastActivity > timeout)
            {
                throw new IOException("Heartbeat timed out.");
            }
        }
    }

    private async Task RunTransientConnectionReceiveLoopAsync(TcpClient client, CancellationToken incomingConnectionCancellation) //touched
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        while (!incomingConnectionCancellation.IsCancellationRequested)
        {
            if (!client.Connected)
            {
                break;
            }
            SessionFrameIO.FrameReadResult result;
            try
            {
                result = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, incomingConnectionCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (incomingConnectionCancellation.IsCancellationRequested)
            {
                break;
            }

            if (!result.IsSuccess || result.Frame is null)
            {
                // TODO Log this
                continue;
            }

            await HandleInboundFrameAsync(result.Frame, incomingConnectionCancellation).ConfigureAwait(false);
        }

        if (!client.Connected)
        {
            throw new TcpConnectionDisconnectedException();
        }
    }

    private async Task HandleInboundFrameAsync(SessionFrame frame, CancellationToken cancellationToken) //touched
    {
        if (ShouldRecordRemoteActivity(frame.Kind))
        {
            RecordRemoteActivity();
        }

        await FrameReceived?.Invoke(frame, SessionKey, cancellationToken)!;
    }

    public static bool ShouldRecordRemoteActivity(SessionFrameKind kind) //touched
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

    private async Task RaiseConnectionEstablishedAsync(CancellationToken cancellationToken)
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
                    return SessionFrame.CreateEventFrame(entry.MessageId, entry.Payload);
                })
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }

        foreach (var frame in frames)
        {
            EventQueue.Enqueue(frame);
            _sendSignal.Release();
        }
    }

    private void NotifySendFailure(Exception exception)
    {
        Console.Error.WriteLine($"{nameof(ResilientSessionConnection)} send failed for {RemoteEndPoint}: {exception}");

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
                    Console.Error.WriteLine($"{nameof(ResilientSessionConnection)} failed to publish {nameof(PublishFailed)}: {t.Exception.Flatten()}");
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (Exception aggregatorException)
        {
            Console.Error.WriteLine($"{nameof(ResilientSessionConnection)} threw while publishing {nameof(PublishFailed)}: {aggregatorException}");
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
            if (!File.Exists(OutboxPath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(OutboxPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var model = JsonSerializer.Deserialize<OutboxFileModel>(json, OutboxSerializerOptions);
            if (model?.Sessions is null)
            {
                return;
            }

            var sessionKeyString = SessionKey.ToString();
            if (!string.IsNullOrWhiteSpace(sessionKeyString) &&
                model.Sessions.TryGetValue(sessionKeyString, out var entries))
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

    private Task SchedulePersist()
    {
        return Task.Run(async () =>
        {
            try
            {
                await PersistOutboxAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{nameof(ResilientSessionConnection)} failed to persist outbox: {ex}");
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
            if (File.Exists(OutboxPath))
            {
                var json = await File.ReadAllTextAsync(OutboxPath, cancellationToken).ConfigureAwait(false);
                model = string.IsNullOrWhiteSpace(json)
                    ? new OutboxFileModel()
                    : JsonSerializer.Deserialize<OutboxFileModel>(json, OutboxSerializerOptions) ?? new OutboxFileModel();
            }
            else
            {
                model = new OutboxFileModel();
            }

            var sessionKeyString = SessionKey.ToString();
            if (string.IsNullOrWhiteSpace(sessionKeyString))
            {
                return;
            }

            if (snapshot.Count == 0)
            {
                model.Sessions.Remove(sessionKeyString);
            }
            else
            {
                model.Sessions[sessionKeyString] = snapshot;
            }

            var output = JsonSerializer.Serialize(model, OutboxSerializerOptions);
            await File.WriteAllTextAsync(OutboxPath, output, cancellationToken).ConfigureAwait(false);
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
        client.LingerState = _lingerOption;
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

    public async Task<AcknowledgementState> WaitForAck(Guid eventId, CancellationToken cancellationToken)//touched
    {
        AcknowledgementState acknowledgementState;
        while (!_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (_acknowledgedEventIds.TryGetValue(eventId, out acknowledgementState))
            {
                return acknowledgementState;
            }
            await Task.Delay(_options.HeartbeatInterval, cancellationToken);
        }
        return _acknowledgedEventIds.TryGetValue(eventId, out acknowledgementState)
            ? acknowledgementState
            : AcknowledgementState.SendingFailed;
    }

    public async Task DumpBuffer(SessionOutboundBuffer outboundBuffer)//touched
    {
        bool bufferHasFrames;
        do
        {
            bufferHasFrames = outboundBuffer.TryDequeue(out SessionFrame? frame);
            if (frame?.Payload is null)
            {
                continue;
            }
            await OutboxFileLock.WaitAsync();
            try
            {
                _outboxEntries.TryAdd(frame.Id, new(frame.Id, frame.Payload));
            }
            finally
            {
                OutboxFileLock.Release();
            }

        }
        while (bufferHasFrames);
    }
}
