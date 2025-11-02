using System.Collections.Concurrent;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.EventHandlers;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports;

public sealed class PersistentSessionListener : IAsyncDisposable
{
    private readonly ResilientTcpServer _server;

    public IEventTransport Transport { get; }

    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Type, IEventReceivedHandler> _eventHandlers = new();

    public PersistentSessionListener(int port, ResilientSessionOptions options, IEventTransport eventTransport)
    {
        ArgumentNullException.ThrowIfNull(options);

        _server = new ResilientTcpServer(port, options);

        Transport = eventTransport;
    }

    //internal Func<string, string, CancellationToken, Task> PayloadHandler => HandleInboundPayloadAsync;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _server.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _server.StopAsync(cancellationToken);
    }

    public void RegisterPersistentSession(IResilientPeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _server.RegisterPersistentClient(session.SessionKey, session.PersistentClient);
    }

    public void Broadcast(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        _server.QueueBroadcast(payload);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    //private async Task HandleInboundPayloadAsync(string sessionKey, string payload, CancellationToken cancellationToken)
    //{
    //    EventEnvelope? envelope;
    //    try
    //    {
    //        envelope = JsonSerializer.Deserialize<EventEnvelope>(payload, _serializerOptions);
    //    }
    //    catch (JsonException ex)
    //    {
    //        await Console.Error.WriteLineAsync($"Failed to deserialize event envelope: {ex}").ConfigureAwait(false);
    //        return;
    //    }

    //    if (envelope is null)
    //    {
    //        return;
    //    }

    //    var handler = EnvelopeReceived;
    //    if (handler is not null)
    //    {
    //        await handler.Invoke(sessionKey, envelope, payload, cancellationToken).ConfigureAwait(false);
    //    }
    //}

    //private Task OnSessionJoinedInternalAsync(string sessionKey, CancellationToken cancellationToken)
    //{
    //    var handler = SessionJoined;
    //    return handler is null
    //        ? Task.CompletedTask
    //        : handler.Invoke(sessionKey, cancellationToken);
    //}

    //private Task OnSessionLeftInternalAsync(string sessionKey, CancellationToken cancellationToken)
    //{
    //    var handler = SessionLeft;
    //    return handler is null
    //        ? Task.CompletedTask
    //        : handler.Invoke(sessionKey, cancellationToken);
    //}

    public async Task HandleReceivedEventAsync<TEvent>(EventReceived<TEvent> domainEvent, CancellationToken cancellationToken = default) where TEvent : class, IDomainEvent
    {
        _eventHandlers.TryGetValue(domainEvent.InnerEvent.GetType(), out IEventReceivedHandler? eventReceivedHandler);
        await (eventReceivedHandler as EventReceivedHandler<TEvent>)?.OnNextAsync(domainEvent, cancellationToken)!;
    }
}
