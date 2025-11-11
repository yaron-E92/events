using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.Transports;

internal class TempPublisher(SessionManager sessionManager, IEventSerializer eventSerializer) : IPersistentFramePublisher
{
    public IOutboundConnectionManager ConnectionManager { get; } = new OutboundConnectionManager(sessionManager);

    public IEventSerializer EventSerializer { get; } = eventSerializer;

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public Task PublishToAllAsync(Guid eventId, string eventEnvelopePayload, CancellationToken cancellationToken)
    {
        ConnectionManager.QueueEventBroadcast(eventId, eventEnvelopePayload);
        throw new NotImplementedException();
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
