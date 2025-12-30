using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.Transport.Tcp;

public class TcpSessionManager(int listenPort, ResilientSessionOptions options) : SessionManager(listenPort, options)
{
    public override IResilientPeerSession GetOrGenerate(SessionKey sessionKey, bool isAnonymous = false)
    {
        IResilientPeerSession session =
            _sessions.GetOrAdd(sessionKey,
                key => new ResilientTcpPeerSession(key, _options)
                {
                    IsAnonymous = isAnonymous,
                });
        return session;
    }
}
