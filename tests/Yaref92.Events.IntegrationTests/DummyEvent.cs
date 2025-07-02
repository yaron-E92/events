using Yaref92.Events.Abstractions;

namespace Yaref92.Events.IntegrationTests;

/// <summary>
/// This event is only a dummy to be used in tests
/// </summary>
public class DummyEvent : IDomainEvent
{
    public DateTime DateTimeOccurredUtc => DateTime.UtcNow;
}
