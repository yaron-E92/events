namespace Yaref92.Events.Abstractions;

/// <summary>
/// This marker interface represents a general subscriber
/// to a domain event
/// </summary>
public interface IEventSubscriber
{
}

/// <summary>
/// This is a strongly typed subscriber
/// to a domain eventextending interface that
/// also defines an OnEvent void method to respond
/// specifically to an event of type <see cref="T"/>
/// </summary>
/// <typeparam name="T">
/// A class implementing <see cref="IDomainEvent"/>
/// </typeparam>
public interface IEventSubscriber<T> : IEventSubscriber, IObserver<T> where T : class, IDomainEvent
{
}
