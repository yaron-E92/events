
using Yaref92.Events.Transports;

namespace Yaref92.Events.Abstractions;

public interface IInboundResilientConnection : IResilientConnection
{
    event ResilientSessionConnection.SessionFrameReceivedHandler? FrameReceived;

    Task<AcknowledgementState> WaitForAck(Guid eventId, CancellationToken cancellationToken);

    Task InitAsync(CancellationToken cancellationToken);
}
