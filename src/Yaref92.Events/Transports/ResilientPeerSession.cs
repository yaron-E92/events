using System.Collections.Concurrent;
using System.Reflection;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports;

internal sealed class ResilientPeerSession : IResilientPeerSession
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> _publishCache = new();

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
                await PublishReflectedEventLocally(frame.Payload, cancellationToken).ConfigureAwait(false);

                if (frame.Id != Guid.Empty)
                {
                    sessionClient.EnqueueControlMessage(SessionFrame.CreateAck(frame.Id));
                }
                break;
        }
    }

    private async Task PublishReflectedEventLocally(string payload, CancellationToken cancellationToken)
    {
        (Type? eventType, IDomainEvent? domainEvent) = _eventSerializer.Deserialize(payload);
        // Build the EventReceived<TEvent> type dynamically
        Type eventReceivedType = typeof(EventReceived<>).MakeGenericType(eventType);
        // Find the (DateTime, TEvent) constructor explicitly
        ConstructorInfo ctor = eventReceivedType.GetConstructor([typeof(DateTime), eventType])!
            ?? throw new InvalidOperationException($"Missing expected constructor on {eventReceivedType}");

        // Instantiate EventReceived<TEvent>(DateTime.UtcNow, (TEvent)domainEvent)
        object eventReceivedInstance = ctor.Invoke([DateTime.UtcNow, domainEvent]);

        // Get and specialize PublishEventAsync<TEvent>
        MethodInfo publishMethod = _publishCache.GetOrAdd(eventType, static t =>
            typeof(IEventAggregator)
                .GetMethod(nameof(IEventAggregator.PublishEventAsync))!
                .MakeGenericMethod(t));

        // Call it asynchronously
        var task = (Task) publishMethod.Invoke(_localAggregator, [eventReceivedInstance, cancellationToken])!;
        await task.ConfigureAwait(false);
    }
}
