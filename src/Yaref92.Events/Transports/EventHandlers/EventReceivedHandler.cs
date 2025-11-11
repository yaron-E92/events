using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports.EventHandlers;

public interface IEventReceivedHandler
{
    /// <summary>
    /// A property to get the inner (nested) event type of the <see cref="EventReceived{TEvent}"/>, i.e. <see cref="{TEvent}"/>.
    /// </summary>
    Type InnerEventType { get; }
}

/// <summary>
/// Default asynchronous handler for <see cref="EventReceived"/> events.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PublishFailedHandler"/> class.
/// </remarks>
/// <param name="localAggregator">
/// <see cref="IEventTransport"/> used to propogate received events by doing the listener.
/// </param>
public sealed class EventReceivedHandler<TEvent>(Type eventType, IEventAggregator localAggregator) : IEventReceivedHandler, IAsyncEventHandler<EventReceived<TEvent>> where TEvent : class, IDomainEvent
{
    public Type InnerEventType { get; } = eventType ?? throw new ArgumentNullException(nameof(eventType));
    private readonly IEventAggregator _localAggregator = localAggregator ?? throw new ArgumentNullException(nameof(localAggregator));

    /// <inheritdoc />
    public Task OnNextAsync(EventReceived<TEvent> eventReceivedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventReceivedEvent);
        return _localAggregator.PublishEventAsync(eventReceivedEvent.InnerEvent, cancellationToken);
    }
}
