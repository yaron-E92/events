using System;
using System.Net.Sockets;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports;
namespace Yaref92.Events.Sessions;

internal sealed class ResilientPeerSession : IResilientPeerSession
{
    private readonly SessionState _state;
    private ResilientSessionConnection _resilientClient;
    private readonly IEventAggregator? _localAggregator;
    private readonly IEventSerializer _eventSerializer;

    public SessionOutboundBuffer OutboundBuffer { get; }

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

    public ResilientPeerSession(SessionKey sessionKey,
        ResilientSessionConnection client,
        ResilientSessionOptions options,
        IEventAggregator? eventAggregator,
        IEventSerializer eventSerializer,
        SessionState? state)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(eventSerializer);

        Key = sessionKey;
        Options = options;
        _resilientClient = client;
        _localAggregator = eventAggregator;
        _eventSerializer = eventSerializer;
        _resilientClient.FrameReceived += OnFrameReceivedAsync;
        _state = state ?? new SessionState(Key);
        OutboundBuffer = new SessionOutboundBuffer();
    }

    public void AttachResilientConnection(ResilientSessionConnection client)
    {
        _resilientClient = client ?? throw new ArgumentNullException(nameof(client));
    }

    public void Touch()
    {
        _state.Touch();
        _resilientClient?.RecordRemoteActivity();
    }

    public string SessionToken => _resilientClient.SessionToken;

    public ResilientSessionConnection PersistentClient => _resilientClient;

    public SessionKey Key { get; }
    public ResilientSessionOptions Options { get; }

    public bool HasAuthenticated => _state.HasAuthenticated;

    public IOutboundResilientConnection OutboundConnection => _resilientClient;

    public IInboundResilientConnection InboundConnection => _resilientClient;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Task[] tasks = [InboundConnection.InitAsync(cancellationToken), OutboundConnection.InitAsync(cancellationToken)];
        return Task.WhenAll(tasks);
    }

    public Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        return OutboundConnection.EnqueueEventAsync(payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseConnectionAsync().ConfigureAwait(false);
        OutboundConnection.DumpBuffer(OutboundBuffer);
        OutboundBuffer.Dispose();
        _resilientClient.FrameReceived -= OnFrameReceivedAsync;
        await _resilientClient.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask OnFrameReceivedAsync(ResilientSessionConnection sessionClient, SessionFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Event when frame.Payload is not null:
                await PublishEventLocallyAsync(frame.Payload, cancellationToken).ConfigureAwait(false);

                if (frame.Id != Guid.Empty)
                {
                    sessionClient.EnqueueFrame(SessionFrame.CreateAck(frame.Id));
                }
                break;
        }
    }

    private async Task PublishEventLocallyAsync(string payload, CancellationToken cancellationToken)
    {
        if (_localAggregator is null)
        {
            return;
        }

        (_, IDomainEvent? domainEvent) = _eventSerializer.Deserialize(payload);
        if (domainEvent is null)
        {
            return;
        }

        await PublishDomainEventAsync(domainEvent, cancellationToken).ConfigureAwait(false);
    }

    private Task PublishDomainEventAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        if (_localAggregator is null)
        {
            return Task.CompletedTask;
        }

        dynamic aggregator = _localAggregator;
        return (Task) aggregator.PublishEventAsync((dynamic) domainEvent, cancellationToken);
    }

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

    public void EnqueueEvent(Guid eventId, string payload)
    {
        OutboundBuffer.EnqueueEvent(eventId, payload);
    }
}
