//using System.Collections.Concurrent;
//using System.Net.Sockets;

//using Yaref92.Events.Abstractions;
//using Yaref92.Events.Sessions;
//using Yaref92.Events.Transports.ConnectionManagers;
//using Yaref92.Events.Transports.EventHandlers;
//using Yaref92.Events.Transports.Events;

//namespace Yaref92.Events.Transports;

//internal sealed class PersistentPortListener : IPersistentPortListener
//{
//    private readonly InboundConnectionManager _inboundConnectionManager;

//    public IEventTransport Transport { get; }
//    public SessionManager SessionManager { get; }

//    private readonly ConcurrentDictionary<Type, IEventReceivedHandler> _eventHandlers = new();

//    public IInboundConnectionManager ConnectionManager => _inboundConnectionManager;

//    public int Port => _port;

//    private readonly int _port;
//    private readonly ResilientSessionOptions _options;
//    private TcpListener? _listener;
//    private Task? _acceptLoop;
//    private Task? _monitorLoop;
//    private readonly ConcurrentDictionary<SessionKey, SessionState> _sessionStates = new();
//    private readonly CancellationTokenSource _cts = new();

//    private readonly ConcurrentDictionary<TcpClient, Task> _acceptConnectionTasks = new();

//    public PersistentPortListener(int port, ResilientSessionOptions options, IEventTransport eventTransport, SessionManager sessionManager)
//    {
//        ArgumentNullException.ThrowIfNull(options);

//        _inboundConnectionManager = new InboundConnectionManager(options, sessionManager);
//        _port = port;
//        _options = options;
//        Transport = eventTransport;
//        SessionManager = sessionManager;
//    }

//    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted
//    {
//        add => _inboundConnectionManager.SessionConnectionAccepted += value;
//        remove => _inboundConnectionManager.SessionConnectionAccepted -= value;
//    }

//    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionRemoved
//    {
//        add => _inboundConnectionManager.SessionLeft += value;
//        remove => _inboundConnectionManager.SessionLeft -= value;
//    }

//    public event Func<SessionKey, SessionFrame, CancellationToken, Task>? FrameReceived
//    {
//        add => _inboundConnectionManager.FrameReceived += value;
//        remove => _inboundConnectionManager.FrameReceived -= value;
//    }

//    public Task StartAsync(CancellationToken cancellationToken = default)
//    {
//        if (_listener is not null)
//        {
//            throw new InvalidOperationException("Listener already started.");
//        }

//        cancellationToken.ThrowIfCancellationRequested();
//        CreateAndStartTcpListener();

//        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
//        _monitorLoop = Task.Run(() => MonitorConnectionsAsync(_cts.Token), _cts.Token);

//        return Task.CompletedTask;
//    }

//    private void CreateAndStartTcpListener()
//    {
//        _listener = TcpListener.Create(Port);
//        _listener.Start();
//    }

//    //public Task InitConnectionsAsync(CancellationToken cancellationToken = default)
//    //{
//    //    return _inboundConnectionManager.InitConnectionsAsync(cancellationToken);
//    //}

//    //public Task StopAsync(CancellationToken cancellationToken = default)
//    //{
//    //    return _inboundConnectionManager.StopAsync(cancellationToken);
//    //}

//    public async Task StopAsync(CancellationToken cancellationToken = default)
//    {
//        await _cts.CancelAsync().ConfigureAwait(false);
//        _listener?.Stop();

//        Task[] tasks = [_inboundConnectionManager.StopAsync(cancellationToken), _acceptLoop ?? Task.CompletedTask, _monitorLoop ?? Task.CompletedTask];
//        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);

//        foreach (var session in _sessionStates.Values)
//        {
//            await session.DisposeAsync().ConfigureAwait(false);
//        }

//        _sessionStates.Clear();
//    }

//    //SHOULD NOT BE HERE ANYMORE
//    //public IInboundResilientConnection GetOrCreatePersistentClient(
//    //    SessionKey sessionKey,
//    //    Func<SessionKey, ResilientSessionConnection> clientFactory)
//    //{
//    //    ArgumentNullException.ThrowIfNull(clientFactory);

