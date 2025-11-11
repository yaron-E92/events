using System.Collections.Concurrent;
using System.Net;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports;

public class SessionManager(int listenPort, ResilientSessionOptions options, IEventSerializer serializer, IEventAggregator? localAggregator)
{
    private readonly ResilientSessionOptions _options = options;
    private readonly IEventSerializer _serializer = serializer;
    private readonly IEventAggregator? _localAggregator = localAggregator;
    private readonly ConcurrentDictionary<SessionKey, IResilientPeerSession> _sessions = new();
    private readonly ConcurrentDictionary<DnsEndPoint, Guid> _anonymousSessionIds = new();
    private readonly int _listenerPort = listenPort;

    public IEnumerable<IResilientPeerSession> AuthenticatedSessions => _sessions.Values.Where(static session => session.HasAuthenticated);

    public IEnumerable<IResilientPeerSession> AnonymousSessions => _sessions.Values.Where(session => _anonymousSessionIds.ContainsKey(session.Key.AsEndPoint()));

    public IEnumerable<IOutboundResilientConnection> OutboundConnections => _sessions.Values.Select(session => session.OutboundConnection);

    public IResilientPeerSession GetOrGenerate(SessionKey sessionKey)
    {
        IResilientPeerSession session = _sessions.GetOrAdd(sessionKey, key => new ResilientPeerSession(key, _options, _localAggregator, _serializer));
        return session;
    }

    

    internal SessionKey CreateFallbackSessionKey(EndPoint? endpoint)
    {
        Guid identifier;
        if (endpoint is DnsEndPoint dnsEndPoint)
        {
            identifier = _anonymousSessionIds.GetOrAdd(dnsEndPoint, static _ => Guid.NewGuid());
            return new SessionKey(identifier, dnsEndPoint.Host, dnsEndPoint.Port);
        }

        var fallbackHost = endpoint switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            _ => IPAddress.Any.ToString(),
        };

        var fallbackPort = endpoint switch
        {
            IPEndPoint ip when ip.Port > 0 => ip.Port,
            _ => _listenerPort,
        };

        identifier = _anonymousSessionIds.GetOrAdd(new DnsEndPoint(fallbackHost, fallbackPort), static _ => Guid.NewGuid());
        return new SessionKey(identifier, fallbackHost, fallbackPort);
    }
}
