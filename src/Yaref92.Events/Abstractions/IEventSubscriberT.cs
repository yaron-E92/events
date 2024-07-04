namespace Yaref92.Events.Abstractions;

/// <summary>
/// <inheritdoc/>
///
/// This is a strongly typed extending interface that
/// also defines an OnEvent void method to respond
/// specifically to an event of type <see cref="T"/>
/// </summary>
/// <typeparam name="T">
/// A class implementing <see cref="IDomainEvent"/>
/// </typeparam>
public interface IEventSubscriber<T> : IEventSubscriber, IObserver<T> where T : class, IDomainEvent
{
}
