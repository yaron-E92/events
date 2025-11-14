using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Connections;
using Yaref92.Events.Serialization;
using Yaref92.Events.Sessions;
using Yaref92.Events.Sessions.Events;
using Yaref92.Events.Transports;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.IntegrationTests;

[TestFixture]
[Explicit("Integration tests require local TCP sockets and timing-sensitive coordination.")]
[Category("Integration")]
public class ResilientSessionIntegrationTests
{
    [Test]
    public async Task PersistentClient_Reconnects_And_Replays_Outbox_On_Drop()
    {
        // Arrange
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(80),
            BackoffInitialDelay = TimeSpan.FromMilliseconds(20),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(50),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        var server = serverHost.Server;
        var deliveries = new List<string>();
        var firstDeliveryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayDeliveryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        RegisterServerEventHandler(serverHost, (domainEvent, _) =>
        {
            if (domainEvent is not TestPayloadEvent testEvent)
            {
                return Task.CompletedTask;
            }

            lock (deliveries)
            {
                deliveries.Add(testEvent.Payload);
                if (deliveries.Count == 1)
                {
                    firstDeliveryTcs.TrySetResult();
                }
                else if (deliveries.Count == 2)
                {
                    replayDeliveryTcs.TrySetResult();
                }
            }

            return Task.CompletedTask;
        });

        await using var clientHost = new TestPersistentClientHost("127.0.0.1", port, options);
        clientHost.DropNextAck();
        await clientHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var payload = $"payload-{Guid.NewGuid():N}";
        var domainEvent = new TestPayloadEvent(payload);
        var serializedEvent = serverHost.Serializer.Serialize(domainEvent);
        var messageId = await clientHost.Client.EnqueueEventAsync(serializedEvent, CancellationToken.None).ConfigureAwait(false);
        messageId.Should().Be(domainEvent.EventId);

        await firstDeliveryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await clientHost.WaitForConnectionCountAsync(2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await replayDeliveryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await clientHost.WaitForAckCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await clientHost.WaitForOutboxCountAsync(0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        deliveries.Should().HaveCount(2).And.OnlyContain(d => d == payload);
        clientHost.ConnectionCount.Should().BeGreaterThanOrEqualTo(2);
        clientHost.AcknowledgedMessageIds.Should().ContainSingle(id => id == messageId);
    }

    [Test]
    public async Task PersistentClient_Completes_Handshake_Without_ConnectionEstablished_Handlers()
    {
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(80),
            BackoffInitialDelay = TimeSpan.FromMilliseconds(20),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(50),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        RegisterServerEventHandler(serverHost);

        await using var clientHost = new TestPersistentClientHost("127.0.0.1", port, options, trackConnections: false);

        Func<Task> act = () => clientHost.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync().ConfigureAwait(false);

        await WaitForAuthenticatedSessionsAsync(serverHost.Server, 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [Test]
    public async Task PersistentClient_AbortActiveConnection_CancelsLoops_And_Reconnects()
    {
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(80),
            BackoffInitialDelay = TimeSpan.FromMilliseconds(20),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(50),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        RegisterServerEventHandler(serverHost);
        var server = serverHost.Server;

        await using var clientHost = new TestPersistentClientHost("127.0.0.1", port, options);
        await clientHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

        await clientHost.WaitForConnectionCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForAuthenticatedSessionsAsync(server, 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var outboundConnection = clientHost.Client.OutboundConnection;
        var (sendLoopTask, heartbeatLoopTask) = await WaitForActiveOutboundLoopsAsync(outboundConnection, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await outboundConnection.AbortActiveConnectionAsync().ConfigureAwait(false);
        await Task.WhenAll(sendLoopTask, heartbeatLoopTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        sendLoopTask.IsCompletedSuccessfully.Should().BeTrue();
        heartbeatLoopTask.IsCompletedSuccessfully.Should().BeTrue();

        await clientHost.WaitForConnectionCountAsync(2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var payload = $"abort-{Guid.NewGuid():N}";
        var initialMessageCount = clientHost.ReceivedPayloads.Count;
        var initialAckCount = clientHost.AcknowledgedMessageIds.Count;

        var domainEvent = new TestPayloadEvent(payload);
        var serializedEvent = serverHost.Serializer.Serialize(domainEvent);
        var messageId = await clientHost.Client.EnqueueEventAsync(serializedEvent, CancellationToken.None).ConfigureAwait(false);
        messageId.Should().Be(domainEvent.EventId);

        await clientHost.WaitForMessageCountAsync(initialMessageCount + 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await clientHost.WaitForAckCountAsync(initialAckCount + 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        clientHost.ReceivedPayloads.Should().Contain(payload);
        clientHost.AcknowledgedMessageIds.Should().Contain(messageId);
    }

    [Test]
    [Explicit("Integration tests require local TCP sockets and timing-sensitive coordination.")]
    public async Task Anonymous_And_Authenticated_Sessions_Are_Differentiated()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = true,
            DoAnonymousSessionsRequireAuthentication = false,
            AuthenticationToken = Guid.NewGuid().ToString("N"),
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(80),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        RegisterServerEventHandler(serverHost);
        var server = serverHost.Server;

        await using var authenticated = new TestPersistentClientHost("127.0.0.1", port, options);
        await using var anonymous = new TestPersistentClientHost("127.0.0.1", port, options, sessionUserId: Guid.Empty);

        await authenticated.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await anonymous.StartAsync(CancellationToken.None).ConfigureAwait(false);

        await WaitForAuthenticatedSessionsAsync(server, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var sessions = serverHost.SessionManager.AuthenticatedSessions
            .Concat(serverHost.SessionManager.ValidAnonymousSessions)
            .DistinctBy(session => session.Key)
            .ToArray();
        sessions.Should().HaveCount(2);

        var anonymousSession = sessions.Single(session => session.Key.IsAnonymousKey);
        var authenticatedSession = sessions.Single(session => !session.Key.IsAnonymousKey);

        anonymousSession.RemoteEndpointHasAuthenticated.Should().BeTrue();
        authenticatedSession.RemoteEndpointHasAuthenticated.Should().BeTrue();

        anonymous.SessionKey.UserId.Should().Be(Guid.Empty);
        authenticated.SessionKey.UserId.Should().NotBe(Guid.Empty);

        anonymousSession.Key.UserId.Should().NotBe(Guid.Empty);
        authenticatedSession.Key.UserId.Should().Be(authenticated.SessionKey.UserId);
        anonymousSession.Key.UserId.Should().NotBe(authenticatedSession.Key.UserId);
    }

    [Test]
    public async Task Broadcasts_Are_Redelivered_Until_Acknowledged_By_All_Peers()
    {
        // Arrange
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(80),
            BackoffInitialDelay = TimeSpan.FromMilliseconds(10),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(40),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        RegisterServerEventHandler(serverHost);
        var server = serverHost.Server;

        await using var peerA = new TestPersistentClientHost("127.0.0.1", port, options);
        await using var peerB = new TestPersistentClientHost("127.0.0.1", port, options, dropFirstAck: true);

        await peerA.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await peerB.StartAsync(CancellationToken.None).ConfigureAwait(false);

        await WaitForAuthenticatedSessionsAsync(server, 2, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        var payload = $"broadcast-{Guid.NewGuid():N}";
        var broadcastEvent = new TestPayloadEvent(payload);
        var serializedEvent = serverHost.Serializer.Serialize(broadcastEvent);
        await serverHost.Publisher.PublishToAllAsync(broadcastEvent.EventId, serializedEvent, CancellationToken.None).ConfigureAwait(false);

        var peerAPayloads = await peerA.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var peerBPayloads = await peerB.WaitForMessageCountAsync(2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await peerA.WaitForAckCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await peerB.WaitForAckCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForServerInflightToDrainAsync(server, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        peerAPayloads.Should().ContainSingle(p => p == payload);
        peerBPayloads.Should().HaveCount(2).And.OnlyContain(p => p == payload);
        peerB.ConnectionCount.Should().BeGreaterThanOrEqualTo(2);
        peerB.AcknowledgedMessageIds.Should().HaveCount(1);
    }

    [Test]
    [Explicit("Integration tests require local TCP sockets and timing-sensitive coordination.")]
    public async Task Heartbeat_Drop_Raises_SessionInboundConnectionDropped_And_Reconnects()
    {
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(60),
            BackoffInitialDelay = TimeSpan.FromMilliseconds(10),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(40),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        RegisterServerEventHandler(serverHost);
        var server = serverHost.Server;

        var dropTcs = new TaskCompletionSource<SessionKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionInboundConnectionDropped += (key, token) =>
        {
            dropTcs.TrySetResult(key);
            return Task.FromResult(true);
        };

        await using var clientHost = new TestPersistentClientHost("127.0.0.1", port, options);
        await clientHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

        await WaitForAuthenticatedSessionsAsync(server, 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var session = server.SessionManager.AuthenticatedSessions
            .Concat(server.SessionManager.ValidAnonymousSessions)
            .OfType<ResilientPeerSession>()
            .First();

        ForceInboundConnectionTimeout(session, options.HeartbeatTimeout + options.HeartbeatInterval);

        await dropTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await clientHost.WaitForConnectionCountAsync(2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [Test]
    public async Task InboundSession_Dispatches_Event_Frames_With_Canonical_Acks()
    {
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(80),
            BackoffInitialDelay = TimeSpan.FromMilliseconds(20),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(50),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        var server = serverHost.Server;

        var sessionKeyTcs = new TaskCompletionSource<SessionKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventTcs = new TaskCompletionSource<TestPayloadEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        RegisterServerEventHandler(serverHost, (domainEvent, key) =>
        {
            if (domainEvent is TestPayloadEvent payloadEvent)
            {
                sessionKeyTcs.TrySetResult(key);
                eventTcs.TrySetResult(payloadEvent);
            }

            return Task.CompletedTask;
        });

        await using var clientHost = new TestPersistentClientHost("127.0.0.1", port, options);
        await clientHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var payload = $"dispatch-{Guid.NewGuid():N}";
        var domainEvent = new TestPayloadEvent(payload);
        var serializedEvent = serverHost.Serializer.Serialize(domainEvent);
        var messageId = await clientHost.Client.EnqueueEventAsync(serializedEvent, CancellationToken.None).ConfigureAwait(false);
        messageId.Should().Be(domainEvent.EventId);

        var receivedEvent = await eventTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var sessionKey = await sessionKeyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await clientHost.WaitForAckCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await clientHost.WaitForOutboxCountAsync(0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        receivedEvent.EventId.Should().Be(messageId);
        receivedEvent.Payload.Should().Be(payload);

        sessionKey.UserId.Should().Be(clientHost.Client.SessionKey.UserId);
        sessionKey.Host.Should().Be(clientHost.Client.SessionKey.Host);
        sessionKey.Port.Should().Be(clientHost.Client.SessionKey.Port);

        clientHost.AcknowledgedMessageIds.Should().ContainSingle(id => id == messageId);
    }

    private static async Task WaitForServerInflightToDrainAsync(InboundConnectionManager server, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var sessions = server.SessionManager.AuthenticatedSessions
                .Concat(server.SessionManager.ValidAnonymousSessions)
                .DistinctBy(session => session.Key);
            var drained = sessions.All(session => session.OutboundBuffer.InflightEventsCount == 0);

            if (drained)
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Server sessions still have in-flight messages after the allotted timeout.");
    }

    [Test]
    public async Task AnonymousClient_SessionJoined_EventPublisherAcceptsFallbackSession()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(80),
        };

        var port = GetFreeTcpPort();
        await using var serverHost = await StartServerAsync(port, options).ConfigureAwait(false);
        RegisterServerEventHandler(serverHost);
        var server = serverHost.Server;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

        var stream = client.GetStream();
        var anonymousEvent = new TestPayloadEvent("anonymous-payload");
        var anonymousFrame = SessionFrame.CreateEventFrame(
            anonymousEvent.EventId,
            serverHost.Serializer.Serialize(anonymousEvent));
        await SendFrameAsync(stream, anonymousFrame, CancellationToken.None).ConfigureAwait(false);

        await WaitForAuthenticatedSessionsAsync(server, 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var sessions = serverHost.SessionManager.AuthenticatedSessions
            .Concat(serverHost.SessionManager.ValidAnonymousSessions)
            .DistinctBy(session => session.Key)
            .ToArray();
        sessions.Should().HaveCount(1);
        var session = sessions.Single();
        session.RemoteEndpointHasAuthenticated.Should().BeTrue();

        SessionKey sessionKey = session.Key;
        sessionKey.UserId.Should().NotBe(Guid.Empty);
        sessionKey.Host.Should().NotBeNullOrWhiteSpace();
        sessionKey.Port.Should().BeGreaterThan(0);

        var remoteEndPoint = (IPEndPoint) client.Client.RemoteEndPoint!;
        SessionKey fallbackKey = new(Guid.Empty, remoteEndPoint.Address.ToString(), remoteEndPoint.Port)
        {
            IsAnonymousKey = true,
        };
        serverHost.SessionManager.HydrateAnonymousSessionId(fallbackKey, remoteEndPoint);
        fallbackKey.UserId.Should().Be(sessionKey.UserId);
        fallbackKey.Host.Should().Be(sessionKey.Host);
        fallbackKey.Port.Should().Be(sessionKey.Port);

        var fallbackPort = GetFreeTcpPort();
        var fallbackSerializer = new JsonEventSerializer();
        var fallbackOptions = new ResilientSessionOptions();
        var fallbackSessionManager = new SessionManager(fallbackPort, fallbackOptions);
        await using var fallbackListener = new PersistentPortListener(fallbackPort, fallbackSerializer, fallbackSessionManager);
        await using var fallbackPublisher = new PersistentEventPublisher(fallbackSessionManager);
        await fallbackListener.StartAsync().ConfigureAwait(false);

        var sessionJoined = new SessionJoined(sessionKey);
        var envelope = fallbackSerializer.Serialize(sessionJoined);
        Func<Task> act = () => fallbackPublisher.PublishToAllAsync(sessionJoined.EventId, envelope, CancellationToken.None);
        await act.Should().NotThrowAsync<ArgumentException>();
        fallbackPublisher.AcknowledgeEventReceipt(sessionJoined.EventId, sessionKey);
    }

    private sealed record TestPayloadEvent(string Payload) : IDomainEvent
    {
        public Guid EventId { get; init; } = Guid.NewGuid();
        public DateTime DateTimeOccurredUtc { get; init; } = DateTime.UtcNow;
    }

    private static async Task SendFrameAsync(NetworkStream stream, SessionFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForAuthenticatedSessionsAsync(InboundConnectionManager server, int expectedCount, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var sessions = server.SessionManager.AuthenticatedSessions
                .Concat(server.SessionManager.ValidAnonymousSessions)
                .DistinctBy(session => session.Key);
            var authenticatedCount = sessions.Count(session => session.RemoteEndpointHasAuthenticated);

            if (authenticatedCount >= expectedCount)
            {
                return;
            }

            await Task.Delay(1).ConfigureAwait(false);
        }

        throw new TimeoutException($"Server did not authenticate {expectedCount} sessions within the timeout window.");
    }

    private static async Task<(Task sendLoop, Task heartbeatLoop)> WaitForActiveOutboundLoopsAsync(ResilientOutboundConnection outboundConnection, TimeSpan timeout)
    {
        var sendLoopField = typeof(ResilientOutboundConnection).GetField("_sendLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var heartbeatLoopField = typeof(ResilientOutboundConnection).GetField("_heartbeatLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var sendLoop = (Task) sendLoopField.GetValue(outboundConnection)!;
            var heartbeatLoop = (Task) heartbeatLoopField.GetValue(outboundConnection)!;

            if (!ReferenceEquals(sendLoop, Task.CompletedTask) && !ReferenceEquals(heartbeatLoop, Task.CompletedTask))
            {
                return (sendLoop, heartbeatLoop);
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Active outbound loops were not observed before the timeout elapsed.");
    }

    private static void ForceInboundConnectionTimeout(ResilientPeerSession session, TimeSpan delta)
    {
        var inbound = (ResilientInboundConnection) session.InboundConnection;
        var field = typeof(ResilientInboundConnection).GetField("_lastRemoteActivityTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var staleTicks = DateTime.UtcNow.Subtract(delta).Ticks;
        field.SetValue(inbound, staleTicks);
    }

    private static async Task<ServerHost> StartServerAsync(int port, ResilientSessionOptions options)
    {
        var serializer = new JsonEventSerializer();
        var sessionManager = new SessionManager(port, options);
        var listener = new PersistentPortListener(port, serializer, sessionManager);
        var publisher = new PersistentEventPublisher(sessionManager);
        var host = new ServerHost(listener, publisher, serializer, sessionManager);
        await listener.StartAsync().ConfigureAwait(false);
        return host;
    }

    private static void RegisterServerEventHandler(ServerHost host, Func<IDomainEvent, SessionKey, Task>? handler = null)
    {
        host.Server.EventReceived += async (domainEvent, sessionKey) =>
        {
            if (handler is not null)
            {
                await handler(domainEvent, sessionKey).ConfigureAwait(false);
            }

            host.Publisher.AcknowledgeEventReceipt(domainEvent.EventId, sessionKey);
        };
    }

    private sealed class ServerHost : IAsyncDisposable
    {
        internal ServerHost(PersistentPortListener listener, PersistentEventPublisher publisher, IEventSerializer serializer, SessionManager sessionManager)
        {
            Listener = listener;
            Publisher = publisher;
            Serializer = serializer;
            SessionManager = sessionManager;
            Server = (InboundConnectionManager)listener.ConnectionManager;

            Listener.SessionConnectionAccepted += OnSessionConnectionAcceptedAsync;
            Listener.SessionInboundConnectionDropped += OnSessionInboundConnectionDroppedAsync;
            Server.AckReceived += OnAckReceivedAsync;
            Server.PingReceived += OnPingReceivedAsync;
        }

        internal PersistentPortListener Listener { get; }
        internal PersistentEventPublisher Publisher { get; }
        internal IEventSerializer Serializer { get; }
        internal SessionManager SessionManager { get; }
        internal InboundConnectionManager Server { get; }

        private Task OnSessionConnectionAcceptedAsync(SessionKey sessionKey, CancellationToken token)
        {
            return Publisher.ConnectionManager.ConnectAsync(sessionKey, token);
        }

        private Task<bool> OnSessionInboundConnectionDroppedAsync(SessionKey sessionKey, CancellationToken token)
        {
            return Publisher.ConnectionManager.TryReconnectAsync(sessionKey, token);
        }

        private Task OnAckReceivedAsync(Guid eventId, SessionKey sessionKey)
        {
            return Publisher.ConnectionManager.OnAckReceived(eventId, sessionKey);
        }

        private Task OnPingReceivedAsync(SessionKey sessionKey)
        {
            Publisher.ConnectionManager.SendPong(sessionKey);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            Listener.SessionConnectionAccepted -= OnSessionConnectionAcceptedAsync;
            Listener.SessionInboundConnectionDropped -= OnSessionInboundConnectionDroppedAsync;
            Server.AckReceived -= OnAckReceivedAsync;
            Server.PingReceived -= OnPingReceivedAsync;
            await Listener.DisposeAsync().ConfigureAwait(false);
            await Publisher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint) listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal sealed class TestPersistentClientHost : IAsyncDisposable
{
    private readonly string _outboxPath;
    private readonly ConcurrentQueue<string> _payloads = new();
    private readonly List<Guid> _acknowledged = new();
    private readonly List<(int Target, TaskCompletionSource Completion)> _messageWaiters = new();
    private readonly List<(int Target, TaskCompletionSource Completion)> _ackWaiters = new();
    private readonly List<(int Target, TaskCompletionSource Completion)> _connectionWaiters = new();
    private readonly object _waiterLock = new();

    private int _connectionCount;
    private int _dropAckFlag;
    private int _dropMessageFlag;
    private readonly bool _trackConnections;

    public TestPersistentClientHost(
        string host,
        int port,
        ResilientSessionOptions options,
        bool trackConnections = true,
        Guid? sessionUserId = null,
        bool dropFirstAck = false,
        bool dropFirstMessage = false)
    {
        _outboxPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.json");
        Client = new ResilientCompositSessionConnection(sessionUserId ?? Guid.NewGuid(), host, port, options);
        SetOutboxPath(Client, _outboxPath);
        _trackConnections = trackConnections;
        if (_trackConnections)
        {
            Client.ConnectionEstablished += OnConnectionEstablishedAsync;
        }
        Client.FrameReceived += OnFrameReceivedAsync;
        if (dropFirstAck)
        {
            DropNextAck();
        }

        if (dropFirstMessage)
        {
            DropNextMessage();
        }
    }

    public ResilientCompositSessionConnection Client { get; }

    public SessionKey SessionKey => Client.OutboundConnection.SessionKey;

    public IReadOnlyCollection<string> ReceivedPayloads => _payloads.ToArray();

    public IReadOnlyCollection<Guid> AcknowledgedMessageIds
    {
        get
        {
            lock (_acknowledged)
            {
                return _acknowledged.ToArray();
            }
        }
    }

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public Task StartAsync(CancellationToken cancellationToken)
        => Client.StartAsync(cancellationToken);

    public void DropNextAck() => Interlocked.Exchange(ref _dropAckFlag, 1);

    public void DropNextMessage() => Interlocked.Exchange(ref _dropMessageFlag, 1);

    public async Task<IReadOnlyList<string>> WaitForMessageCountAsync(int count, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        TaskCompletionSource waiter;
        lock (_waiterLock)
        {
            if (_payloads.Count >= count)
            {
                return _payloads.ToArray();
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _messageWaiters.Add((count, waiter));
        }

        await TryToAwaitWaiter(timeout, waiter).ConfigureAwait(false);
        return _payloads.ToArray();
    }

    private static async Task TryToAwaitWaiter(TimeSpan timeout, TaskCompletionSource waiter)
    {
        try
        {
            await waiter.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AggregateException or TimeoutException)
        {
            var exOrFlattened = ex is AggregateException aggEx ? aggEx.Flatten() : ex;
            await Console.Error.WriteLineAsync($"Persistent receive loop faulted: {exOrFlattened}");
            throw exOrFlattened;
        }
    }

    public async Task WaitForAckCountAsync(int count, TimeSpan timeout)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        TaskCompletionSource waiter;
        lock (_waiterLock)
        {
            if (_acknowledged.Count >= count)
            {
                return;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ackWaiters.Add((count, waiter));
        }

        await TryToAwaitWaiter(timeout, waiter).ConfigureAwait(false);
    }

    public async Task WaitForConnectionCountAsync(int count, TimeSpan timeout)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        TaskCompletionSource waiter;
        lock (_waiterLock)
        {
            if (_connectionCount >= count)
            {
                return;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionWaiters.Add((count, waiter));
        }

        await TryToAwaitWaiter(timeout, waiter).ConfigureAwait(false);
    }

    public async Task WaitForOutboxCountAsync(int expectedCount, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (GetOutboxEntries().Count == expectedCount)
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException($"Outbox did not reach the expected count of {expectedCount} within the timeout window.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_trackConnections)
        {
            Client.ConnectionEstablished -= OnConnectionEstablishedAsync;
        }
        Client.FrameReceived -= OnFrameReceivedAsync;
        await Client.DisposeAsync().ConfigureAwait(false);

        if (File.Exists(_outboxPath))
        {
            try
            {
                File.Delete(_outboxPath);
            }
            catch
            {
                // ignore cleanup failures in tests
            }
        }
    }

    private ValueTask OnConnectionEstablishedAsync(ResilientCompositSessionConnection session, CancellationToken cancellationToken)
    {
        var connections = Interlocked.Increment(ref _connectionCount);
        NotifyWaiters(_connectionWaiters, connections);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnFrameReceivedAsync(ResilientCompositSessionConnection session, SessionFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Event when frame.Payload is not null && frame.Id != Guid.Empty:
                _payloads.Enqueue(frame.Payload);
                NotifyWaiters(_messageWaiters, _payloads.Count);

                if (Interlocked.Exchange(ref _dropMessageFlag, 0) == 1)
                {
                    await session.AbortActiveConnectionAsync().ConfigureAwait(false);
                    break;
                }

                session.EnqueueFrame(SessionFrame.CreateAck(frame.Id));
                break;
            case SessionFrameKind.Ack when frame.Id != Guid.Empty:
                if (Interlocked.Exchange(ref _dropAckFlag, 0) == 1)
                {
                    await session.AbortActiveConnectionAsync().ConfigureAwait(false);
                    break;
                }

                lock (_acknowledged)
                {
                    _acknowledged.Add(frame.Id);
                }

                NotifyWaiters(_ackWaiters, _acknowledged.Count);
                break;
        }
    }

    private IReadOnlyCollection<Guid> GetOutboxEntries()
    {
        var entriesField = typeof(ResilientCompositSessionConnection).GetField("_outboxEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary) entriesField.GetValue(Client)!;
        var keys = new List<Guid>();
        foreach (var key in entries.Keys)
        {
            if (key is Guid id)
            {
                keys.Add(id);
            }
        }

        return keys;
    }

    private static void SetOutboxPath(ResilientCompositSessionConnection client, string path)
    {
        var field = typeof(ResilientCompositSessionConnection).GetField("_outboxPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(client, path);
    }

    private void NotifyWaiters(List<(int Target, TaskCompletionSource Completion)> waiters, int current)
    {
        if (waiters.Count == 0)
        {
            return;
        }

        List<TaskCompletionSource>? completed = null;
        lock (_waiterLock)
        {
            for (var i = waiters.Count - 1; i >= 0; i--)
            {
                var (target, tcs) = waiters[i];
                if (current >= target)
                {
                    completed ??= new List<TaskCompletionSource>();
                    completed.Add(tcs);
                    waiters.RemoveAt(i);
                }
            }
        }

        if (completed is null)
        {
            return;
        }

        foreach (var tcs in completed)
        {
            tcs.TrySetResult();
        }
    }
}
