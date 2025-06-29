namespace Yaref92.Events.Abstractions;

/// <summary>
/// Marker interface for a general event subscriber.
/// </summary>
public interface IEventSubscriber
{
}

/// <summary>
/// Strongly typed synchronous subscriber to a domain event.
/// </summary>
/// <typeparam name="T">A class implementing <see cref="IDomainEvent"/></typeparam>
public interface IEventSubscriber<T> : IEventSubscriber where T : class, IDomainEvent
{
    void OnNext(T domainEvent);
}

/// <summary>
/// Strongly typed asynchronous subscriber to a domain event.
/// </summary>
/// <typeparam name="T">A class implementing <see cref="IDomainEvent"/></typeparam>
public interface IAsyncEventSubscriber<T> : IEventSubscriber where T : class, IDomainEvent
{
    Task OnNextAsync(T domainEvent, CancellationToken cancellationToken = default);
}
