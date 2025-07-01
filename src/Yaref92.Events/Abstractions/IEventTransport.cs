namespace Yaref92.Events.Abstractions;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Defines the contract for a network event transport capable of publishing and subscribing to domain events across process or network boundaries.
/// </summary>
public interface IEventTransport
{
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
    /// <typeparam name="T">The event type, must implement <see cref="IDomainEvent"/>.</typeparam>
    /// <param name="handler">An asynchronous handler to invoke when an event is received.</param>
    void Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : class, IDomainEvent;
} 