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
    /// The monitor loop triggers this event when an inbound session connection is stale.<br/>
    /// This triggers the transport's <see cref="IEventTransport.SessionInboundConnectionDroppedHandler"/>
    /// (through the listener), which handles the dropped connection appropriately using an attempted reconnect by the publisher,
    /// </summary>
    event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;

    /// <summary>
    /// Triggered when the relevant session's inbound connection receives an Ack
    /// for the event id Guid.<br/>
    /// This triggers the Transport's event handler which triggers the publisher's
    /// outbound connection manager Ack received handler method
    /// </summary>
    event Func<Guid, SessionKey, Task>? AckReceived;
    /// <summary>
    /// Triggered when the relevant session's inbound connection receives a Ping
    /// Triggers the transport to send a pong back
    /// </summary>
    event Func<SessionKey, Task>? PingReceived;

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
}
