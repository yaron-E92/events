using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;

namespace Yaref92.Events.Transports;

/// <summary>
/// A peer-to-peer capable TCP event transport implementing <see cref="IEventTransport"/>.
/// Supports both listening for incoming connections and connecting to peers.
/// Serializes events as JSON and delivers them to registered handlers.
/// </summary>
public class TCPEventTransport : IEventTransport, IDisposable
{
    private readonly ConcurrentDictionary<Type, ConcurrentBag<Func<object, CancellationToken, Task>>> _handlers = new();
    private readonly int _listenPort;
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, PersistentSessionClient?> _clientSessions = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _receiveTasks = new();
    private readonly ConcurrentDictionary<string, PersistentSessionClient> _persistentSessions = new();
    private Task? _acceptConnectionsTask;
    private CancellationTokenSource? _cts;
    private readonly IEventSerializer _serializer;
    private readonly IEventAggregator? _eventAggregator;
    private readonly TimeSpan _heartbeatInterval;
    private readonly string? _authenticationToken;
    private long _transientMessageId;

    public TCPEventTransport(
        int listenPort,
        IEventSerializer? serializer = null,
        IEventAggregator? eventAggregator = null,
        TimeSpan? heartbeatInterval = null,
        string? authenticationToken = null)
    {
        _listenPort = listenPort;
        _serializer = serializer ?? new JsonEventSerializer();
        _eventAggregator = eventAggregator;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        _authenticationToken = authenticationToken;

        _eventAggregator?.RegisterEventType<PublishFailed>();
    }

