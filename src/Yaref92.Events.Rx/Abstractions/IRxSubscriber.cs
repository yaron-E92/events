using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Rx.Abstractions;

public interface IRxSubscriber
{
}

/// <summary>
/// Strongly typed synchronous subscriber to a domain event.
/// </summary>
/// <typeparam name="T">A class implementing <see cref="IDomainEvent"/></typeparam>
public interface IRxSubscriber<T> : IRxSubscriber, IEventSubscriber<T>, IObserver<T> where T : class, IDomainEvent
{
}
