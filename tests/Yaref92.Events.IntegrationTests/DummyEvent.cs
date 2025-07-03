namespace Yaref92.Events.IntegrationTests;

/// <summary>
/// This event is only a dummy to be used in tests
/// </summary>
public class DummyEvent : DomainEventBase
{
    public string? Text { get; }

    public DummyEvent(DateTime dateTimeOccurredUtc = default, string? text = null) : base(dateTimeOccurredUtc)
    {
        Text = text;
    }
}
