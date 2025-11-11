using System.Net.Sockets;

using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.Abstractions;

internal interface IInboundResilientConnection : IResilientConnection
{
    /// <summary>
    /// Triggered when a frame is received from the remote endpoint through a transient connection
    /// Triggers the session's frame handling method.
    /// </summary>
    event SessionFrameReceivedHandler? FrameReceived;

    public delegate Task SessionFrameReceivedHandler(SessionFrame frame, SessionKey sessionKey, CancellationToken cancellationToken);

    Task<AcknowledgementState> WaitForAck(Guid eventId, CancellationToken cancellationToken);

    Task InitAsync(CancellationToken cancellationToken);

    Task AttachTransientConnection(TcpClient transientConnection, CancellationTokenSource incomingConnectionCts);
}
