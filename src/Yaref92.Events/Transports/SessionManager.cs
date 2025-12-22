using System.Collections.Concurrent;
using System.Net;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports;

public class SessionManager : ISessionManager
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

        if (_options.CallbackPort <= 0)
        {
            _options.CallbackPort = listenPort;
        }

        if (string.IsNullOrWhiteSpace(_options.CallbackHost))
        {
            _options.CallbackHost = Dns.GetHostName();
        }
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
        if (!SessionFrameContract.TryValidateAuthentication(
                authFrame,
                _options,
                out SessionKey? sessionKey,
                out SessionAuthenticationPayload? authenticationPayload))
        {
            throw new System.Security.Authentication.AuthenticationException("Failed to validate session authentication frame.");
        }
        sessionKey = NormalizeSessionKeyWithRemoteEndpoint(sessionKey, remoteEndPoint, authenticationPayload?.CallbackPort);

        if (sessionKey.IsAnonymousKey)
        {
            HydrateAnonymousSessionId(sessionKey, remoteEndPoint);
        }

        var session = GetOrGenerate(sessionKey, sessionKey.IsAnonymousKey);
        session.RegisterAuthentication();
        session.Touch();
        return session;
    }

    internal IResilientPeerSession ResolveFallbackSession(EndPoint? remoteEndPoint)
    {
        SessionKey sessionKey = CreateFallbackSessionKey(remoteEndPoint);
        HydrateAnonymousSessionId(sessionKey, remoteEndPoint);

        var session = GetOrGenerate(sessionKey, isAnonymous: true);
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

    public void HydrateAnonymousSessionId(SessionKey sessionKey, EndPoint? remoteEndPoint)
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
            IPEndPoint ip => FormatIpAddress(ip.Address),
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

    private SessionKey NormalizeSessionKeyWithRemoteEndpoint(SessionKey sessionKey, EndPoint? remoteEndPoint, int? advertisedCallbackPort)
    {
        if (remoteEndPoint is null)
        {
            return ApplyAdvertisedPort(sessionKey, advertisedCallbackPort);
        }

        (string host, int port) = remoteEndPoint switch
        {
            DnsEndPoint dnsEndPoint => (dnsEndPoint.Host, dnsEndPoint.Port),
            IPEndPoint ipEndPoint => (FormatIpAddress(ipEndPoint.Address), ipEndPoint.Port),
            _ => (sessionKey.Host, sessionKey.Port),
        };

        if (string.Equals(sessionKey.Host, host, StringComparison.OrdinalIgnoreCase)
            && sessionKey.Port == port)
        {
            return ApplyAdvertisedPort(sessionKey, advertisedCallbackPort);
        }

        var normalizedKey = new SessionKey(sessionKey.UserId, host, port)
        {
            IsAnonymousKey = sessionKey.IsAnonymousKey,
        };
        return ApplyAdvertisedPort(normalizedKey, advertisedCallbackPort);
    }

    private static SessionKey ApplyAdvertisedPort(SessionKey sessionKey, int? advertisedCallbackPort)
    {
        if (advertisedCallbackPort is not int callbackPort || callbackPort <= 0 || sessionKey.Port == callbackPort)
        {
            return sessionKey;
        }

        return new SessionKey(sessionKey.UserId, sessionKey.Host, callbackPort)
        {
            IsAnonymousKey = sessionKey.IsAnonymousKey,
        };
    }

    private SessionKey CreateFallbackSessionKey(EndPoint? remoteEndPoint)
    {
        if (remoteEndPoint is DnsEndPoint dns)
        {
            return new SessionKey(Guid.Empty, dns.Host, _listenerPort)
            {
                IsAnonymousKey = true,
            };
        }

        if (remoteEndPoint is IPEndPoint ip)
        {
            return new SessionKey(Guid.Empty, FormatIpAddress(ip.Address), _listenerPort)
            {
                IsAnonymousKey = true,
            };
        }

        var fallbackEndpoint = remoteEndPoint ?? new DnsEndPoint(IPAddress.Any.ToString(), _listenerPort);
        return new SessionKey(Guid.Empty, FallbackHost(fallbackEndpoint), _listenerPort)
        {
            IsAnonymousKey = true,
        };
    }

    private static string FormatIpAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        return address.ToString();
    }
}
