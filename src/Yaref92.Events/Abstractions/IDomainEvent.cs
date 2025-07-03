namespace Yaref92.Events.Abstractions;

/// <summary>
/// Represents a domain event with a unique identifier and a UTC timestamp.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// The unique identifier for this event instance.
    /// </summary>
    Guid EventId { get; }

    /// <summary>
    /// The UTC date and time when the event occurred.
    /// </summary>
    DateTime DateTimeOccurredUtc { get; }
}
