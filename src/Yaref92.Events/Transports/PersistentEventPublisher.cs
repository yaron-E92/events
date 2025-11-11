using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.Transports;

internal class PersistentEventPublisher(SessionManager sessionManager) : IPersistentFramePublisher
{
    public IOutboundConnectionManager ConnectionManager { get; } = new OutboundConnectionManager(sessionManager);

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public Task PublishToAllAsync(Guid eventId, string eventEnvelopePayload, CancellationToken cancellationToken)
    {
        return Task.Run(() => ConnectionManager.QueueEventBroadcast(eventId, eventEnvelopePayload), cancellationToken);
    }

    /// <summary>
    /// Using the ConnectionManager, send an Ack for the given eventId
    /// using the correct outbound connection.
    /// </summary>
    /// <param name="eventId">Id of the event that the transport received successfully</param>
    /// <param name="sessionKey">Key for the session representing the remote receiver of the ack</param>
    public void AcknowledgeEventReceipt(Guid eventId, SessionKey sessionKey)
    {
        ConnectionManager.SendAck(eventId, sessionKey);
    }
}