    /// <summary>
    /// Starts listening for incoming TCP connections.
    /// </summary>
    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The transport is already listening.");
        }

        _listener = new TcpListener(IPAddress.Any, _listenPort);
        _listener.Start();

        _cts ??= new CancellationTokenSource();

        var registration = default(CancellationTokenRegistration);
        var registrationSet = false;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() => _cts.Cancel());
            registrationSet = true;
        }

        _acceptConnectionsTask = AcceptConnectionsLoopAsync(_cts.Token);
        TrackBackgroundTask(_acceptConnectionsTask, nameof(AcceptConnectionsLoopAsync));

        if (registrationSet)
        {
            _acceptConnectionsTask!.ContinueWith(_ => registration.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connects to a remote peer.
    /// </summary>
    public async Task ConnectToPeerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
        }

        _cts ??= new CancellationTokenSource();

        var key = $"{host}:{port}";
        var session = _persistentSessions.GetOrAdd(
            key,
            _ =>
            {
                var persistentSession = new PersistentSessionClient(
                    host,
                    port,
                    RegisterPersistentClient,
                    _heartbeatInterval,
                    _authenticationToken,
                    _eventAggregator);
                return persistentSession;
            });

        await session.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        if (domainEvent is null)
        {
            throw new ArgumentNullException(nameof(domainEvent));
        }

        var payload = _serializer.Serialize(domainEvent);
        var envelope = TransportEnvelope.CreateEvent(Interlocked.Increment(ref _transientMessageId), payload);

        List<Exception>? exceptions = null;
        foreach (var client in _clients.Keys)
        {
            try
            {
                await WriteToClientAsync(client, envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                HandlePublishFailure(client, ex, ref exceptions);
            }
            catch (SocketException ex)
            {
                HandlePublishFailure(client, ex, ref exceptions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        foreach (var session in _persistentSessions.Values)
        {
            try
            {
                await session.EnqueueEventAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }

        if (exceptions is { Count: > 0 })
        {
            throw new AggregateException("One or more transports failed to queue the published event.", exceptions);
        }
    }

    /// <summary>
    /// Writes the transport envelope to the specified TCP client.
    /// Extracted as a separate method to enable deterministic failure injection in tests.
    /// </summary>
    /// <param name="client">The client to write to.</param>
    /// <param name="envelope">The transport envelope to write.</param>
    /// <param name="cancellationToken">Cancellation token for the asynchronous operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the write finishes.</returns>
    protected virtual async Task WriteToClientAsync(
        TcpClient client,
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, TransportEnvelopeSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public void Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : class, IDomainEvent
    {
        var bag = _handlers.GetOrAdd(typeof(T), _ => new ConcurrentBag<Func<object, CancellationToken, Task>>());
        bag.Add(async (obj, ct) => await handler((T)obj, ct).ConfigureAwait(false));
    }

    private async Task AcceptConnectionsLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    if (_listener is null)
                    {
                        throw new InvalidOperationException("TCP listener is not initialized.");
                    }

                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    await Console.Error.WriteLineAsync($"{nameof(AcceptConnectionsLoopAsync)} socket error: {ex}").ConfigureAwait(false);
                    continue;
                }
                catch (IOException ex)
                {
                    await Console.Error.WriteLineAsync($"{nameof(AcceptConnectionsLoopAsync)} I/O error: {ex}").ConfigureAwait(false);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (client is null)
                {
                    continue;
                }

                RegisterInboundClient(client, cancellationToken);
            }
        }
        finally
        {
            // no-op, method exits when cancellation requested or listener disposed
        }
    }

    private void RegisterInboundClient(TcpClient client, CancellationToken cancellationToken)
    {
        if (!_clients.TryAdd(client, 0))
        {
            client.Dispose();
            return;
        }

        if (!_clientSessions.TryAdd(client, null))
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
            return;
        }

        StartReceiveLoopForClient(client, session: null, cancellationToken);
    }

    private Task RegisterPersistentClient(PersistentSessionClient session, TcpClient client, CancellationToken cancellationToken)
    {
        if (!_clientSessions.TryAdd(client, session))
        {
            client.Dispose();
            return Task.CompletedTask;
        }

        StartReceiveLoopForClient(client, session, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task ReceiveMessagesLoopAsync(TcpClient client, PersistentSessionClient? session, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var lengthBuffer = new byte[4];
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ProcessNextEnvelopeAsync(client, session, stream, lengthBuffer, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (IOException ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} encountered an I/O error: {ex}").ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} encountered a socket error: {ex}").ConfigureAwait(false);
        }
        finally
        {
            CleanupClient(client);
        }
    }

    private async Task<bool> ProcessNextEnvelopeAsync(
        TcpClient client,
        PersistentSessionClient? session,
        NetworkStream stream,
        byte[] lengthBuffer,
        CancellationToken cancellationToken)
    {
        var readResult = await TryReadEnvelopeAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        switch (readResult.Status)
        {
            case EnvelopeReadStatus.Break:
                return false;
            case EnvelopeReadStatus.Continue:
                return true;
            case EnvelopeReadStatus.Success:
                session?.RecordRemoteActivity();
                return await TryHandleEnvelopeAsync(client, session, readResult.Envelope!, cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async Task<EnvelopeReadResult> TryReadEnvelopeAsync(NetworkStream stream, byte[] lengthBuffer, CancellationToken cancellationToken)
    {
        var lengthStatus = await TryReadLengthPrefixAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        if (lengthStatus != EnvelopeReadStatus.Success)
        {
            return EnvelopeReadResult.FromStatus(lengthStatus);
        }

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        var payloadBuffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var payloadStatus = await TryReadPayloadAsync(stream, payloadBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            if (payloadStatus != EnvelopeReadStatus.Success)
            {
                return EnvelopeReadResult.FromStatus(payloadStatus);
            }

            try
            {
                var json = Encoding.UTF8.GetString(payloadBuffer, 0, length);
                var envelope = JsonSerializer.Deserialize<TransportEnvelope>(json, TransportEnvelopeSerializer.Options);
                return envelope is null
                    ? EnvelopeReadResult.Continue()
                    : EnvelopeReadResult.Success(envelope);
            }
            catch (JsonException ex)
            {
                await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} failed to deserialize transport envelope: {ex}").ConfigureAwait(false);
                return EnvelopeReadResult.Continue();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payloadBuffer);
        }
    }

    private async Task<EnvelopeReadStatus> TryReadLengthPrefixAsync(NetworkStream stream, byte[] lengthBuffer, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadExactAsync(stream, lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false)
                ? EnvelopeReadStatus.Success
                : EnvelopeReadStatus.Break;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EnvelopeReadStatus.Break;
        }
        catch (IOException ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} I/O error while reading length prefix: {ex}").ConfigureAwait(false);
            return EnvelopeReadStatus.Break;
        }
        catch (SocketException ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} socket error while reading length prefix: {ex}").ConfigureAwait(false);
            return EnvelopeReadStatus.Break;
        }
    }

    private async Task<EnvelopeReadStatus> TryReadPayloadAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false)
                ? EnvelopeReadStatus.Success
                : EnvelopeReadStatus.Break;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EnvelopeReadStatus.Break;
        }
        catch (IOException ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} I/O error while reading payload: {ex}").ConfigureAwait(false);
            return EnvelopeReadStatus.Continue;
        }
        catch (SocketException ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} socket error while reading payload: {ex}").ConfigureAwait(false);
            return EnvelopeReadStatus.Continue;
        }
    }

    private async Task<bool> TryHandleEnvelopeAsync(
        TcpClient client,
        PersistentSessionClient? session,
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleTransportEnvelopeAsync(client, session, envelope, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            await Console.Error.WriteLineAsync($"{nameof(ReceiveMessagesLoopAsync)} failed to handle envelope: {ex}").ConfigureAwait(false);
            return false;
        }
    }

    private enum EnvelopeReadStatus
    {
        Success,
        Continue,
        Break,
    }

    private readonly record struct EnvelopeReadResult(EnvelopeReadStatus Status, TransportEnvelope? Envelope)
    {
        public static EnvelopeReadResult Success(TransportEnvelope envelope) => new(EnvelopeReadStatus.Success, envelope);

        public static EnvelopeReadResult Continue() => new(EnvelopeReadStatus.Continue, null);

        public static EnvelopeReadResult FromStatus(EnvelopeReadStatus status) => new(status, null);
    }

    private async Task HandleTransportEnvelopeAsync(
        TcpClient client,
        PersistentSessionClient? session,
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        switch (envelope.MessageType)
        {
            case TransportMessageType.Event:
                if (!string.IsNullOrWhiteSpace(envelope.Payload))
                {
                    (Type? type, IDomainEvent? domainEvent) = _serializer.Deserialize(envelope.Payload);
                    if (type != null && _handlers.TryGetValue(type, out var handlersForType))
                    {
                        foreach (var handler in handlersForType)
                        {
                            await handler(domainEvent!, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (envelope.MessageId.HasValue)
                {
                    await SendAckAsync(client, session, envelope.MessageId.Value, cancellationToken).ConfigureAwait(false);
                }
                break;
            case TransportMessageType.Ack:
                if (session is not null && envelope.MessageId.HasValue)
                {
                    session.Acknowledge(envelope.MessageId.Value);
                    session.RecordRemoteActivity();
                }
                break;
            case TransportMessageType.Ping:
                await SendPongAsync(client, session, cancellationToken).ConfigureAwait(false);
                break;
            case TransportMessageType.Pong:
                session?.RecordRemoteActivity();
                break;
            case TransportMessageType.Auth:
                session?.RecordRemoteActivity();
                break;
            default:
                break;
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    private Task SendAckAsync(TcpClient client, PersistentSessionClient? session, long messageId, CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            session.EnqueueControlMessage(TransportEnvelope.CreateAck(messageId));
            return Task.CompletedTask;
        }

        return WriteToClientAsync(client, TransportEnvelope.CreateAck(messageId), cancellationToken);
    }

    private Task SendPongAsync(TcpClient client, PersistentSessionClient? session, CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            session.EnqueueControlMessage(TransportEnvelope.CreatePong());
            return Task.CompletedTask;
        }

        return WriteToClientAsync(client, TransportEnvelope.CreatePong(), cancellationToken);
    }

    public void Dispose()
    {
        _cts?.Cancel();

        try
        {
            _acceptConnectionsTask?.Wait();
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"{nameof(AcceptConnectionsLoopAsync)} faulted during disposal: {ex.Flatten()}");
        }

        foreach (var task in _receiveTasks.Values)
        {
            try
            {
                task.Wait();
            }
            catch (AggregateException ex)
            {
                Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} faulted during disposal: {ex.Flatten()}");
            }
        }
        _receiveTasks.Clear();

        _listener?.Stop();
        foreach (var client in _clientSessions.Keys)
        {
            try
            {
                client.Close();
            }
            catch
            {
                // ignored
            }
            finally
            {
                client.Dispose();
            }
        }
        _clientSessions.Clear();
        _clients.Clear();

        foreach (var session in _persistentSessions.Values)
        {
            try
            {
                session.DisposeAsync().AsTask().Wait();
            }
            catch (AggregateException ex)
            {
                Console.Error.WriteLine($"{nameof(PersistentSessionClient)} disposal faulted: {ex.Flatten()}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{nameof(PersistentSessionClient)} disposal faulted: {ex}");
            }
        }
        _persistentSessions.Clear();
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptConnectionsTask = null;
    }

    private void StartReceiveLoopForClient(TcpClient client, PersistentSessionClient? session, CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            _cts = new CancellationTokenSource();
        }

        CancellationTokenSource? linkedCts = null;
        CancellationToken token;
        if (cancellationToken.CanBeCanceled && cancellationToken != _cts.Token)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            token = linkedCts.Token;
        }
        else
        {
            token = _cts.Token;
        }

        var receiveTask = ReceiveMessagesLoopAsync(client, session, token);
        _receiveTasks[client] = receiveTask;
        receiveTask.ContinueWith(t =>
        {
            _receiveTasks.TryRemove(client, out _);
            linkedCts?.Dispose();
            if (t.IsFaulted && t.Exception is not null)
            {
                Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} task faulted: {t.Exception.Flatten()}");
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void TrackBackgroundTask(Task task, string taskName)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                Console.Error.WriteLine($"{taskName} task faulted: {t.Exception.Flatten()}");
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void CleanupClient(TcpClient client)
    {
        _clientSessions.TryRemove(client, out _);
        _clients.TryRemove(client, out _);

        try
        {
            client.Close();
        }
        catch
        {
            // ignored – the connection is already closing.
        }
        finally
        {
            client.Dispose();
        }
    }

    private void HandlePublishFailure(TcpClient client, Exception exception, ref List<Exception>? exceptions)
    {
        Console.Error.WriteLine($"{nameof(PublishAsync)} failed for client {client.Client?.RemoteEndPoint}: {exception}");
        if (_eventAggregator is not null)
        {
            try
            {
                var publishFailed = new PublishFailed(client.Client?.RemoteEndPoint, exception);
                var publishTask = _eventAggregator.PublishEventAsync(publishFailed);
                TrackBackgroundTask(publishTask, nameof(PublishFailed));
            }
            catch (Exception aggregatorException)
            {
                Console.Error.WriteLine($"{nameof(HandlePublishFailure)} failed to publish {nameof(PublishFailed)}: {aggregatorException}");
            }
        }

        CleanupClient(client);

        exceptions ??= new List<Exception>();
        exceptions.Add(exception);
    }

}
