using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Tcp.ConnectionManagers;
using Yaref92.Events.Transport.Tcp;
using Yaref92.Events.Transport.Tcp.Abstractions;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public sealed class OutboundConnectionManagerTests
{
    [Test]
    public async Task TryReconnectAsync_Completes_after_simulated_drop()
    {
        var options = new ResilientSessionOptions();
        var sessionManager = new TcpSessionManager(0, options);
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 1234);

        var outbound = new StubOutboundConnection(sessionKey);
        outbound.SimulateDrop();

        sessionManager.InjectSession(new StubPeerSession(sessionKey, outbound));

        var manager = new OutboundConnectionManager(sessionManager);

        var reconnectTask = manager.TryReconnectAsync(sessionKey, CancellationToken.None);

        outbound.CompleteReconnect(true);

        var reconnected = await reconnectTask.ConfigureAwait(false);

        reconnected.Should().BeTrue();
        outbound.RefreshAttempts.Should().Be(1);
    }

    [Test]
    public async Task StopAsync_Disposes_anonymous_outbound_connections()
    {
        var options = new ResilientSessionOptions();
        var sessionManager = new TcpSessionManager(0, options);
        var manager = new OutboundConnectionManager(sessionManager);

        var anonymousSessionKey = new SessionKey(Guid.Empty, "localhost", 2345)
        {
            IsAnonymousKey = true,
        };
        sessionManager.HydrateAnonymousSessionId(anonymousSessionKey, new DnsEndPoint("localhost", 2345));

        var outbound = new DisposableStubOutboundConnection(anonymousSessionKey);
        var anonymousSession = new StubPeerSession(anonymousSessionKey, outbound, isAnonymous: true, remoteEndpointHasAuthenticated: false);

        sessionManager.InjectSession(anonymousSession);

        await manager.StopAsync().ConfigureAwait(false);

        outbound.DisposeAsyncCalls.Should().Be(1);
    }

    [Test]
    public void QueueEventBroadcast_EnqueuesFramesOnEachDistinctSession()
    {
        var options = new ResilientSessionOptions();
        var sessionManager = new TcpSessionManager(0, options);
        var firstKey = new SessionKey(Guid.NewGuid(), "host-one", 1111);
        var secondKey = new SessionKey(Guid.NewGuid(), "host-two", 2222);

        var firstSession = new FakeResilientPeerSession(firstKey, new FakeInboundResilientConnection(firstKey), new FakeOutboundResilientConnection(firstKey));
        var secondSession = new FakeResilientPeerSession(secondKey, new FakeInboundResilientConnection(secondKey), new FakeOutboundResilientConnection(secondKey));
        sessionManager.InjectSession(firstSession);
        sessionManager.InjectSession(secondSession);

        var manager = new OutboundConnectionManager(sessionManager);
        var eventId = Guid.NewGuid();
        const string payload = "{\"type\":\"dummy\"}";

        manager.QueueEventBroadcast(eventId, payload);

        var firstOutbound = (FakeOutboundResilientConnection)firstSession.OutboundConnection;
        var secondOutbound = (FakeOutboundResilientConnection)secondSession.OutboundConnection;

        firstOutbound.EnqueuedFrames.Should().ContainSingle(frame => frame.Kind == SessionFrameKind.Event && frame.Id == eventId && frame.Payload == payload);
        secondOutbound.EnqueuedFrames.Should().ContainSingle(frame => frame.Kind == SessionFrameKind.Event && frame.Id == eventId && frame.Payload == payload);
    }

    [Test]
    public void SendAck_UsesSessionManagerToLocateSession()
    {
        var options = new ResilientSessionOptions();
        var sessionManager = new TcpSessionManager(0, options);
        var firstKey = new SessionKey(Guid.NewGuid(), "host-one", 1111);
        var secondKey = new SessionKey(Guid.NewGuid(), "host-two", 2222);

        var firstSession = new FakeResilientPeerSession(firstKey, new FakeInboundResilientConnection(firstKey), new FakeOutboundResilientConnection(firstKey));
        var secondSession = new FakeResilientPeerSession(secondKey, new FakeInboundResilientConnection(secondKey), new FakeOutboundResilientConnection(secondKey));
        sessionManager.InjectSession(firstSession);
        sessionManager.InjectSession(secondSession);

        var manager = new OutboundConnectionManager(sessionManager);
        var ackId = Guid.NewGuid();

        manager.SendAck(ackId, secondKey);

        var firstOutbound = (FakeOutboundResilientConnection)firstSession.OutboundConnection;
        var secondOutbound = (FakeOutboundResilientConnection)secondSession.OutboundConnection;

        firstOutbound.EnqueuedFrames.Should().BeEmpty();
        secondOutbound.EnqueuedFrames.Should().ContainSingle(frame => frame.Kind == SessionFrameKind.Ack && frame.Id == ackId);
    }

    [Test]
    public void SendPong_EnqueuesFrameOnTargetedSession()
    {
        var options = new ResilientSessionOptions();
        var sessionManager = new TcpSessionManager(0, options);
        var firstKey = new SessionKey(Guid.NewGuid(), "host-one", 1111);
        var secondKey = new SessionKey(Guid.NewGuid(), "host-two", 2222);

        var firstSession = new FakeResilientPeerSession(firstKey, new FakeInboundResilientConnection(firstKey), new FakeOutboundResilientConnection(firstKey));
        var secondSession = new FakeResilientPeerSession(secondKey, new FakeInboundResilientConnection(secondKey), new FakeOutboundResilientConnection(secondKey));
        sessionManager.InjectSession(firstSession);
        sessionManager.InjectSession(secondSession);

        var manager = new OutboundConnectionManager(sessionManager);

        manager.SendPong(firstKey);

        var firstOutbound = (FakeOutboundResilientConnection)firstSession.OutboundConnection;
        var secondOutbound = (FakeOutboundResilientConnection)secondSession.OutboundConnection;

        firstOutbound.EnqueuedFrames.Should().ContainSingle(frame => frame.Kind == SessionFrameKind.Pong);
        secondOutbound.EnqueuedFrames.Should().BeEmpty();
    }

    [Test]
    public async Task TryReconnectAsync_UsesSessionSelectedBySessionManager()
    {
        var options = new ResilientSessionOptions();
        var sessionManager = new TcpSessionManager(0, options);
        var firstKey = new SessionKey(Guid.NewGuid(), "host-one", 1111);
        var secondKey = new SessionKey(Guid.NewGuid(), "host-two", 2222);

        var firstOutbound = new FakeOutboundResilientConnection(firstKey)
        {
            RefreshResult = false,
        };
        var secondOutbound = new FakeOutboundResilientConnection(secondKey)
        {
            RefreshResult = true,
        };

        sessionManager.InjectSession(new FakeResilientPeerSession(firstKey, new FakeInboundResilientConnection(firstKey), firstOutbound));
        sessionManager.InjectSession(new FakeResilientPeerSession(secondKey, new FakeInboundResilientConnection(secondKey), secondOutbound));

        var manager = new OutboundConnectionManager(sessionManager);

        bool reconnected = await manager.TryReconnectAsync(secondKey, CancellationToken.None).ConfigureAwait(false);

        reconnected.Should().BeTrue();
        firstOutbound.RefreshRequests.Should().Be(0);
        secondOutbound.RefreshRequests.Should().Be(1);
    }

    private class StubOutboundConnection : IOutboundResilientConnection
    {
        private TaskCompletionSource<bool> _reconnectSource = CreateSource();

        internal int RefreshAttempts { get; private set; }

        internal StubOutboundConnection(SessionKey sessionKey)
        {
            SessionKey = sessionKey;
            OutboundBuffer = new SessionOutboundBuffer();
            AcknowledgedEventIds = new ConcurrentDictionary<Guid, AcknowledgementState>();
        }

        public SessionKey SessionKey { get; }
        public DnsEndPoint RemoteEndPoint => new(SessionKey.Host, SessionKey.Port);
        public string OutboxPath => string.Empty;
        public ConcurrentDictionary<Guid, AcknowledgementState> AcknowledgedEventIds { get; }
        public SessionOutboundBuffer OutboundBuffer { get; }

        public Task DumpBuffer() => Task.CompletedTask;

        public void EnqueueFrame(SessionFrame frame)
        {
        }

        public Task InitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void OnAckReceived(Guid eventId)
        {
        }

        public Task<bool> RefreshConnectionAsync(CancellationToken token)
        {
            RefreshAttempts++;
            return _reconnectSource.Task.WaitAsync(token);
        }

        internal void SimulateDrop()
        {
            _reconnectSource = CreateSource();
        }

        internal void CompleteReconnect(bool succeeded) =>
            _reconnectSource.TrySetResult(succeeded);

        private static TaskCompletionSource<bool> CreateSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DisposableStubOutboundConnection : StubOutboundConnection, IAsyncDisposable
    {
        internal int DisposeAsyncCalls { get; private set; }

        internal DisposableStubOutboundConnection(SessionKey sessionKey)
            : base(sessionKey)
        {
        }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubPeerSession : IResilientTcpSession
    {
        private readonly bool _isAnonymous;
        private readonly bool _remoteEndpointHasAuthenticated;

        internal StubPeerSession(SessionKey key, IOutboundResilientConnection outbound, bool isAnonymous = false, bool remoteEndpointHasAuthenticated = true)
        {
            Key = key;
            OutboundConnection = outbound;
            InboundConnection = new StubInboundConnection(key);
            OutboundBuffer = outbound.OutboundBuffer;
            _isAnonymous = isAnonymous;
            _remoteEndpointHasAuthenticated = remoteEndpointHasAuthenticated;
        }

        public SessionKey Key { get; }
        public string AuthToken => string.Empty;
        public bool IsAnonymous => _isAnonymous;
        public IOutboundResilientConnection OutboundConnection { get; }
        public IInboundResilientConnection InboundConnection { get; }
        public bool RemoteEndpointHasAuthenticated => _remoteEndpointHasAuthenticated;
        public SessionOutboundBuffer OutboundBuffer { get; }

        public event IInboundResilientConnection.SessionFrameReceivedHandler? FrameReceived
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RegisterAuthentication()
        {
        }

        public void Touch()
        {
        }
    }

    private sealed class StubInboundConnection : IInboundResilientConnection
    {
        internal StubInboundConnection(SessionKey key)
        {
            SessionKey = key;
        }

        public SessionKey SessionKey { get; }
        public bool IsPastTimeout => false;

        public event IInboundResilientConnection.SessionFrameReceivedHandler? FrameReceived
        {
            add { }
            remove { }
        }

        public Task<AcknowledgementState> WaitForAck(Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult(AcknowledgementState.None);

        public Task InitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AttachTransientConnection(TcpClient transientConnection, CancellationTokenSource incomingConnectionCts) =>
            Task.CompletedTask;

        public void RecordRemoteActivity()
        {
        }
    }
}
