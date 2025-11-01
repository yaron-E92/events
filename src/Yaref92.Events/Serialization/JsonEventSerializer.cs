using System;
using System.Text.Json;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Serialization;

/// <summary>
/// Serializes and deserializes events using System.Text.Json.
/// </summary>
public class JsonEventSerializer : IEventSerializer
{
    private readonly JsonSerializerOptions _options;

    public JsonEventSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
    }

    public string Serialize<T>(T domainEvent) where T : class, IDomainEvent
        => SerializeToEventEnvelope(domainEvent);

    public (Type? type, IDomainEvent? domainEvent) Deserialize(string data)
        => DeserializeFromEventEnvelope(data);

    private string SerializeToEventEnvelope<T>(T domainEvent) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent, nameof(domainEvent));

        string eventJson = JsonSerializer.Serialize(domainEvent, _options);
        string? typeName = typeof(T).AssemblyQualifiedName;
        Guid eventId = domainEvent.EventId;
        return JsonSerializer.Serialize(new EventEnvelope(eventId, typeName!, eventJson), _options);
    }

    private (Type? type, IDomainEvent? domainEvent) DeserializeFromEventEnvelope(string data)
    {
        EventEnvelope eventEnvelope = JsonSerializer.Deserialize<EventEnvelope>(data, _options)!;
        if (eventEnvelope.TypeName is null)
        {
            return (null, null);
        }

        var type = Type.GetType(eventEnvelope.TypeName);
        var domainEvent = JsonSerializer.Deserialize(eventEnvelope.EventJson!, type!, _options) as IDomainEvent;

        return (type, domainEvent);
    }
}
