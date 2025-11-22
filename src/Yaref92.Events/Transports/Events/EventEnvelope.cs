namespace Yaref92.Events.Transports.Events;

public record EventEnvelope(Guid EventId, string? TypeName, string? EventJson);
