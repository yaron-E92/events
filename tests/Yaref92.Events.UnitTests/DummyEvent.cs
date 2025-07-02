using Yaref92.Events.Abstractions;
using System;

namespace Yaref92.Events.UnitTests;

/// <summary>
/// This event is only a dummy to be used in tests
/// </summary>
public class DummyEvent : IDomainEvent
{
    public DateTime DateTimeOccurredUtc => DateTime.UtcNow;
}
