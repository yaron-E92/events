using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Transports.Events;

public abstract class EventReceived(DateTime nowUtc, IDomainEvent innerEvent) : DomainEventBase(nowUtc, innerEvent.EventId)
{
    public IDomainEvent InnerEvent { get; } = innerEvent ?? throw new ArgumentNullException(nameof(innerEvent));
}

public class EventReceived<TEvent>(DateTime nowUtc, TEvent innerEvent) : EventReceived(nowUtc, innerEvent) where TEvent : class, IDomainEvent
{
    public TEvent InnerEvent { get; } = innerEvent ?? throw new ArgumentNullException(nameof(innerEvent));
}
