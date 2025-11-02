using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports.EventHandlers;

/// <summary>
/// Default asynchronous handler for <see cref="EventReceived"/> events.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PublishFailedHandler"/> class.
/// </remarks>
/// <param name="parentTransport">
/// <see cref="IEventTransport"/> used to propogate received events by doing the listener.
/// </param>
public interface IEventReceivedHandler
{}

/// <summary>
/// Default asynchronous handler for <see cref="EventReceived"/> events.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PublishFailedHandler"/> class.
/// </remarks>
/// <param name="localAggregator">
/// <see cref="IEventTransport"/> used to propogate received events by doing the listener.
/// </param>
public sealed class EventReceivedHandler<TEvent>(IEventAggregator localAggregator) : IEventReceivedHandler, IAsyncEventHandler<EventReceived<TEvent>> where TEvent : class, IDomainEvent
{
    private readonly IEventAggregator _localAggregator = localAggregator ?? throw new ArgumentNullException(nameof(localAggregator));

    /// <inheritdoc />
    public Task OnNextAsync(EventReceived<TEvent> eventReceivedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventReceivedEvent);
        return _localAggregator.PublishEventAsync(eventReceivedEvent.InnerEvent, cancellationToken);
    }
}
