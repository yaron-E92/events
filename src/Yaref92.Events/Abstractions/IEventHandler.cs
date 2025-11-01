namespace Yaref92.Events.Abstractions;

/// <summary>
/// Marker interface for a general event handler.
/// </summary>
public interface IEventHandler
{
}

/// <summary>
/// Strongly typed synchronous handler for domain events.
/// </summary>
/// <typeparam name="T">A class implementing <see cref="IDomainEvent"/></typeparam>
public interface IEventHandler<in T> : IEventHandler where T : class, IDomainEvent
{
    void OnNext(T domainEvent);
}

/// <summary>
/// Strongly typed asynchronous handler for domain events.
/// </summary>
/// <typeparam name="T">A class implementing <see cref="IDomainEvent"/></typeparam>
public interface IAsyncEventHandler<in T> : IEventHandler where T : class, IDomainEvent
{
    Task OnNextAsync(T domainEvent, CancellationToken cancellationToken = default);
}
