using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.Transport.Tcp;

public class TcpSessionManager(int listenPort, ResilientSessionOptions options) : SessionManager(listenPort, options)
{
    private readonly object _retentionSync = new();

    public override IResilientPeerSession GetOrGenerate(SessionKey sessionKey, bool isAnonymous = false)
    {
        if (_sessions.TryGetValue(sessionKey, out IResilientPeerSession? existing))
        {
            return existing;
        }

        lock (_retentionSync)
        {
            if (_sessions.TryGetValue(sessionKey, out existing))
            {
                return existing;
            }

            if (_sessions.Count >= _options.MaxRetainedSessions)
            {
                throw new SessionCapacityExceededException(_options.MaxRetainedSessions);
            }

            var created = new ResilientTcpPeerSession(sessionKey, _options)
            {
                IsAnonymous = isAnonymous,
            };
            _sessions[sessionKey] = created;
            return created;
        }
    }

    internal bool ContainsSession(SessionKey sessionKey) => _sessions.ContainsKey(sessionKey);
}

internal sealed class SessionCapacityExceededException(int capacity)
    : InvalidOperationException($"The retained session capacity of {capacity} has been reached.");
