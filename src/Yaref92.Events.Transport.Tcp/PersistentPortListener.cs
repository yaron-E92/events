using System.Collections.Concurrent;
using System.Net.Sockets;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Tcp.Abstractions;
using Yaref92.Events.Transport.Tcp.ConnectionManagers;
using Yaref92.Events.Transports;

namespace Yaref92.Events.Transport.Tcp;
internal class PersistentPortListener : IPersistentPortListener
{
    private const string UnknownPeer = "unknown";
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _acceptConnectionTasks = [];
    private readonly SemaphoreSlim _handshakeCapacity;
    private readonly IngressDiagnostics _diagnostics;
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public PersistentPortListener(
        int listenPort,
        IEventSerializer eventSerializer,
        TcpSessionManager sessionManager,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        Port = listenPort;
        _handshakeCapacity = new SemaphoreSlim(sessionManager.Options.MaxInboundConnections, sessionManager.Options.MaxInboundConnections);
        _diagnostics = new IngressDiagnostics(logger);
        var limiter = new PeerIngressLimiter(sessionManager.Options);
        var activeSessions = new ActiveInboundSessionRegistry(sessionManager.Options);
        ConnectionManager = new InboundConnectionManager(sessionManager, eventSerializer, limiter, activeSessions, _diagnostics);
    }

    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;

    event IEventTransport.SessionInboundConnectionDroppedHandler? IPersistentPortListener.SessionInboundConnectionDropped
    {
        add => ConnectionManager.SessionInboundConnectionDropped += value;
        remove => ConnectionManager.SessionInboundConnectionDropped -= value;
    }

    public IInboundConnectionManager ConnectionManager { get; }

    public int Port { get; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("Listener already started.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        CreateAndStartTcpListener();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private void CreateAndStartTcpListener()
    {
        _listener = TcpListener.Create(Port);
        _listener.Start();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_listener is null)
            {
                CreateAndStartTcpListener();
            }

            TcpListener listener = _listener ?? throw new InvalidOperationException("Listener could not be started.");
            TcpClient? incomingTransientConnection;
            try
            {
                incomingTransientConnection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
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
                _diagnostics.Rejected("accept-error", UnknownPeer, exception: ex);
                continue;
            }

            string peer = incomingTransientConnection.Client.RemoteEndPoint is System.Net.IPEndPoint remoteEndPoint
                ? remoteEndPoint.Address.ToString()
                : UnknownPeer;
            if (!await _handshakeCapacity.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _diagnostics.Rejected("connection-limit", peer);
                incomingTransientConnection.Dispose();
                continue;
            }

            var lease = new CapacityLease(_handshakeCapacity);
            Task<ConnectionInitializationResult> initializationTask =
                ConnectionManager.HandleIncomingTransientConnectionAsync(incomingTransientConnection, cancellationToken);
            _acceptConnectionTasks[incomingTransientConnection] = initializationTask;
            _ = ObserveAcceptedConnectionAsync(incomingTransientConnection, initializationTask, lease, cancellationToken);
        }
    }

    private async Task ObserveAcceptedConnectionAsync(
        TcpClient client,
        Task<ConnectionInitializationResult> initializationTask,
        CapacityLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            ConnectionInitializationResult result = await initializationTask.ConfigureAwait(false);
            if (!result.IsSuccess || result.Session is not { Key: not null } session)
            {
                return;
            }

            Func<SessionKey, CancellationToken, Task>? handler = SessionConnectionAccepted;
            if (handler is not null)
            {
                await handler(session.Key, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener is stopping.
        }
        catch (Exception ex)
        {
            _diagnostics.Rejected("connection-initialization-error", UnknownPeer, exception: ex);
        }
        finally
        {
            _acceptConnectionTasks.TryRemove(client, out _);
            lease.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        Task acceptLoopTask = _acceptLoop ?? Task.CompletedTask;
        await acceptLoopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(_acceptConnectionTasks.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            _diagnostics.Rejected("listener-stop-error", UnknownPeer, exception: ex);
        }

        try
        {
            await ConnectionManager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Rejected("listener-disposal-error", UnknownPeer, exception: ex);
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _handshakeCapacity.Dispose();
        }
    }

    private sealed class CapacityLease(SemaphoreSlim semaphore)
    {
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try
                {
                    semaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The listener completed disposal before the connection callback ran.
                }
            }
        }
    }
}
