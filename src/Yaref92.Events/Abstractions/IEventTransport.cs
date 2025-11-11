namespace Yaref92.Events.Abstractions;

/// <summary>
/// Defines the contract for a network event transport capable of publishing and subscribing to domain events across process or network boundaries.
/// </summary>
public interface IEventTransport
{
    /// <summary>
    /// An event that is triggered when a domain event is received from the listener's connection manager.
    /// Hooked into <see cref="NetworkedEventAggregator"/>'s event handling method
    /// </summary>
    event Func<IDomainEvent, Task<bool>> EventReceived;

    internal IPersistentPortListener PersistentPortListener { get; }

    internal IPersistentFramePublisher PersistentFramePublisher { get; }

    /// <summary>
    /// Accepts an incoming event and uses local aggregator to handle that.
    /// </summary>
    /// <typeparam name="T">The event type, must implement <see cref="IDomainEvent"/>.</typeparam>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>Should be the Persistent's listener's responsibility</remarks>
    Task AcceptIncomingEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent;

    /// <summary>
    /// Publishes an event asynchronously to the transport.
    /// </summary>
    /// <typeparam name="T">The event type, must implement <see cref="IDomainEvent"/>.</typeparam>
    /// <param name="domainEvent">The event instance to publish.</param>
    /// <param name="cancellationToken">A cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent;

    /// <summary>
    /// Subscribes to events of a specific type received from the transport.
    /// </summary>
    /// <typeparam name="TEvent">The event type, must implement <see cref="IDomainEvent"/>.</typeparam>
    void Subscribe<TEvent>() where TEvent : class, IDomainEvent;
}
