using System.Net.Sockets;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports;

using static Yaref92.Events.Abstractions.IInboundResilientConnection;

namespace Yaref92.Events.Sessions;

internal sealed partial class ResilientPeerSession : IResilientPeerSession
{
    private readonly SessionState _state;
    private ResilientSessionConnection _resilientConnection;
    private readonly IEventAggregator? _localAggregator;
    private readonly IEventSerializer _eventSerializer;

    public SessionOutboundBuffer OutboundBuffer { get; }

    public string AuthToken => _resilientConnection.SessionToken;

    public ResilientSessionConnection PersistentClient => _resilientConnection;

    public SessionKey Key { get; }
    public ResilientSessionOptions Options { get; }

    public bool HasAuthenticated => _state.HasAuthenticated;

    public IOutboundResilientConnection OutboundConnection => _resilientConnection;

    public IInboundResilientConnection InboundConnection => _resilientConnection;

    public bool IsAnonymous { get; init; }

    event SessionFrameReceivedHandler? IResilientPeerSession.FrameReceived
    {
        add => InboundConnection.FrameReceived += value;
        remove => InboundConnection.FrameReceived -= value;
    }

    public ResilientPeerSession(SessionKey sessionKey,
        ResilientSessionOptions options,
        IEventAggregator? eventAggregator, IEventSerializer eventSerializer)
        : this(sessionKey,
            new ResilientSessionConnection(sessionKey, options, eventAggregator),
            options,
            eventAggregator,
            eventSerializer, null)
    {
    }

    internal ResilientPeerSession(SessionKey sessionKey,
        ResilientSessionConnection connection,
        ResilientSessionOptions options,
        IEventAggregator? eventAggregator,
        IEventSerializer eventSerializer,
        SessionState? state)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(eventSerializer);

        Key = sessionKey;
        Options = options;
        _resilientConnection = connection;
        _localAggregator = eventAggregator;
        _eventSerializer = eventSerializer;
        _state = state ?? new SessionState(Key);
        OutboundBuffer = new SessionOutboundBuffer();
    }

    public void AttachResilientConnection(IResilientConnection resilientConnection)
    {
        _resilientConnection = resilientConnection as ResilientSessionConnection ?? throw new ArgumentNullException(nameof(resilientConnection));
    }

    public void Touch()
    {
        _state.Touch();
        _resilientConnection?.RecordRemoteActivity();
    }

    public Task InitConnectionsAsync(CancellationToken cancellationToken)
    {
        Task[] tasks = [InboundConnection.InitAsync(cancellationToken), OutboundConnection.InitAsync(cancellationToken)];
        return Task.WhenAll(tasks);
    }

    // SHOULD NOT BE THE RESPONSIBILITY OF THE SESSION
    //public Task PublishToAllAsync(string payload, CancellationToken cancellationToken)
    //{
    //    return OutboundConnection.EnqueueEventAsync(payload, cancellationToken);
    //}

    public async ValueTask DisposeAsync()
    {
        await CloseConnectionAsync().ConfigureAwait(false);
        await OutboundConnection.DumpBuffer(OutboundBuffer);
        OutboundBuffer.Dispose();
        InboundConnection.FrameReceived -= OnFrameReceivedAsync;
        await _resilientConnection.DisposeAsync().ConfigureAwait(false);
    }

    //private async Task OnFrameReceivedAsync(ResilientSessionConnection resilientIncomingConnection, SessionFrame frame, CancellationToken cancellationToken)
    //{
    //    switch (frame.Kind)
    //    {
    //        case SessionFrameKind.Event when frame.Payload is not null:
    //            await PublishIncomingEventLocallyAsync(frame.Payload, cancellationToken).ConfigureAwait(false);

    //            if (frame.Id != Guid.Empty)
    //            {
    //                resilientIncomingConnection.EnqueueFrame(SessionFrame.CreateAck(frame.Id));
    //            }
    //            break;
    //        case SessionFrameKind.Ack when frame.Id != Guid.Empty:
    //            Acknowledge(frame.Id);
    //            break;
    //        case SessionFrameKind.Ping:
    //            EnqueueFrame(SessionFrame.CreatePong());
    //            break;
    //    }
    //}

    //private async Task PublishIncomingEventLocallyAsync(string payload, CancellationToken cancellationToken)
    //{
    //    if (_localAggregator is null)
    //    {
    //        return;
    //    }

    //    (_, IDomainEvent? domainEvent) = _eventSerializer.Deserialize(payload);
    //    if (domainEvent is null)
    //    {
    //        return;
    //    }

    //    await PublishDomainEventAsync(domainEvent, cancellationToken).ConfigureAwait(false);
    //}

    //private Task PublishDomainEventAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    //{
    //    if (_localAggregator is null)
    //    {
    //        return Task.CompletedTask;
    //    }

    //    dynamic aggregator = _localAggregator;
    //    return (Task) aggregator.PublishEventAsync((dynamic) domainEvent, cancellationToken);
    //}

    public async Task CloseConnectionAsync()
    {
        var previous = DetachCurrentConnection();
        if (previous is null)
        {
            return;
        }

        await DisposeConnectionAsync(previous.Value).ConfigureAwait(false);
        OutboundBuffer.RequeueInflight();
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

    // SHOULD NOT BE THE RESPONSIBILITY OF THE SESSION
    //public void EnqueueEvent(Guid eventId, string payload)
    //{
    //    OutboundBuffer.EnqueueEvent(eventId, payload);
    //}

    public void RegisterAuthentication()
    {
        _state.RegisterAuthentication();
    }

    internal partial class SessionState { }
}