//    //    return _inboundConnectionManager.GetOrCreateInboundConnection(sessionKey, clientFactory);
//    //}

//    //public void Broadcast(string payload)
//    //{
//    //    ArgumentNullException.ThrowIfNull(payload);

//    //    _inboundConnectionManager.QueueEventBroadcast(payload);
//    //}

//    public async ValueTask DisposeAsync()
//    {
//        await _inboundConnectionManager.DisposeAsync().ConfigureAwait(false);
//        await _cts.CancelAsync();
//        _cts.Dispose();
//    }

//    public async Task HandleReceivedEventAsync<TEvent>(EventReceived<TEvent> domainEvent, CancellationToken cancellationToken = default) where TEvent : class, IDomainEvent
//    {
//        _eventHandlers.TryGetValue(domainEvent.InnerEvent.GetType(), out IEventReceivedHandler? eventReceivedHandler);
//        await (eventReceivedHandler as EventReceivedHandler<TEvent>)?.OnNextAsync(domainEvent, cancellationToken)!;
//    }

//    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
//    {
//        if (_listener is null)
//        {
//            return;
//        }

//        while (!cancellationToken.IsCancellationRequested)
//        {
//            TcpClient? incomingTransientConnection = null;
//            try
//            {
//                incomingTransientConnection = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
//            }
//            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
//            {
//                break;
//            }
//            catch (ObjectDisposedException)
//            {
//                break;
//            }
//            catch (Exception ex)
//            {
//                await Console.Error.WriteLineAsync($"{nameof(AcceptLoopAsync)} failed: {ex}").ConfigureAwait(false);
//                continue;
//            }

//            if (incomingTransientConnection is null)
//            {
//                continue;
//            }

//            var task = Task.Run(() => _inboundConnectionManager.HandleIncomingTransientConnectionAsync(incomingTransientConnection, cancellationToken), cancellationToken);
//            _acceptConnectionTasks[incomingTransientConnection] = task;
//            _ = task.ContinueWith(_ => _acceptConnectionTasks.TryRemove(incomingTransientConnection, out _), TaskContinuationOptions.ExecuteSynchronously);
//        }
//    }

//    private async ValueTask OnFrameReceivedAsync(ResilientSessionConnection sessionClient, SessionFrame frame, CancellationToken cancellationToken)
//    {
//        switch (frame.Kind)
//        {
//            case SessionFrameKind.Event when frame.Payload is not null:
//                await PublishEventLocallyAsync(frame.Payload, cancellationToken).ConfigureAwait(false);

//                if (frame.Id != Guid.Empty)
//                {
//                    Transport.AcknowledgeEventReceipt(frame.Id, sessionClient.SessionKey);
//                }
//                break;
//        }
//    }

//    private async Task PublishEventLocallyAsync(string payload, CancellationToken cancellationToken)
//    {
//        if (_localAggregator is null)
//        {
//            return;
//        }

//        (_, IDomainEvent? domainEvent) = _eventSerializer.Deserialize(payload);
//        if (domainEvent is null)
//        {
//            return;
//        }

//        await PublishDomainEventAsync(domainEvent, cancellationToken).ConfigureAwait(false);
//    }

//    private Task PublishDomainEventAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
//    {
//        if (_localAggregator is null)
//        {
//            return Task.CompletedTask;
//        }

//        dynamic aggregator = _localAggregator;
//        return (Task) aggregator.PublishEventAsync((dynamic) domainEvent, cancellationToken);
//    }

//    private async Task MonitorConnectionsAsync(CancellationToken cancellationToken)
//    {
//        while (!cancellationToken.IsCancellationRequested)
//        {
//            try
//            {
//                await Task.Delay(SessionFrameContract.GetHeartbeatInterval(_options), cancellationToken).ConfigureAwait(false);
//            }
//            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
//            {
//                break;
//            }

//            var now = DateTime.UtcNow;
//            var timeout = SessionFrameContract.GetHeartbeatTimeout(_options);
//            foreach (var session in _sessionStates.Values.Where(session => session.IsExpired(now, timeout)))
//            {
//                await session.CloseConnectionAsync().ConfigureAwait(false);
//            }
//        }
//    }
//}
