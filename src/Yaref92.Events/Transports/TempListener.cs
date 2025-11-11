
using System.Collections.Concurrent;
using System.Net.Sockets;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.Transports;
internal class TempListener : IPersistentPortListener
{
    public IInboundConnectionManager ConnectionManager { get; }

    public int Port { get; }
    public ResilientSessionOptions SessionOptions { get; }
    public SessionManager SessionManager { get; }

    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;
    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionRemoved;
    public event Func<SessionKey, SessionFrame, CancellationToken, Task>? FrameReceived;

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _monitorLoop;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _acceptConnectionTasks = [];

    public TempListener(int listenPort, ResilientSessionOptions sessionOptions, IEventSerializer eventSerializer, SessionManager sessionManager)
    {
        Port = listenPort;
        SessionOptions = sessionOptions;
        SessionManager = sessionManager;
        ConnectionManager = new InboundConnectionManager(SessionManager, eventSerializer);
    }

    public async ValueTask DisposeAsync()
    {
        await ConnectionManager.DisposeAsync().ConfigureAwait(false);
        await _cts.CancelAsync();
        _cts.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("Listener already started.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        CreateAndStartTcpListener();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        _monitorLoop = Task.Run(() => MonitorConnectionsAsync(_cts.Token), _cts.Token);

        return Task.CompletedTask;
    }

    private void CreateAndStartTcpListener()
    {
        _listener = TcpListener.Create(Port);
        _listener.Start();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        Task[] tasks = [_acceptLoop ?? Task.CompletedTask, _monitorLoop ?? Task.CompletedTask];
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        await ConnectionManager.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_listener is null)
            {
                CreateAndStartTcpListener();
            }

            TcpClient? incomingTransientConnection = null;
            try
            {
                incomingTransientConnection = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
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

            if (incomingTransientConnection is null)
            {
                continue;
            }

            Task<ConnectionInitializationResult> task = Task.Run(() => ConnectionManager.HandleIncomingTransientConnectionAsync(incomingTransientConnection, cancellationToken), cancellationToken);
            _acceptConnectionTasks[incomingTransientConnection] = task;
            _ = task.ContinueWith(finishedTask =>
            {
                if (finishedTask.IsCompletedSuccessfully && finishedTask is Task<ConnectionInitializationResult> connectionTask)
                {
                    ConnectionInitializationResult result = connectionTask.Result;
                    if (result.IsSuccess && result.Session!.Key is not null)
                    {
                        _ = SessionConnectionAccepted?.Invoke(result.Session.Key, cancellationToken);
                    }
                }
                _acceptConnectionTasks.TryRemove(incomingTransientConnection, out _);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private void MonitorConnectionsAsync(CancellationToken token)
    {
        throw new NotImplementedException();
    }
}
