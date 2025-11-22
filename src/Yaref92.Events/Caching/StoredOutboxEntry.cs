namespace Yaref92.Events.Caching;

internal sealed record StoredOutboxEntry(Guid Id, string Payload);
