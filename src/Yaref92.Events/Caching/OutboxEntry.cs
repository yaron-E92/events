namespace Yaref92.Events.Caching;

internal sealed class OutboxEntry(Guid messageId, string payload)
{
    public Guid MessageId { get; } = messageId;

    public string Payload { get; } = payload;

    public bool IsQueued { get; internal set; }
}
