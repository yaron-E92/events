using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Tcp.Abstractions;

internal interface IPersistentFramePublisher : IAsyncDisposable
{
    /// <summary>
    /// Manages the underlying <see cref="IInboundResilientConnection"/> for the
    /// <see cref="IResilientPeerSession"/>s
    /// </summary>
    IOutboundConnectionManager ConnectionManager { get; }

    /// <summary>
    /// Send Ack to the other remote endpoint of the session represented
    /// by the key.
    /// </summary>
    /// <param name="eventId">Id of the received event to acknowledge</param>
    /// <param name="sessionKey">Key of the session representing the connection from which the event came from</param>
    internal void AcknowledgeEventReceipt(Guid eventId, SessionKey sessionKey);

    Task PublishToAllAsync(Guid eventId, string eventEnvelopePayload, CancellationToken cancellationToken);
}
