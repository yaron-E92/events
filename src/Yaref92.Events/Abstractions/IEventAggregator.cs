namespace Yaref92.Events.Abstractions;

/// <summary>
/// Defines the contract for an event aggregator that manages event types, subscriptions, and event publishing.
/// </summary>
public interface IEventAggregator
{
    /// <summary>
    /// Gets the set of registered event types.
    /// </summary>
    ISet<Type> EventTypes { get; }

    /// <summary>
    /// Gets the collection of all current subscribers.
    /// </summary>
    IReadOnlyCollection<IEventSubscriber> Subscribers { get; }

    /// <summary>
    /// Registers a new event type.
    /// </summary>
    /// <typeparam name="T">The event type to register.</typeparam>
    /// <returns>True if the event type was registered; false if it was already registered.</returns>
    bool RegisterEventType<T>() where T : class, IDomainEvent;

    /// <summary>
    /// Publishes an event synchronously to all synchronous subscribers.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="domainEvent">The event instance to publish.</param>
    void PublishEvent<T>(T domainEvent) where T : class, IDomainEvent;

    /// <summary>
    /// Subscribes a synchronous subscriber to an event type.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="subscriber">The subscriber instance.</param>
    void SubscribeToEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent;

    void SubscribeToEventType<T>(IAsyncEventSubscriber<T> subscriber) where T : class, IDomainEvent;

    /// <summary>
    /// Unsubscribes a synchronous subscriber from an event type.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="subscriber">The subscriber instance.</param>
    void UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent;

    void UnsubscribeFromEventType<T>(IAsyncEventSubscriber<T> subscriber) where T : class, IDomainEvent;

    Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent;
}
