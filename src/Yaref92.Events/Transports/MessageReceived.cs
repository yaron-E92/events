using System;

using Yaref92.Events;

namespace Yaref92.Events.Transports;

/// <summary>
/// Domain event emitted when a serialized payload is received from a remote session.
/// </summary>
public sealed class MessageReceived : DomainEventBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageReceived"/> class.
    /// </summary>
    /// <param name="sessionKey">The unique key identifying the remote session.</param>
    /// <param name="payload">The serialized payload received from the remote session.</param>
    /// <param name="dateTimeOccurredUtc">Optional UTC timestamp representing when the event occurred.</param>
    /// <param name="eventId">Optional identifier for the event.</param>
    public MessageReceived(string sessionKey, string payload, DateTime dateTimeOccurredUtc = default, Guid eventId = default)
        : base(dateTimeOccurredUtc, eventId)
    {
        SessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <summary>
    /// Gets the unique key that identifies the remote session from which the payload was received.
    /// </summary>
    public string SessionKey { get; }

    /// <summary>
    /// Gets the serialized payload that was received from the remote session.
    /// </summary>
    public string Payload { get; }
}
