using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

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
    private readonly ConcurrentBag<TcpClient> _clients = new();
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
    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        _listener = new TcpListener(IPAddress.Any, _listenPort);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = AcceptConnectionsLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Connects to a remote peer.
    /// </summary>
    public async Task ConnectToPeerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        _clients.Add(client);
        _ = ReceiveMessagesLoopAsync(client, cancellationToken);
    }

    public async Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        var payload = _serializer.Serialize(domainEvent);
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var lengthPrefix = BitConverter.GetBytes(bytes.Length);

        foreach (var client in _clients)
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
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
            _clients.Add(client);
            _ = ReceiveMessagesLoopAsync(client, cancellationToken);
        }
    }

    private async Task ReceiveMessagesLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var buffer = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            // Read length prefix
            int readLength = await stream.ReadAsync(buffer.AsMemory(0, 4), cancellationToken);
            if (readLength < 4) break;
            int length = BitConverter.ToInt32(buffer, 0);
            var data = new byte[length];
            int received = 0;
            while (received < length)
            {
                readLength = await stream.ReadAsync(data.AsMemory(received, length - received), cancellationToken);
                if (readLength == 0) break;
                received += readLength;
            }
            if (received < length) break;
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

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }
} 
