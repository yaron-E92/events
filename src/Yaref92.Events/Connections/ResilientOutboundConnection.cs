using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Caching;
using Yaref92.Events.Connections;

namespace Yaref92.Events.Sessions;

public sealed partial class ResilientOutboundConnection : IOutboundResilientConnection, IAsyncDisposable
{
    private readonly ResilientSessionOptions _options;
    private const string OutboxFileName = "outbox.json";
    private static readonly LingerOption _lingerOption = new(true, 5);
    private static readonly SemaphoreSlim OutboxFileLock = new(1, 1);
    private static readonly JsonSerializerOptions OutboxSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _sessionToken;

    private readonly Dictionary<Guid, OutboxEntry> _outboxEntries = [];
    private readonly SemaphoreSlim _reconnectGate;

    public SessionOutboundBuffer OutboundBuffer { get; }

    private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<Guid, AcknowledgementState> _acknowledgedEventIds = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private CancellationTokenSource? _activeOutboundConnectionCts;
    private readonly CancellationTokenSource _cts = new();

    private TaskCompletionSource _firstConnectionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _heartbeatLoop = Task.CompletedTask;
    private bool _outboxLoaded;

    private TaskCompletionSource _reconnectGateChangedCountCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runOutboundTask;
    private Task _sendLoop = Task.CompletedTask;
    private long _lastRemoteActivityTicks;

    private readonly string? _authenticationSecret;
    private readonly object _runLock = new();

    public DnsEndPoint RemoteEndPoint => new(SessionKey.Host, SessionKey.Port);

    public string OutboxPath { get; private set; }

    private bool NoMoreReconnectsAllowed => _reconnectGate.CurrentCount <= 0;

    public SessionKey SessionKey { get; }

    public ConcurrentDictionary<Guid, AcknowledgementState> AcknowledgedEventIds => _acknowledgedEventIds;

    internal event IResilientConnection.SessionConnectionEstablishedHandler? ConnectionEstablished;

