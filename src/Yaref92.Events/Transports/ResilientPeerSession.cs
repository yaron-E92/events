using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
namespace Yaref92.Events.Transports;

internal sealed class ResilientPeerSession : IResilientPeerSession
{
    private readonly ResilientSessionClient _client;
    private readonly IEventAggregator? _localAggregator;
    private readonly IEventSerializer _eventSerializer;

    public ResilientPeerSession(SessionKey sessionKey,
        ResilientSessionOptions options,
        IEventAggregator? eventAggregator, IEventSerializer eventSerializer)
    {
        SessionKey = sessionKey;
        Options = options;
        _client = new ResilientSessionClient(sessionKey, options, eventAggregator);
        _localAggregator = eventAggregator;
        _eventSerializer = eventSerializer;
        _client.FrameReceived += OnFrameReceivedAsync;
        _ = StartAsync(CancellationToken.None);
    }

    public string SessionToken => _client.SessionToken;

    public ResilientSessionClient PersistentClient => _client;

    public SessionKey SessionKey { get; }
    public ResilientSessionOptions Options { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return PersistentClient.StartAsync(cancellationToken);
    }

    public Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        return PersistentClient.EnqueueEventAsync(payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _client.FrameReceived -= OnFrameReceivedAsync;
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask OnFrameReceivedAsync(ResilientSessionClient sessionClient, SessionFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Event when frame.Payload is not null:
                await PublishEventLocallyAsync(frame.Payload, cancellationToken).ConfigureAwait(false);

                if (frame.Id != Guid.Empty)
                {
                    sessionClient.EnqueueControlMessage(SessionFrame.CreateAck(frame.Id));
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
}
