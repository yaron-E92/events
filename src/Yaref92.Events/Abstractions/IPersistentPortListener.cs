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

    event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;
    event Func<SessionKey, CancellationToken, Task>? SessionConnectionRemoved;
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
