using System;

namespace Yaref92.Events.Transports;

/// <summary>
/// Represents the serialized form of a domain event as it travels across transports.
/// </summary>
public record EventEnvelope
{
    /// <summary>
    /// Gets the unique identifier associated with the event.
    /// </summary>
    public Guid EventId { get; init; }

    /// <summary>
    /// Gets the assembly-qualified type name of the event.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Gets the serialized JSON representation of the event instance.
    /// </summary>
    public string? EventJson { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventEnvelope"/> class.
    /// Parameterless constructor required for serialization.
    /// </summary>
    public EventEnvelope()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventEnvelope"/> class with the specified type name and payload.
    /// </summary>
    /// <param name="typeName">The assembly-qualified type name of the event.</param>
    /// <param name="eventJson">The serialized event payload.</param>
    public EventEnvelope(string? typeName, string? eventJson)
        : this(Guid.Empty, typeName, eventJson)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventEnvelope"/> class with the specified event metadata.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="typeName">The assembly-qualified type name of the event.</param>
    /// <param name="eventJson">The serialized event payload.</param>
    public EventEnvelope(Guid eventId, string? typeName, string? eventJson)
    {
        EventId = eventId;
        TypeName = typeName;
        EventJson = eventJson;
    }
}
