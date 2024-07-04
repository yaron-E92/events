using System.Reactive.Linq;
using System.Reactive.Subjects;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events;

public class EventAggregator : IEventAggregator
{

    public ISet<Type> EventTypes { get; private set; }

    private readonly IObservable<IDomainEvent> _eventStream;
    private readonly Subject<IDomainEvent> _subject;

    public EventAggregator()
    {
        EventTypes = new HashSet<Type>();
        _subject = new Subject<IDomainEvent>();
        _eventStream = _subject.AsObservable();
    }

    void IEventAggregator.RegisterEventType<T>()
    {
        if (!EventTypes.Add(typeof(T)))
        {
            //TODO: When logging exists log the duplication of registration
            return;
        }
    }

    void IEventAggregator.PublishEvent<T>(T domainEvent)
    {
        if (!EventTypes.Contains(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }

        _subject.OnNext(domainEvent);
    }

    void IEventAggregator.SubscribeToEventType<T>(IEventSubscriber<T> subscriber)
    {
        if (!EventTypes.Contains(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }

        subscriber.Subscription.AddSubscription(_eventStream.OfType<T>()
            .Subscribe(subscriber));
    }

    void IEventAggregator.UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber)
    {
        if (!EventTypes.Contains(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }

        subscriber.Subscription.Dispose();
    }
}
