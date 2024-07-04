namespace Yaref92.Events.Abstractions;

public interface IDomainEvent
{
    DateTime DateTimeOccurredUtc { get; }
}
