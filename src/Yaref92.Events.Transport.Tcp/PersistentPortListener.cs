using System.Collections.Concurrent;
using System.Net.Sockets;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Tcp.Abstractions;
using Yaref92.Events.Transport.Tcp.ConnectionManagers;
using Yaref92.Events.Transports;

namespace Yaref92.Events.Transport.Tcp;
internal class PersistentPortListener(int listenPort, IEventSerializer eventSerializer, TcpSessionManager sessionManager) : IPersistentPortListener
{

    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;

    event IEventTransport.SessionInboundConnectionDroppedHandler? IPersistentPortListener.SessionInboundConnectionDropped
    {
        add => ConnectionManager.SessionInboundConnectionDropped += value;
        remove => ConnectionManager.SessionInboundConnectionDropped -= value;
    }

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _acceptConnectionTasks = [];

    public IInboundConnectionManager ConnectionManager { get; } = new InboundConnectionManager(sessionManager, eventSerializer);

    public int Port { get; } = listenPort;

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
                if (DidAcceptConnectionTaskCompleteSuccessfullyAndHasValidInitializationResult(finishedTask, out ConnectionInitializationResult connectionResult)
                    && connectionResult.IsSuccess && connectionResult.Session!.Key is not null)
                {
                    _ = SessionConnectionAccepted?.Invoke(connectionResult.Session.Key, cancellationToken);
                }
                _acceptConnectionTasks.TryRemove(incomingTransientConnection, out _);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private static bool DidAcceptConnectionTaskCompleteSuccessfullyAndHasValidInitializationResult(Task<ConnectionInitializationResult> finishedTask, out ConnectionInitializationResult connectionResult)
    {
        bool didItCompleteSuccessfullyWithValidResult = finishedTask.IsCompletedSuccessfully && finishedTask.Result is ConnectionInitializationResult { };
        connectionResult = didItCompleteSuccessfullyWithValidResult ? finishedTask.Result : default!;
        return didItCompleteSuccessfullyWithValidResult;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        Task acceptLoopTask = _acceptLoop ?? Task.CompletedTask;
        await acceptLoopTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"{nameof(PersistentPortListener)} stop failed: {ex}").ConfigureAwait(false);
        }

        try
        {
            await ConnectionManager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(PersistentPortListener)} disposal failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
