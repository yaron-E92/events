namespace Yaref92.Events.Abstractions;

/// <summary>
/// Represents a domain event with a UTC timestamp.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// The UTC date and time when the event occurred.
    /// </summary>
    DateTime DateTimeOccurredUtc { get; }
}
