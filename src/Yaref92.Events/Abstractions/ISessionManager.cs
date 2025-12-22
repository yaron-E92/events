using System.Net;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

public interface ISessionManager
{
    ResilientSessionOptions Options { get; }
    IEnumerable<IResilientPeerSession> AuthenticatedSessions { get; }
    IEnumerable<IResilientPeerSession> ValidAnonymousSessions { get; }

    IResilientPeerSession GetOrGenerate(SessionKey sessionKey, bool isAnonymous = false);
    void HydrateAnonymousSessionId(SessionKey sessionKey, EndPoint? remoteEndPoint);
}
