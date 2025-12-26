namespace Yaref92.Events.Caching;

public sealed record StoredOutboxEntry(Guid Id, string Payload);
