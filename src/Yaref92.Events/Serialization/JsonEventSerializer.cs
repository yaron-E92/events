using System;
using System.Text.Json;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports;

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
        Guid eventId = eventEnvelope.EventId;
        if ((eventEnvelope?.TypeName) == null)
        {
            return (null, null);
        }

        var type = Type.GetType(eventEnvelope.TypeName);
        var domainEvent = JsonSerializer.Deserialize(eventEnvelope?.EventJson!, type!, _options) as IDomainEvent;

        if (domainEvent is IDomainEvent evt && evt.EventId == Guid.Empty && eventId != Guid.Empty)
        {
            // If the payload did not preserve the event ID, attempt to hydrate it via reflection when possible.
            TryAssignEventId(evt, eventId);
        }

        return (type, domainEvent);
    }

    private static void TryAssignEventId(IDomainEvent domainEvent, Guid eventId)
    {
        var eventIdProperty = domainEvent.GetType().GetProperty(nameof(IDomainEvent.EventId));
        if (eventIdProperty?.CanWrite == true && eventIdProperty.PropertyType == typeof(Guid))
        {
            eventIdProperty.SetValue(domainEvent, eventId);
            return;
        }

        var field = domainEvent.GetType().GetField("<EventId>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(domainEvent, eventId);
    }
}
