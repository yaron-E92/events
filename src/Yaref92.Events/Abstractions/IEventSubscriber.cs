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
public interface IEventSubscriber<T> : IEventSubscriber, IObserver<T> where T : class, IDomainEvent
{
}
