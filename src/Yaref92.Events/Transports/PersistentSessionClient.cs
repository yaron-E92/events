using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Transports;

internal sealed class PersistentSessionClient : IAsyncDisposable
{
    private const string OutboxFileName = "outbox.json";
    private static readonly SemaphoreSlim OutboxFileLock = new(1, 1);
    private static readonly JsonSerializerOptions OutboxSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _host;
    private readonly int _port;
    private readonly Func<PersistentSessionClient, TcpClient, CancellationToken, Task> _onClientConnected;
    private readonly TimeSpan _heartbeatInterval;
    private readonly string? _authenticationToken;
    private readonly IEventAggregator? _eventAggregator;

    private readonly ConcurrentQueue<TransportEnvelope> _controlQueue = new();
    private readonly ConcurrentQueue<TransportEnvelope> _eventQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private readonly Dictionary<long, OutboxEntry> _outboxEntries = new();
    private readonly string _sessionKey;
    private readonly string _outboxPath;

    private long _nextMessageId;
    private long _lastRemoteActivityTicks;
    private bool _initialized;

    private readonly TaskCompletionSource _firstConnectionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;
    private readonly object _runLock = new();

    public PersistentSessionClient(
        string host,
        int port,
        Func<PersistentSessionClient, TcpClient, CancellationToken, Task> onClientConnected,
        TimeSpan? heartbeatInterval = null,
        string? authenticationToken = null,
        IEventAggregator? eventAggregator = null)
    {
        _host = host;
        _port = port;
        _onClientConnected = onClientConnected ?? throw new ArgumentNullException(nameof(onClientConnected));
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        _authenticationToken = authenticationToken;
        _eventAggregator = eventAggregator;
        _sessionKey = $"{host}:{port}";
        _outboxPath = Path.Combine(AppContext.BaseDirectory, OutboxFileName);
        _lastRemoteActivityTicks = DateTime.UtcNow.Ticks;
    }

    public DnsEndPoint RemoteEndPoint => new(_host, _port);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        StartRunLoop();
        await _firstConnectionCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> EnqueueEventAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        long messageId;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            messageId = ++_nextMessageId;
            var entry = new OutboxEntry(messageId, payload)
            {
                IsQueued = true,
            };
            _outboxEntries[messageId] = entry;
            _eventQueue.Enqueue(TransportEnvelope.CreateEvent(messageId, payload));
        }
        finally
        {
            _stateLock.Release();
        }

        _sendSignal.Release();
        SchedulePersist();
        return messageId;
    }

    public void EnqueueControlMessage(TransportEnvelope envelope)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        _controlQueue.Enqueue(envelope);
        _sendSignal.Release();
    }

    public void Acknowledge(long messageId)
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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
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
        _cts.Dispose();
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

    private async Task RunConnectionOnceAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        ConfigureClient(client);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionToken = connectionCts.Token;

        Volatile.Write(ref _lastRemoteActivityTicks, DateTime.UtcNow.Ticks);

        await _onClientConnected(this, client, connectionToken).ConfigureAwait(false);

        if (!_firstConnectionCompletion.Task.IsCompleted)
        {
            _firstConnectionCompletion.TrySetResult();
        }

        await ReplayPendingEntriesAsync(connectionToken).ConfigureAwait(false);

        if (_authenticationToken is not null)
        {
            await WriteEnvelopeAsync(client.GetStream(), TransportEnvelope.CreateAuth(_authenticationToken), connectionToken).ConfigureAwait(false);
        }

        var sendTask = RunSendLoopAsync(client, connectionToken);
        var heartbeatTask = RunHeartbeatLoopAsync(connectionToken);

        var completed = await Task.WhenAny(sendTask, heartbeatTask).ConfigureAwait(false);
        connectionCts.Cancel();

        try
        {
            await Task.WhenAll(sendTask, heartbeatTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            if (!_cts.IsCancellationRequested)
            {
                throw;
            }
        }

        if (completed.IsFaulted)
        {
            throw completed.Exception?.GetBaseException() ?? new IOException("Persistent session terminated unexpectedly.");
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Persistent session terminated unexpectedly.");
        }
    }

    private async Task RunSendLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        while (!cancellationToken.IsCancellationRequested)
        {
            TransportEnvelope? envelope = null;
            if (!_controlQueue.TryDequeue(out envelope))
            {
                if (!_eventQueue.TryDequeue(out envelope))
                {
                    await _sendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            if (envelope.MessageType == TransportMessageType.Event && envelope.MessageId is long eventId)
            {
                if (!TryMarkEventDequeued(eventId))
                {
                    continue;
                }
            }

            try
            {
                await WriteEnvelopeAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
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
        var timeout = TimeSpan.FromTicks(_heartbeatInterval.Ticks * 2);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
            EnqueueControlMessage(TransportEnvelope.CreatePing());

            var lastTicks = Volatile.Read(ref _lastRemoteActivityTicks);
            var lastActivity = new DateTime(lastTicks, DateTimeKind.Utc);
            if (DateTime.UtcNow - lastActivity > timeout)
            {
                throw new IOException("Heartbeat timed out.");
            }
        }
    }

    private async Task ReplayPendingEntriesAsync(CancellationToken cancellationToken)
    {
        List<TransportEnvelope> envelopes;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            envelopes = _outboxEntries.Values
                .Where(entry => !entry.IsQueued)
                .OrderBy(entry => entry.MessageId)
                .Select(entry =>
                {
                    entry.IsQueued = true;
                    return TransportEnvelope.CreateEvent(entry.MessageId, entry.Payload);
                })
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }

        foreach (var envelope in envelopes)
        {
            _eventQueue.Enqueue(envelope);
            _sendSignal.Release();
        }
    }

    private void NotifySendFailure(Exception exception)
    {
        Console.Error.WriteLine($"{nameof(PersistentSessionClient)} send failed for {RemoteEndPoint}: {exception}");

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
                    Console.Error.WriteLine($"{nameof(PersistentSessionClient)} failed to publish {nameof(PublishFailed)}: {t.Exception.Flatten()}");
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (Exception aggregatorException)
        {
            Console.Error.WriteLine($"{nameof(PersistentSessionClient)} threw while publishing {nameof(PublishFailed)}: {aggregatorException}");
        }
    }

    private bool TryMarkEventDequeued(long messageId)
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

            if (model.Sessions.TryGetValue(_sessionKey, out var entries))
            {
                foreach (var entry in entries)
                {
                    var outboxEntry = new OutboxEntry(entry.Id, entry.Payload);
                    _outboxEntries[entry.Id] = outboxEntry;
                    if (entry.Id > _nextMessageId)
                    {
                        _nextMessageId = entry.Id;
                    }
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
                Console.Error.WriteLine($"{nameof(PersistentSessionClient)} failed to persist outbox: {ex}");
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
                model.Sessions.Remove(_sessionKey);
            }
            else
            {
                model.Sessions[_sessionKey] = snapshot;
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

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, attempt), 30);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task WriteEnvelopeAsync(NetworkStream stream, TransportEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, TransportEnvelopeSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private sealed class OutboxEntry
    {
        public OutboxEntry(long messageId, string payload)
        {
            MessageId = messageId;
            Payload = payload;
        }

        public long MessageId { get; }

        public string Payload { get; }

        public bool IsQueued { get; set; }
    }

    private sealed record StoredOutboxEntry(long Id, string Payload);

    private sealed class OutboxFileModel
    {
        public Dictionary<string, List<StoredOutboxEntry>> Sessions { get; set; } = new();
    }
}