    public ResilientOutboundConnection(ResilientSessionOptions options, SessionKey sessionKey)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _lastRemoteActivityTicks = DateTime.UtcNow.Ticks;
        OutboxPath = Path.Combine(AppContext.BaseDirectory, OutboxFileName);
        SessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
        _authenticationSecret = _options.AuthenticationToken;
        _sessionToken = SessionFrameContract.CreateSessionToken(SessionKey, _options, _authenticationSecret);
        _reconnectGate = new SemaphoreSlim(_options.MaximalReconnectAttempts, _options.MaximalReconnectAttempts);
        OutboundBuffer = new SessionOutboundBuffer();
    }

    async Task IOutboundResilientConnection.InitAsync(CancellationToken cancellationToken)
    {
        await EnsureOutboxLoadedAsync(cancellationToken).ConfigureAwait(false);
        StartRunOutboundLoop();
        await _firstConnectionCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RefreshConnectionAsync(CancellationToken token)
    {
        await (_runOutboundTask ?? Task.CompletedTask);
        StartRunOutboundLoop();
        // TODO Check if this is thread safe
        do
        {
            await _firstConnectionCompletion.Task.ConfigureAwait(false);
            if (_firstConnectionCompletion.Task.IsCompletedSuccessfully)
            {
                return true;
            }
            await WaitForReconnectGateCountChangeOrFullRelease();
        } while (_reconnectGate.CurrentCount > 0);
        return false;
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

    private static void ConfigureClient(TcpClient client)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        client.LingerState = _lingerOption;
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

    private static async Task WriteFrameAsync(NetworkStream stream, SessionFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task DumpBuffer()//touched
    {
        OutboundBuffer.RequeueInflight();
        bool bufferHasFrames;
        do
        {
            bufferHasFrames = OutboundBuffer.TryDequeue(out SessionFrame? frame);
            if (frame?.Payload is null)
            {
                continue;
            }
            if (FrameIsAcknowledgedEvent(frame))
            {
                // We do not want to save an acknowledged event to outbox
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

    private bool FrameIsAcknowledgedEvent(SessionFrame frame)
    {
        return frame is SessionFrame { Kind: SessionFrameKind.Event }
                        && AcknowledgedEventIds.TryGetValue(frame.Id, out AcknowledgementState state)
                        && state is AcknowledgementState.Acknowledged;
    }

    public void EnqueueFrame(SessionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        OutboundBuffer.EnqueueFrame(frame);
        _sendSignal.Release();
    }

    public void OnAckReceived(Guid eventId)
    {
        var removed = false;
        _stateLock.Wait();
        try
        {
            removed = _outboxEntries.Remove(eventId);
            AcknowledgedEventIds[eventId] = AcknowledgementState.Acknowledged;
            OutboundBuffer.TryAcknowledge(eventId);
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

    private async Task ActivateOutboundConnectionLoops(TcpClient tcpConnection, CancellationToken connectionToken)//touched
    {
        await ReplayPendingEntriesAsync(connectionToken).ConfigureAwait(false);

        await _stateLock.WaitAsync(connectionToken).ConfigureAwait(false);

        try
        {
            _sendLoop = Task.Run(() => RunSendLoopAsync(tcpConnection, connectionToken), connectionToken);
            _heartbeatLoop = Task.Run(() => RunHeartbeatLoopAsync(connectionToken), connectionToken);
        }
        finally
        {
            _stateLock.Release();
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

            await ConnectionEstablished?.Invoke(this, connectionToken)!;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            client.Close();
            throw new TcpConnectionDisconnectedException();
        }
        return client;
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

    private void FullyReleaseReconnectGate()
    {
        int amountToRelease = _options.MaximalReconnectAttempts - _reconnectGate.CurrentCount;
        if (amountToRelease > 1)
        {
            _reconnectGate.Release(amountToRelease);
        }
        _reconnectGateChangedCountCompletion.TrySetResult();
    }

    private TimeSpan GetBackoffDelay(int attempt)
    {
        var initial = _options.BackoffInitialDelay;
        var max = _options.BackoffMaxDelay;

        double clampedOneAndTwoPowAttemptMinusOne = Math.Pow(2, Math.Max(0, attempt - 1));
        var multiplier = Math.Min(clampedOneAndTwoPowAttemptMinusOne, max / initial);
        var candidate = TimeSpan.FromMilliseconds(initial.TotalMilliseconds * multiplier);
        return candidate > max ? max : candidate;
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

    private async Task PersistOutboxAsync(CancellationToken cancellationToken)
    {
        List<StoredOutboxEntry> snapshot;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = [.. _outboxEntries.Values
                .OrderBy(entry => entry.MessageId)
                .Select(entry => new StoredOutboxEntry(entry.MessageId, entry.Payload))];
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

    private async Task ReplayPendingEntriesAsync(CancellationToken cancellationToken)
    {
        List<SessionFrame> frames;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            frames = [.. _outboxEntries.Values
                .Where(entry => !entry.IsQueued)
                .OrderBy(entry => entry.MessageId)
                .Select(entry =>
                {
                    entry.IsQueued = true;
                    return SessionFrame.CreateEventFrame(entry.MessageId, entry.Payload);
                })];
        }
        finally
        {
            _stateLock.Release();
        }

        foreach (var frame in frames)
        {
            OutboundBuffer.EnqueueFrame(frame);
            _sendSignal.Release();
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

    private async Task RunOutboundAsync(CancellationToken cancellationToken)//Touched
    {
        var reconnectAttempt = 0;
        FullyReleaseReconnectGate();
        _firstConnectionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using TcpClient transientOutboundConnection = await ActivateTcpConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ActivateOutboundConnectionLoops(transientOutboundConnection, cancellationToken).ConfigureAwait(false);
                await RunUntilCancellationOrDisconnectAsync(cancellationToken).ConfigureAwait(false);
                reconnectAttempt = 0;
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

                reconnectAttempt++;
                if (NoMoreReconnectsAllowed) // TODO Ensure this is happening: Think about adding a maximal reconnectAttempt count, and then giving up
                {
                    break;
                }
                await _reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                _reconnectGateChangedCountCompletion.TrySetResult();
                var delay = GetBackoffDelay(reconnectAttempt);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                FullyReleaseReconnectGate();
            }
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

            if (!OutboundBuffer.TryDequeue(out SessionFrame? frame))
            {
                await OutboundBuffer.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (frame!.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty && !TryMarkEventDequeued(frame.Id))
            {
                continue;
            }

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
                if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty)
                {
                    AcknowledgedEventIds[frame.Id] = AcknowledgementState.SendingFailed;
                    OutboundBuffer.Return(frame);
                }
                await Console.Error.WriteLineAsync($"{nameof(ResilientOutboundConnection)} send loop error: {ex}").ConfigureAwait(false);
                break;
            }
        }

        if (!client.Connected)
        {
            throw new TcpConnectionDisconnectedException();
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
                Console.Error.WriteLine($"{nameof(ResilientCompositSessionConnection)} failed to persist outbox: {ex}");
            }
        });
    }

    private void SignalFirstConnectionSuccess()
    {
        if (_firstConnectionCompletion.Task.IsCompleted)
        {
            return;
        }

        _firstConnectionCompletion.TrySetResult();
    }

    private void StartRunOutboundLoop()
    {
        lock (_runLock)
        {
            _runOutboundTask ??= Task.Run(() => RunOutboundAsync(_cts.Token));
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

    private async Task WaitForReconnectGateCountChangeOrFullRelease()
    {
        await _reconnectGateChangedCountCompletion.Task;
        _reconnectGateChangedCountCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        await ResilientCompositSessionConnection.CancelAndDisposeTokenSource(_activeOutboundConnectionCts).ConfigureAwait(false);
        await ResilientCompositSessionConnection.CancelAndDisposeTokenSource(_cts).ConfigureAwait(false);

        Task[] runTasks = new Task[3];
        lock (_runLock)
        {
            runTasks = [_sendLoop ?? Task.CompletedTask, _heartbeatLoop ?? Task.CompletedTask, _runOutboundTask ?? Task.CompletedTask];
        }

        if (runTasks is not null)
        {
            try
            {
                await Task.WhenAll(runTasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                await Console.Error.WriteLineAsync($"loop closed with {ex}")
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
        }

        await SchedulePersist();

        _sendSignal.Dispose();
    }
}
