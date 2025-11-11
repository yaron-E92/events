using System.Net.Sockets;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface IInboundResilientConnection : IResilientConnection
{
    /// <summary>
    /// Triggered when a frame is received from the remote endpoint through a transient connection
    /// Triggers the session's frame handling method.
    /// </summary>
    event SessionFrameReceivedHandler? FrameReceived;

    public delegate Task SessionFrameReceivedHandler(SessionFrame frame, SessionKey sessionKey, CancellationToken cancellationToken);

    /// <summary>
    /// Represents whether the connection has passed its timeout threshold.
    /// It checks it by looking at the last activity time and comparing it to the timeout duration.
    /// </summary>
    bool IsPastTimeout { get; }

    /// <summary>
    /// Loops until the inbound connection received an ack for this event.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken">A cancellation token to cancel waiting for ack</param>
    /// <returns>
    /// Task for the acknowledgement state for this event: Either none yet,
    /// sending failed or acknowledged
    /// </returns>
    Task<AcknowledgementState> WaitForAck(Guid eventId, CancellationToken cancellationToken);

    Task InitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cancels any existing incoming connection and attaches the new transient connection for receiving frames.
    /// It is done in a thread-safe manner.
    /// </summary>
    /// <param name="transientConnection"></param>
    /// <param name="incomingConnectionCts"></param>
    /// <remarks>
    /// The resilient connection has a receive loop that waits for transient connections to be attached.
    /// When a new transient connection is attached, the receive loop starts processing frames from it.
    /// </remarks>
    Task AttachTransientConnection(TcpClient transientConnection, CancellationTokenSource incomingConnectionCts);
    void RecordRemoteActivity();
}
