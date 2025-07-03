using Yaref92.Events.Abstractions;

namespace Yaref92.Events.UnitTests;

/// <summary>
/// This event is only a dummy to be used in tests
/// </summary>
public class DummyEvent : IDomainEvent
{
    public DateTime DateTimeOccurredUtc { get; }

    public string? Text { get; }

    public DummyEvent(DateTime dateTimeOccurredUtc = default, string? text = null)
    {
        DateTimeOccurredUtc = dateTimeOccurredUtc;
        Text = text;
    }
}
