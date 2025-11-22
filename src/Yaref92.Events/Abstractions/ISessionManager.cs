using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface ISessionManager
{
    ResilientSessionOptions Options { get; }
    IEnumerable<IResilientPeerSession> AuthenticatedSessions { get; }
    IEnumerable<IResilientPeerSession> ValidAnonymousSessions { get; }
}
