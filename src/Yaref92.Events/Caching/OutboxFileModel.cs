namespace Yaref92.Events.Caching;

public sealed class OutboxFileModel
{
    public Dictionary<string, List<StoredOutboxEntry>> Sessions { get; set; } = [];
}
