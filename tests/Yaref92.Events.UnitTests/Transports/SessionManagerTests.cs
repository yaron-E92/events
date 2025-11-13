using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class SessionManagerTests
{
    [Test]
    public void HydrateAnonymousSessionId_ReusesSessionForSameHostWithDifferentRemotePort()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            DoAnonymousSessionsRequireAuthentication = false,
        };
        var sessionManager = new SessionManager(listenPort: 5050, options);
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
        firstSession.OutboundConnection.AcknowledgedEventIds[ackedEventId] = AcknowledgementState.Acknowledged;

        var reconnectKey = new SessionKey(Guid.Empty, remoteHost, clientListenerPort)
        {
            IsAnonymousKey = true,
        };

        sessionManager.HydrateAnonymousSessionId(reconnectKey, new IPEndPoint(IPAddress.Parse(remoteHost), 42000));

        reconnectKey.UserId.Should().Be(initialKey.UserId);

        var secondSession = sessionManager.GetOrGenerate(reconnectKey, isAnonymous: true);
        secondSession.Should().BeSameAs(firstSession);
        secondSession.OutboundBuffer.Should().BeSameAs(firstSession.OutboundBuffer);
        secondSession.OutboundConnection.AcknowledgedEventIds.Should().ContainKey(ackedEventId);
    }
}
