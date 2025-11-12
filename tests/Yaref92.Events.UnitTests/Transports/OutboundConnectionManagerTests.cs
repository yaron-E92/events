using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public sealed class OutboundConnectionManagerTests
{
    [Test]
    public async Task TryReconnectAsync_Completes_after_simulated_drop()
    {
        var options = new ResilientSessionOptions();
        var sessionManager = new SessionManager(0, options);
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 1234);

        var outbound = new StubOutboundConnection(sessionKey);
        outbound.SimulateDrop();

        InjectSession(sessionManager, new StubPeerSession(sessionKey, outbound));

        var manager = new OutboundConnectionManager(sessionManager);

        var reconnectTask = manager.TryReconnectAsync(sessionKey, CancellationToken.None);

        outbound.CompleteReconnect(true);

        var reconnected = await reconnectTask.ConfigureAwait(false);

        reconnected.Should().BeTrue();
        outbound.RefreshAttempts.Should().Be(1);
    }

    private static void InjectSession(SessionManager sessionManager, IResilientPeerSession session)
    {
        var sessionsField = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance);
        var sessions = (ConcurrentDictionary<SessionKey, IResilientPeerSession>)sessionsField!.GetValue(sessionManager)!;
        sessions[session.Key] = session;
    }

    private sealed class StubOutboundConnection : IOutboundResilientConnection
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

    private sealed class StubPeerSession : IResilientPeerSession
    {
        internal StubPeerSession(SessionKey key, IOutboundResilientConnection outbound)
        {
            Key = key;
            OutboundConnection = outbound;
            InboundConnection = new StubInboundConnection(key);
            OutboundBuffer = outbound.OutboundBuffer;
        }

        public SessionKey Key { get; }
        public string AuthToken => string.Empty;
        public bool IsAnonymous => false;
        public IOutboundResilientConnection OutboundConnection { get; }
        public IInboundResilientConnection InboundConnection { get; }
        public bool RemoteEndpointHasAuthenticated => true;
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
