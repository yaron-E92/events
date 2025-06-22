namespace Yaref92.Events.Abstractions;

public interface IEventAggregator
{
    ISet<Type> EventTypes { get; }

    bool RegisterEventType<T>() where T : class, IDomainEvent;

    void PublishEvent<T>(T domainEvent) where T : class, IDomainEvent;

    void SubscribeToEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent;

    void UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent;
}
