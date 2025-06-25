using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Rx.Abstractions;

namespace Yaref92.Events.Rx;

public sealed class RxEventAggregator : EventAggregator, IDisposable
{
    private readonly ISubject<IDomainEvent> _subject;
    private readonly IObservable<IDomainEvent> _eventStream;
    private readonly ConcurrentDictionary<(Type, IRxSubscriber), IDisposable> _rxSubscriptions = new();

    public RxEventAggregator() : base()
    {
        _subject = Subject.Synchronize(new Subject<IDomainEvent>());
        _eventStream = _subject.AsObservable();
    }

    public IObservable<IDomainEvent> EventStream => _eventStream;

    public override void SubscribeToEventType<T>(IEventSubscriber<T> subscriber)
    {
        if (subscriber is IRxSubscriber<T> rxSubscriber)
        {
            SubscribeToEventTypeRx(rxSubscriber);
        }
        else
        {
            base.SubscribeToEventType(subscriber);
        }
    }

    private void SubscribeToEventTypeRx<T>(IRxSubscriber<T> observer) where T : class, IDomainEvent
    {
        var subscription = _eventStream.OfType<T>().Subscribe(observer);
        _rxSubscriptions.TryAdd((typeof(T), observer), subscription);
    }

    public override void UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber)
    {
        
        if (subscriber is IRxSubscriber<T> rxSubscriber)
        {
            UnsubscribeRx(rxSubscriber);
        }
        else
        {
            base.UnsubscribeFromEventType(subscriber);
        }
    }

    private void UnsubscribeRx<T>(IRxSubscriber<T> observer) where T : class, IDomainEvent
    {
        if (_rxSubscriptions.TryRemove((typeof(T), observer), out var disposable))
        {
            disposable.Dispose();
        }
    }

    public override void PublishEvent<T>(T domainEvent)
    {
        ValidateEvent(domainEvent);
        _subject.OnNext(domainEvent);
        base.PublishEvent(domainEvent);
    }

    /// <summary>
    /// Disposes all tracked Rx subscriptions and clears the subscription dictionary.
    /// </summary>
    public void Dispose()
    {
        foreach (var disposable in _rxSubscriptions.Values)
        {
            disposable.Dispose();
        }
        _rxSubscriptions.Clear();
    }
} 
