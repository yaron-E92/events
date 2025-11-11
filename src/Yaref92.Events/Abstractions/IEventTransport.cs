namespace Yaref92.Events.Abstractions;

using System;
using System.Threading;
using System.Threading.Tasks;

using Yaref92.Events.Sessions;

/// <summary>
/// Defines the contract for a network event transport capable of publishing and subscribing to domain events across process or network boundaries.
/// </summary>
public interface IEventTransport
{
    /// <summary>
    /// Accepts an incoming event and uses local aggregator to handle that.
    /// </summary>
    /// <typeparam name="T">The event type, must implement <see cref="IDomainEvent"/>.</typeparam>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AcceptIncomingTrafficAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent;
    void AcknowledgeEventReceipt(Guid eventId, SessionKey sessionKey);

    /// <summary>
    /// Publishes an event asynchronously to the transport.
    /// </summary>
    /// <typeparam name="T">The event type, must implement <see cref="IDomainEvent"/>.</typeparam>
    /// <param name="domainEvent">The event instance to publish.</param>
    /// <param name="cancellationToken">A cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent;

    /// <summary>
    /// Subscribes to events of a specific type received from the transport.
    /// </summary>
    /// <typeparam name="TEvent">The event type, must implement <see cref="IDomainEvent"/>.</typeparam>
    void Subscribe<TEvent>() where TEvent : class, IDomainEvent;
}
