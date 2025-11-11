using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface IOutboundConnectionManager : IConnectionManager
{
    //event IOutboundResilientConnection.PublishFailedHandler PublishFailed;

    Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken);
    Task ConnectAsync(SessionKey sessionKey, CancellationToken cancellationToken);
    /// <summary>
    /// Generates and queues a broadcast of an event session frame to all connected outbound resilient connections
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="eventEnvelopeJson"></param>
    void QueueEventBroadcast(Guid eventId, string eventEnvelopeJson);
    Task<bool> TryReconnectAsync(SessionKey sessionKey, CancellationToken token);
    void SendAck(Guid eventId, SessionKey sessionKey);

    /// <summary>
    /// Triggered by the transport after it receieved the event that an ack was received.
    /// Finds the correct session, and triggers its outbound connection's OnAckReceived
    /// handler method
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="sessionKey"></param>
    /// <returns></returns>
    Task OnAckReceived(Guid eventId, SessionKey sessionKey);

    /// <summary>
    /// Triggered by the transport after it is told a ping was received.
    /// Sends a pong back using the <paramref name="sessionKey"/> to identify correct outbound connection
    /// </summary>
    /// <param name="sessionKey"></param>
    /// <returns></returns>
    void SendPong(SessionKey sessionKey);
}
