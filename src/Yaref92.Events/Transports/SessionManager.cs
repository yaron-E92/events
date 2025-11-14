using System.Collections.Concurrent;
using System.Net;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports;

internal class SessionManager : ISessionManager
{
    private readonly ResilientSessionOptions _options;
    private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _anonymousSessionIds = new();
    private readonly int _listenerPort;

    public SessionManager(int listenPort, ResilientSessionOptions options)
    {
        if (options is null || !options.Validate())
        {
            options = new ResilientSessionOptions();
        }
        _options = options;
        _listenerPort = listenPort;
    }

    public IEnumerable<IResilientPeerSession> AuthenticatedSessions => _sessions.Values.Where(static session => session.RemoteEndpointHasAuthenticated);

    public IEnumerable<IResilientPeerSession> ValidAnonymousSessions =>
        _sessions.Values.Where(session =>
            _anonymousSessionIds.ContainsKey(ResolveAnonymousIdentityKey(session.Key))
            && (session.RemoteEndpointHasAuthenticated || !_options.DoAnonymousSessionsRequireAuthentication));

    public ResilientSessionOptions Options => _options;

    /// <summary>
    /// Using the authentication frame, resolve the session or throw if authentication fails.
    /// Then return the resolved or created session.
    /// </summary>
    /// <param name="remoteEndPoint">Endpoint representing the remote host&port</param>
    /// <param name="authFrame">
    /// A <see cref="SessionFrame"/> containing the auth secret.
    /// In no-auth scenarios, this frame may contain a null payload.
    /// </param>
    /// <returns></returns>
    /// <exception cref="System.Security.Authentication.AuthenticationException"></exception>
    /// <remarks>The Session will be generated if necessary</remarks>
    internal IResilientPeerSession ResolveSession(EndPoint? remoteEndPoint, SessionFrame authFrame)
    {
        if (!SessionFrameContract.TryValidateAuthentication(authFrame, _options, out SessionKey? sessionKey))
        {
            throw new System.Security.Authentication.AuthenticationException("Failed to validate session authentication frame.");
        }
        sessionKey = NormalizeSessionKeyWithRemoteEndpoint(sessionKey, remoteEndPoint);

        if (sessionKey.IsAnonymousKey)
        {
            HydrateAnonymousSessionId(sessionKey, remoteEndPoint);
        }

        var session = GetOrGenerate(sessionKey, sessionKey.IsAnonymousKey);
        session.RegisterAuthentication();
        session.Touch();
        return session;
    }

    public IResilientPeerSession GetOrGenerate(SessionKey sessionKey, bool isAnonymous = false)
    {
        IResilientPeerSession session =
            _sessions.GetOrAdd(sessionKey,
                key => new ResilientPeerSession(key, _options)
                {
                    IsAnonymous = isAnonymous,
                });
        return session;
    }

    internal void HydrateAnonymousSessionId(SessionKey sessionKey, EndPoint? remoteEndPoint)
    {
        remoteEndPoint ??= new DnsEndPoint(IPAddress.Any.ToString(), _listenerPort);
        string identityKey = ResolveAnonymousIdentityKey(remoteEndPoint);
        Guid identifier = _anonymousSessionIds.GetOrAdd(identityKey, static _ => Guid.NewGuid());
        sessionKey.HydrateAnonymouseId(identifier);
    }

    private static string FallbackHost(EndPoint remoteEndPoint)
    {
        return remoteEndPoint switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            _ => IPAddress.Any.ToString(),
        };
    }

    private static string ResolveAnonymousIdentityKey(EndPoint remoteEndPoint)
    {
        if (remoteEndPoint is DnsEndPoint dnsEndPoint)
        {
            return NormalizeHostKey(dnsEndPoint.Host);
        }

        string fallbackHost = FallbackHost(remoteEndPoint);
        return NormalizeHostKey(fallbackHost);
    }

    private static string ResolveAnonymousIdentityKey(SessionKey sessionKey)
    {
        return NormalizeHostKey(sessionKey.Host);
    }

    private static string NormalizeHostKey(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return IPAddress.Any.ToString();
        }

        return host.ToLowerInvariant();
    }

    internal void TouchSession(SessionKey sessionKey)
    {
        IResilientPeerSession session = GetOrGenerate(sessionKey, sessionKey.IsAnonymousKey);
        session.Touch();
    }

    private SessionKey NormalizeSessionKeyWithRemoteEndpoint(SessionKey sessionKey, EndPoint? remoteEndPoint)
    {
        if (remoteEndPoint is null)
        {
            return sessionKey;
        }

        (string host, int port) = remoteEndPoint switch
        {
            DnsEndPoint dnsEndPoint => (dnsEndPoint.Host, dnsEndPoint.Port),
            IPEndPoint ipEndPoint => (ipEndPoint.Address.ToString(), ipEndPoint.Port),
            _ => (sessionKey.Host, sessionKey.Port),
        };

        if (string.Equals(sessionKey.Host, host, StringComparison.OrdinalIgnoreCase)
            && sessionKey.Port == port)
        {
            return sessionKey;
        }

        return new SessionKey(sessionKey.UserId, host, port)
        {
            IsAnonymousKey = sessionKey.IsAnonymousKey,
        };
    }
}
