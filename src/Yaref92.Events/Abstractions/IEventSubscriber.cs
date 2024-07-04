namespace Yaref92.Events.Abstractions;

/// <summary>
/// This interface represents a general subscriber
/// to a domain event, with an <see cref="ISubscription"/>
/// representing the subscription itself.
/// </summary>
/// <remarks>
/// Any actual response to the event will only be in a strongly
/// typed subclass
/// </remarks>
public interface IEventSubscriber
{
    ISubscription Subscription { get; }
}
