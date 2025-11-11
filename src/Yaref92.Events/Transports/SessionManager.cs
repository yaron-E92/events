using System.Collections.Concurrent;
using System.Net;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports;

internal class SessionManager : ISessionManager
{
    private readonly ResilientSessionOptions _options;
    private readonly IEventSerializer _serializer;
    private readonly IEventAggregator? _localAggregator;
    private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<DnsEndPoint, Guid> _anonymousSessionIds = new();
    private readonly int _listenerPort;

    public SessionManager(int listenPort, ResilientSessionOptions options, IEventSerializer serializer, IEventAggregator? localAggregator)
    {
        if (options is null || !options.Validate())
        {
            options = new ResilientSessionOptions();
        }
        _options = options;
        _serializer = serializer;
        _localAggregator = localAggregator;
        _listenerPort = listenPort;
    }

    public IEnumerable<IResilientPeerSession> AuthenticatedSessions => _sessions.Values.Where(static session => session.RemoteEndpointHasAuthenticated);

    public IEnumerable<IResilientPeerSession> ValidAnonymousSessions => _sessions.Values.Where(session => _anonymousSessionIds.ContainsKey(session.Key.AsEndPoint()) && (session.RemoteEndpointHasAuthenticated || !_options.DoAnonymousSessionsRequireAuthentication));

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
        if (sessionKey.IsAnonymousKey)
        {
            HydrateAnonymousSessionId(sessionKey, remoteEndPoint);
        }

        var session = GetOrGenerate(sessionKey, sessionKey.IsAnonymousKey);
        session.RegisterAuthentication();
        return session;
    }

    public IResilientPeerSession GetOrGenerate(SessionKey sessionKey, bool isAnonymous = false)
    {
        IResilientPeerSession session =
            _sessions.GetOrAdd(sessionKey,
                key => new ResilientPeerSession(key, _options, _localAggregator, _serializer)
                {
                    IsAnonymous = isAnonymous,
                });
        return session;
    }

    //internal SessionKey CreateFallbackSessionKey(EndPoint? remoteEndpoint)
    //{
    //    remoteEndpoint ??= new DnsEndPoint(IPAddress.Any.ToString(), _listenerPort);
    //    Guid identifier;
    //    if (remoteEndpoint is DnsEndPoint dnsEndPoint)
    //    {
    //        identifier = _anonymousSessionIds.GetOrAdd(dnsEndPoint, static _ => Guid.NewGuid());
    //        return new SessionKey(identifier, dnsEndPoint.Host, dnsEndPoint.Port);
    //    }

    //    var fallbackHost = FallbackHost(remoteEndpoint);
    //    var fallbackPort = FallbackPort(remoteEndpoint);

    //    identifier = _anonymousSessionIds.GetOrAdd(new DnsEndPoint(fallbackHost, fallbackPort), static _ => Guid.NewGuid());
    //    return new SessionKey(identifier, fallbackHost, fallbackPort);
    //}

    internal void HydrateAnonymousSessionId(SessionKey sessionKey, EndPoint? remoteEndPoint)
    {
        remoteEndPoint ??= new DnsEndPoint(IPAddress.Any.ToString(), _listenerPort);
        Guid identifier;
        if (remoteEndPoint is DnsEndPoint dnsEndPoint)
        {
            identifier = _anonymousSessionIds.GetOrAdd(dnsEndPoint, static _ => Guid.NewGuid());
        }
        else
        {
            string fallbackHost = FallbackHost(remoteEndPoint);
            int fallbackPort = FallbackPort(remoteEndPoint);
            identifier = _anonymousSessionIds.GetOrAdd(new DnsEndPoint(fallbackHost, fallbackPort), static _ => Guid.NewGuid());
        }
        sessionKey.HydrateAnonymouseId(identifier);
    }

    private int FallbackPort(EndPoint remoteEndPoint)
    {
        return remoteEndPoint switch
        {
            IPEndPoint ip when ip.Port > 0 => ip.Port,
            _ => _listenerPort,
        };
    }

    private static string FallbackHost(EndPoint remoteEndPoint)
    {
        return remoteEndPoint switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            _ => IPAddress.Any.ToString(),
        };
    }

    internal void TouchSession(SessionKey sessionKey)
    {
        IResilientPeerSession session = GetOrGenerate(sessionKey, sessionKey.IsAnonymousKey);
        session.Touch();
    }
}
