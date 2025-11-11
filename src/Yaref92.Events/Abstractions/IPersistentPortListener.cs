using System.Net.Sockets;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface IPersistentPortListener : IAsyncDisposable
{
    /// <summary>
    /// Port number this listener is listening on
    /// </summary>
    int Port { get; }

    /// <summary>
    /// Manages the underlying <see cref="IInboundResilientConnection"/> for the
    /// <see cref="IResilientPeerSession"/>s
    /// </summary>
    IInboundConnectionManager ConnectionManager { get; }

    /// <summary>
    /// Triggered when a session connection is accepted.
    /// Triggers the transport's session connection accepted handling method,
    /// which ensures the publisher sets up its outbound connection for that session.
    /// </summary>
    event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;

    /// <summary>
    /// Trigerred by the <see cref="ConnectionManager"/> when an inbound session connection is dropped.
    /// Triggers the transport's <see cref="IEventTransport.SessionInboundConnectionDroppedHandler"/>,
    /// which handles the dropped connection appropriately using an attempted reconnect by the publisher,
    /// followed by session cleanup if reconnect attempts are exhausted.
    /// </summary>
    event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;
    event Func<SessionKey, SessionFrame, CancellationToken, Task>? FrameReceived;

    /// <summary>
    /// Starts the underlying <see cref="TcpListener"/> and start loops
    /// to accept incoming <see cref="TcpClient"/> connections and monitor
    /// them
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Task representing the starting of the listener and loops</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop and cancel the listener, await the loops to finish and wrap it all up
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Register an incoming connection for the <paramref name="session"/> on the
    /// <see cref="ConnectionManager"/>
    /// </summary>
    /// <param name="session"></param>
    void RegisterIncomingSessionConnection(SessionKey sessionKey) => ConnectionManager.RegisterIncomingSessionConnection(sessionKey);
}
