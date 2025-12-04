using System.Text.Json.Serialization;

using Yaref92.Events;

namespace EventMessenger.Events;

public class MessageEvent : DomainEventBase
{
    [JsonConstructor]
    public MessageEvent(string sender, string text, DateTime timestamp, Guid eventId = default)
        : base(timestamp, eventId)
    {
        Sender = sender;
        Text = text;
        Timestamp = timestamp;
    }

    public string Sender { get; }

    public string Text { get; }

    public DateTime Timestamp { get; }
}
