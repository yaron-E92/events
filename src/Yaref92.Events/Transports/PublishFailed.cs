using System;
using System.Net;

using Yaref92.Events;

namespace Yaref92.Events.Transports;

/// <summary>
/// Event emitted when a TCP publish operation fails for a specific remote endpoint.
/// </summary>
public sealed class PublishFailed : DomainEventBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PublishFailed"/> class.
    /// </summary>
    /// <param name="endpoint">The remote endpoint that failed to receive the event.</param>
    /// <param name="exception">The exception describing the failure.</param>
    /// <param name="dateTimeOccurredUtc">Optional UTC timestamp for when the failure occurred.</param>
    /// <param name="eventId">Optional identifier for the event.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exception"/> is null.</exception>
    public PublishFailed(EndPoint? endpoint, Exception exception, DateTime dateTimeOccurredUtc = default, Guid eventId = default)
        : base(dateTimeOccurredUtc, eventId)
    {
        Endpoint = endpoint;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>
    /// Gets the remote endpoint that failed to receive the published event.
    /// </summary>
    public EndPoint? Endpoint { get; }

    /// <summary>
    /// Gets the exception describing the publish failure.
    /// </summary>
    public Exception Exception { get; }
}
