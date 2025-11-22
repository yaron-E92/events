using System.Text.Json.Serialization;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events;

/// <summary>
/// Provides a base implementation for domain events, ensuring a unique EventId and UTC timestamp.
/// </summary>
public abstract class DomainEventBase : IDomainEvent
{
    /// <inheritdoc/>
    [JsonInclude]
    public Guid EventId { get; private set; }
    /// <inheritdoc/>
    [JsonInclude]
    public DateTime DateTimeOccurredUtc { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainEventBase"/> class.
    /// </summary>
    /// <param name="dateTimeOccurredUtc">The UTC time the event occurred. Defaults to now if not provided.</param>
    /// <param name="eventId">The unique event ID. If not provided or empty, a new Guid is generated.</param>
    [JsonConstructor]
    protected DomainEventBase(DateTime dateTimeOccurredUtc = default, Guid eventId = default)
    {
        EventId = eventId == default ? Guid.NewGuid() : eventId;
        DateTimeOccurredUtc = dateTimeOccurredUtc == default ? DateTime.UtcNow : dateTimeOccurredUtc;
    }
}
