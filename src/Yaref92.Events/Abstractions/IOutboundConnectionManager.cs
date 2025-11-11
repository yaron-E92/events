using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface IOutboundConnectionManager : IConnectionManager
{
    Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken);
    Task ConnectAsync(SessionKey sessionKey, CancellationToken cancellationToken);
    /// <summary>
    /// Generates and queues a broadcast of an event session frame to all connected outbound resilient connections
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="eventEnvelopeJson"></param>
    void QueueEventBroadcast(Guid eventId, string eventEnvelopeJson);
    void SendAck(Guid eventId, SessionKey sessionKey);
}
