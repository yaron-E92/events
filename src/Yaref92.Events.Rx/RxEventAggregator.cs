using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Yaref92.Events;
using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Rx;

public class RxEventAggregator : EventAggregator
{
    private readonly ISubject<IDomainEvent> _subject;
    private readonly IObservable<IDomainEvent> _eventStream;

    public RxEventAggregator() : base()
    {
        _subject = Subject.Synchronize(new Subject<IDomainEvent>());
        _eventStream = _subject.AsObservable();
    }

    public IObservable<IDomainEvent> EventStream => _eventStream;

    public IDisposable SubscribeToEventTypeRx<T>(IObserver<T> observer) where T : class, IDomainEvent
    {
        return _eventStream.OfType<T>().Subscribe(observer);
    }

    public void PublishEventRx<T>(T domainEvent) where T : class, IDomainEvent
    {
        _subject.OnNext(domainEvent);
    }
} 