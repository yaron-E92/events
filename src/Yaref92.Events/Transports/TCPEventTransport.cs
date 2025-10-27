using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    private readonly ConcurrentDictionary<TcpClient, Task> _receiveTasks = new();
    private Task? _acceptConnectionsTask;
    private CancellationTokenSource? _cts;
    private readonly IEventSerializer _serializer;

    public TCPEventTransport(int listenPort, IEventSerializer? serializer = null)
    {
        _listenPort = listenPort;
        _serializer = serializer ?? new JsonEventSerializer();
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
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new InvalidOperationException($"Failed to connect to {host}:{port}.", ex);
        }
        catch (OperationCanceledException ex)
        {
            client.Dispose();
            throw new OperationCanceledException($"Connection attempt to {host}:{port} was canceled.", ex, cancellationToken);
        }

        if (!client.Connected)
        {
            client.Dispose();
            throw new InvalidOperationException($"Failed to connect to {host}:{port}: the TCP client is not connected.");
        }
        _cts ??= new CancellationTokenSource();

        if (!_clients.TryAdd(client, 0))
        {
            client.Dispose();
            throw new InvalidOperationException("The TCP client is already tracked by the transport.");
        }

        StartReceiveLoopForClient(client, cancellationToken);
    }

    public async Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        var payload = _serializer.Serialize(domainEvent);
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var lengthPrefix = BitConverter.GetBytes(bytes.Length);

        foreach (var client in _clients.Keys)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(lengthPrefix, cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
            }
            catch
            {
                // TODO: Handle broken connections
                CleanupClient(client);
            }
        }
    }

    public void Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : class, IDomainEvent
    {
        var bag = _handlers.GetOrAdd(typeof(T), _ => new ConcurrentBag<Func<object, CancellationToken, Task>>());
        bag.Add(async (obj, ct) => await handler((T)obj, ct));
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

                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine($"{nameof(AcceptConnectionsLoopAsync)} socket error: {ex}");
                    continue;
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"{nameof(AcceptConnectionsLoopAsync)} I/O error: {ex}");
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

                if (!_clients.TryAdd(client, 0))
                {
                    client.Dispose();
                    continue;
                }

                StartReceiveLoopForClient(client, cancellationToken);
            }
        }
        finally
        {
            // no-op, method exits when cancellation requested or listener disposed
        }
    }

    private async Task ReceiveMessagesLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4];
            while (!cancellationToken.IsCancellationRequested)
            {
                int readLength;
                try
                {
                    readLength = await stream.ReadAsync(buffer.AsMemory(0, 4), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} I/O error while reading length prefix: {ex}");
                    break;
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} socket error while reading length prefix: {ex}");
                    break;
                }

                if (readLength < 4)
                {
                    break;
                }

                int length = BitConverter.ToInt32(buffer, 0);
                var data = new byte[length];
                int received = 0;
                while (received < length && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        readLength = await stream.ReadAsync(data.AsMemory(received, length - received), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (IOException ex)
                    {
                        Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} I/O error while reading payload: {ex}");
                        received = 0;
                        break;
                    }
                    catch (SocketException ex)
                    {
                        Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} socket error while reading payload: {ex}");
                        received = 0;
                        break;
                    }

                    if (readLength == 0)
                    {
                        received = 0;
                        break;
                    }

                    received += readLength;
                }

                if (received < length)
                {
                    break;
                }

                var payload = System.Text.Encoding.UTF8.GetString(data);
                (Type? type, IDomainEvent? domainEvent) = _serializer.Deserialize(payload);
                if (type != null && _handlers.TryGetValue(type, out var handlersForType))
                {
                    foreach (var handler in handlersForType)
                    {
                        await handler(domainEvent!, cancellationToken);
                    }
                }
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} encountered an I/O error: {ex}");
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"{nameof(ReceiveMessagesLoopAsync)} encountered a socket error: {ex}");
        }
        finally
        {
            CleanupClient(client);
        }
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
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }
        _clients.Clear();
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptConnectionsTask = null;
    }

    private void StartReceiveLoopForClient(TcpClient client, CancellationToken cancellationToken)
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

        var receiveTask = ReceiveMessagesLoopAsync(client, token);
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
        if (_clients.TryRemove(client, out _))
        {
            try
            {
                client.Close();
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
