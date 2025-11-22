namespace Yaref92.Events.Caching;

internal sealed class OutboxFileModel
{
    public Dictionary<string, List<StoredOutboxEntry>> Sessions { get; set; } = [];
}
