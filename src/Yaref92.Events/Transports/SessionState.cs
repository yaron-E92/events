using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports;

internal sealed class SessionState : IAsyncDisposable
{
    private readonly object _lock = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _sendTask;
    private ResilientSessionClient? _persistentClient;
    private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;

    public SessionState(SessionKey key)
    {
        Key = key;
        Outbound = new SessionOutboundBuffer();
    }

    public SessionKey Key { get; }

    public bool HasAuthenticated { get; private set; }

    public SessionOutboundBuffer Outbound { get; }

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

    public async Task AttachAsync(
        TcpClient client,
        NetworkStream stream,
        CancellationTokenSource connectionCancellation,
        Func<SessionState, NetworkStream, CancellationToken, Task> sendLoopFactory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(connectionCancellation);
        ArgumentNullException.ThrowIfNull(sendLoopFactory);

        var previous = DetachCurrentConnection();
        if (previous is not null)
        {
            await DisposeConnectionAsync(previous.Value).ConfigureAwait(false);
        }

        Outbound.RequeueInflight();

        var sendTask = Task.Run(() => sendLoopFactory(this, stream, connectionCancellation.Token), connectionCancellation.Token);

        lock (_lock)
        {
            _client = client;
            _stream = stream;
            _connectionCancellation = connectionCancellation;
            _sendTask = sendTask;
        }

        Touch();
        _persistentClient?.RecordRemoteActivity();
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

    public async Task CloseConnectionAsync()
    {
        var previous = DetachCurrentConnection();
        if (previous is null)
        {
            return;
        }

        await DisposeConnectionAsync(previous.Value).ConfigureAwait(false);
        Outbound.RequeueInflight();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseConnectionAsync().ConfigureAwait(false);
        Outbound.Dispose();
    }

    private (TcpClient? Client, NetworkStream? Stream, CancellationTokenSource? Cancellation, Task? SendTask)? DetachCurrentConnection()
    {
        lock (_lock)
        {
            if (_client is null && _stream is null && _connectionCancellation is null && _sendTask is null)
            {
                return null;
            }

            var snapshot = (_client, _stream, _connectionCancellation, _sendTask);
            _client = null;
            _stream = null;
            _connectionCancellation = null;
            _sendTask = null;
            return snapshot;
        }
    }

    private static async Task DisposeConnectionAsync((TcpClient? Client, NetworkStream? Stream, CancellationTokenSource? Cancellation, Task? SendTask) connection)
    {
        try
        {
            if (connection.Cancellation is not null)
            {
                try
                {
                    await connection.Cancellation.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // cancellation source already disposed
                }
            }

            if (connection.SendTask is not null)
            {
                try
                {
                    await connection.SendTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    await Console.Error.WriteLineAsync($"{nameof(SessionState)} send loop closed with {ex}")
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected when cancellation is requested
                }
            }

            if (connection.Stream is not null)
            {
                await connection.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            connection.Client?.Dispose();
            connection.Cancellation?.Dispose();
        }
    }
}
