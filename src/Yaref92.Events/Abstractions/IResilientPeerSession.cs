using Yaref92.Events.Sessions;

using static Yaref92.Events.Abstractions.IInboundResilientConnection;

namespace Yaref92.Events.Abstractions;

/// <summary>
/// An abstraction representing a "session" against a specific remote endpoint
/// for a specific user (or anonymous).<br/>
/// It has an inbound and outbound resilient connection to that remote endpoint.<br/>
/// It has an in memory buffer for outbound <see cref="SessionFrame"/>s that should
/// be delivered to the remote endpoint.
/// </summary>
/// <remarks>At construction ensures an existing in/outbound connection</remarks>
public interface IResilientPeerSession : IAsyncDisposable
{
    SessionKey Key { get; }

    string AuthToken { get; }

    bool IsAnonymous { get; }

    IOutboundResilientConnection OutboundConnection { get; }

    IInboundResilientConnection InboundConnection { get; }
    bool RemoteEndpointHasAuthenticated { get; }
    SessionOutboundBuffer OutboundBuffer { get; }

    event SessionFrameReceivedHandler? FrameReceived;

    void RegisterAuthentication(); // TODO ensure this method is doing what it should

    /// <summary>
    /// Update the last activity time as response to heartbeat or received frame/ack
    /// </summary>
    void Touch();
}
