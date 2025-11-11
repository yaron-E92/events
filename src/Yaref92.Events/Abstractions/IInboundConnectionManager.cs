using System.Net.Sockets;

using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.Abstractions;

internal interface IInboundConnectionManager : IConnectionManager
{
    /// <summary>
    /// An event that is triggered when a domain event frame is received from any of the managed inbound connections.<br/>
    /// This triggers the transport's event handling method.
    /// </summary>
    event Func<IDomainEvent, SessionKey, Task> EventReceived;

    /// <summary>
    /// Handles an incoming transient connection by associating it with the appropriate
    /// session's <see cref="IInboundResilientConnection"/>, if possible.<br/>
    /// The transient connection is expected to send a session initiation auth frame.<br/>
    /// Initiates the session's inbound resilient connection if not already done.<br/>
    /// Also makes sure the receive loop is started on the inbound resilient connection.
    /// </summary>
    /// <param name="incomingTransientConnection"></param>
    /// <param name="serverToken"></param>
    /// <returns>true if connected successfully, false otherwise</returns>
    Task<ConnectionInitializationResult> HandleIncomingTransientConnectionAsync(TcpClient incomingTransientConnection, CancellationToken serverToken);

    /// <summary>
    /// With the help of the SessionManager, ensures the session is managed,
    /// and get or create its <see cref="IInboundResilientConnection"/>
    /// </summary>
    /// <param name="sessionKey"></param>
    void RegisterIncomingSessionConnection(SessionKey sessionKey);
}
