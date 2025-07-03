namespace Yaref92.Events.UnitTests;

/// <summary>
/// This event is only a dummy to be used in tests
/// </summary>
public class DummyEvent : DomainEventBase
{
    public string? Text { get; }

    public DummyEvent(string? text = null, DateTime dateTimeOccurredUtc = default, Guid eventId = default)
        : base(dateTimeOccurredUtc, eventId)
    {
        Text = text;
    }
}
