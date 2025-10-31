namespace Yaref92.Events.Transports;

public record EventEnvelope(Guid EventId, string? TypeName, string? EventJson);
