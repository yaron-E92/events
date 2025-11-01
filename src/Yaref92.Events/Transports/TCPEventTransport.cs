using System.Collections.Concurrent;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;

namespace Yaref92.Events.Transports;

public class TCPEventTransport : IEventTransport, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Type, ConcurrentBag<Func<object, CancellationToken, Task>>> _handlers = new();

    private readonly int _listenPort;
    private readonly IEventSerializer _serializer;
    private readonly IEventAggregator? _eventAggregator;
    private readonly ResilientSessionOptions _sessionOptions;
    private readonly PersistentSessionListener _listener;
    private readonly PersistentEventPublisher _publisher;

    public TCPEventTransport(
        int listenPort,
        IEventSerializer? serializer = null,
        IEventAggregator? eventAggregator = null,
        TimeSpan? heartbeatInterval = null,
        string? authenticationToken = null)
    {
        _listenPort = listenPort;
        _serializer = serializer ?? new JsonEventSerializer();
        _eventAggregator = eventAggregator;

        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        _sessionOptions = new ResilientSessionOptions
        {
            RequireAuthentication = authenticationToken is not null,
            AuthenticationToken = authenticationToken,
            HeartbeatInterval = interval,
            HeartbeatTimeout = TimeSpan.FromTicks(interval.Ticks * 2),
        };

        _eventAggregator?.RegisterEventType<PublishFailed>();
        _eventAggregator?.RegisterEventType<MessageReceived>();
        _eventAggregator?.SubscribeToEventType(new PublishFailedHandler());

        _listener = new PersistentSessionListener(_listenPort, _sessionOptions);
        _listener.EnvelopeReceived += OnEnvelopeReceivedAsync;

        _publisher = new PersistentEventPublisher(_listener, _sessionOptions, _listener.PayloadHandler, _eventAggregator);
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        return _listener.StartAsync(cancellationToken);
    }

    public Task ConnectToPeerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
        }

        return _publisher.ConnectAsync(host, port, cancellationToken);
    }

    public async Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var payload = _serializer.Serialize(domainEvent);
        _listener.Broadcast(payload);
        await _publisher.PublishAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public void Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : class, IDomainEvent
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var bag = _handlers.GetOrAdd(typeof(T), _ => new ConcurrentBag<Func<object, CancellationToken, Task>>());
        bag.Add(async (obj, ct) => await handler((T)obj, ct).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        _listener.EnvelopeReceived -= OnEnvelopeReceivedAsync;

        await _publisher.DisposeAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnEnvelopeReceivedAsync(string sessionKey, EventEnvelope envelope, string payload, CancellationToken cancellationToken)
    {
        return HandleInboundMessageAsync(sessionKey, payload, cancellationToken);
    }

    private async Task HandleInboundMessageAsync(string sessionKey, string payload, CancellationToken cancellationToken)
    {
        await NotifyMessageReceivedAsync(sessionKey, payload, cancellationToken).ConfigureAwait(false);
        await DispatchEventAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyMessageReceivedAsync(string sessionKey, string payload, CancellationToken cancellationToken)
    {
        var messageEvent = new MessageReceived(sessionKey, payload);

        if (_eventAggregator is not null)
        {
            await _eventAggregator.PublishEventAsync(messageEvent, cancellationToken).ConfigureAwait(false);
        }

        if (_handlers.TryGetValue(typeof(MessageReceived), out var handlers) && handlers.Count > 0)
        {
            await InvokeHandlersAsync(handlers, messageEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task DispatchEventAsync(string payload, CancellationToken cancellationToken)
    {
        Type? eventType;
        IDomainEvent? domainEvent;
        try
        {
            (eventType, domainEvent) = _serializer.Deserialize(payload);
        }
        catch (JsonException ex)
        {
            return Console.Error.WriteLineAsync($"Failed to deserialize event envelope: {ex}");
        }

        if (eventType is null)
        {
            return Console.Error.WriteLineAsync($"Unknown or missing event type.");
        }

        if (domainEvent is null)
        {
            return Console.Error.WriteLineAsync($"missing event.");
        }

        if (!_handlers.TryGetValue(eventType, out var handlers) || handlers.Count == 0)
        {
            return Console.Error.WriteLineAsync($"No handlers found for the event type {eventType}");
        }

        return InvokeHandlersAsync(handlers, domainEvent, cancellationToken);
    }

    private static Task InvokeHandlersAsync(IEnumerable<Func<object, CancellationToken, Task>> handlers, object domainEvent, CancellationToken cancellationToken)
    {
        var tasks = handlers.Select(handler => handler(domainEvent, cancellationToken));
        return Task.WhenAll(tasks);
    }
}
