using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Tcp;
using Yaref92.Events.Transport.Tcp.Abstractions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class SessionManagerTests
{
    [Test]
    public void ResolveSession_NormalizesRemoteHostAndCallbackPort()
    {
        const int advertisedCallbackPort = 62000;
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            DoAnonymousSessionsRequireAuthentication = false,
            CallbackPort = advertisedCallbackPort,
        };

        var sessionManager = new TcpSessionManager(listenPort: 5050, options);
        var originalKey = new SessionKey(Guid.NewGuid(), "listener-host", 5050);
        var sessionToken = SessionFrameContract.CreateSessionToken(originalKey, options, authenticationSecret: null);
        var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);
        var remoteEndPoint = new DnsEndPoint("peer.example.net", 62001);

        var session = sessionManager.ResolveSession(remoteEndPoint, authFrame);

        session.Key.Host.Should().Be(remoteEndPoint.Host);
        session.Key.Port.Should().Be(advertisedCallbackPort);
    }

    [Test]
    public void ResolveSession_ReusesAnonymousIdentifiersPerHost()
    {
        const int callbackPort = 65000;
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            DoAnonymousSessionsRequireAuthentication = false,
            CallbackPort = callbackPort,
        };

        var sessionManager = new TcpSessionManager(listenPort: 5050, options);
        var anonymousKey = new SessionKey(Guid.Empty, "ignored-host", callbackPort)
        {
            IsAnonymousKey = true,
        };
        var sessionToken = SessionFrameContract.CreateSessionToken(anonymousKey, options, authenticationSecret: null);
        var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);

        var firstRemote = new DnsEndPoint("Peer.Example.Net", 62001);
        var secondRemote = new DnsEndPoint("peer.example.net", 62050);

        var firstSession = sessionManager.ResolveSession(firstRemote, authFrame);
        var secondSession = sessionManager.ResolveSession(secondRemote, authFrame);

        firstSession.Key.UserId.Should().NotBe(Guid.Empty);
        secondSession.Key.UserId.Should().Be(firstSession.Key.UserId);
        firstSession.Key.Port.Should().Be(callbackPort);
        secondSession.Key.Port.Should().Be(callbackPort);
    }

    [Test]
    public void HydrateAnonymousSessionId_ReusesSessionForSameHostWithDifferentRemotePort()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            DoAnonymousSessionsRequireAuthentication = false,
        };
        var sessionManager = new TcpSessionManager(listenPort: 5050, options);
        var remoteHost = IPAddress.Parse("203.0.113.10").ToString();
        const int clientListenerPort = 62000;
        var initialKey = new SessionKey(Guid.Empty, remoteHost, clientListenerPort)
        {
            IsAnonymousKey = true,
        };

        sessionManager.HydrateAnonymousSessionId(initialKey, new IPEndPoint(IPAddress.Parse(remoteHost), 41000));
        var firstSession = sessionManager.GetOrGenerate(initialKey, isAnonymous: true);

        var ackedEventId = Guid.NewGuid();
        firstSession.OutboundBuffer.EnqueueFrame(SessionFrame.CreateEventFrame(Guid.NewGuid(), "{}"));
        (firstSession as IResilientTcpSession).OutboundConnection.AcknowledgedEventIds[ackedEventId] = AcknowledgementState.Acknowledged;

        var reconnectKey = new SessionKey(Guid.Empty, remoteHost, clientListenerPort)
        {
            IsAnonymousKey = true,
        };

        sessionManager.HydrateAnonymousSessionId(reconnectKey, new IPEndPoint(IPAddress.Parse(remoteHost), 42000));

        reconnectKey.UserId.Should().Be(initialKey.UserId);

        var secondSession = sessionManager.GetOrGenerate(reconnectKey, isAnonymous: true);
        secondSession.Should().BeSameAs(firstSession);
        secondSession.OutboundBuffer.Should().BeSameAs(firstSession.OutboundBuffer);
        (secondSession as IResilientTcpSession).OutboundConnection.AcknowledgedEventIds.Should().ContainKey(ackedEventId);
    }
}
