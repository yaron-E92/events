namespace Yaref92.Events.Abstractions;

/// <summary>
/// Defines a contract for serializing and deserializing domain events for network transport.
/// </summary>
public interface IEventSerializer
{
    /// <summary>
    /// Serializes an event to a string for transmission.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="evt">The event instance to serialize.</param>
    /// <returns>A string representation of the event.</returns>
    string Serialize<T>(T evt) where T : class, IDomainEvent;

    /// <summary>
    /// Deserializes an event from a string, returning the event type and instance.
    /// </summary>
    /// <param name="data">The serialized event data.</param>
    /// <returns>The deserialized event type and instance.</returns>
    (Type? type, IDomainEvent? domainEvent) Deserialize(string data);
} 
