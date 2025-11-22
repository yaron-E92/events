using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;
using Yaref92.Events.Transports.ConnectionManagers;
using Yaref92.Events.UnitTests;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class InboundConnectionManagerTests
{
    [Test]
    public async Task HandleIncomingTransientConnectionAsync_DeliversInboundEventFrames()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(200),
        };

        const int clientListenerPort = 62000;
        options.CallbackPort = clientListenerPort;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var sessionManager = new SessionManager(((IPEndPoint)listener.LocalEndpoint).Port, options);
        var serializer = new FakeEventSerializer();

        await using var manager = new InboundConnectionManager(sessionManager, serializer);
        var inboundManager = (IInboundConnectionManager)manager;
        var eventReceived = new TaskCompletionSource<IDomainEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        inboundManager.EventReceived += (domainEvent, _) =>
        {
            eventReceived.TrySetResult(domainEvent);
            return Task.CompletedTask;
        };

        using var client = new TcpClient();
        Task connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);

        using var serverClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        await connectTask.ConfigureAwait(false);

        try
        {
            var remotePort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            var sessionKey = new SessionKey(Guid.NewGuid(), IPAddress.Loopback.ToString(), remotePort);
            var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);
            var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);

            var initializationTask = manager.HandleIncomingTransientConnectionAsync(serverClient, CancellationToken.None);

            await WriteFrameAsync(client, authFrame).ConfigureAwait(false);

            var initialization = await initializationTask.ConfigureAwait(false);
            initialization.IsSuccess.Should().BeTrue();
            initialization.Session.Should().NotBeNull();
            initialization.ConnectionCancellation.Should().NotBeNull();

            var eventFrame = SessionFrame.CreateEventFrame(Guid.NewGuid(), FakeEventSerializer.ExpectedPayload);
            await WriteFrameAsync(client, eventFrame).ConfigureAwait(false);

            var domainEvent = await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            domainEvent.Should().BeOfType<DummyEvent>();
            domainEvent.As<DummyEvent>().Text.Should().Be(FakeEventSerializer.ExpectedPayload);

            await initialization.ConnectionCancellation!.CancelAsync().ConfigureAwait(false);
            initialization.ConnectionCancellation.Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task FrameHandlers_FireWithoutSockets_WhenFakeSessionRaisesFrames()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
        };

        var sessionKey = new SessionKey(Guid.NewGuid(), "loopback", 5000);
        var sessionManager = new SessionManager(sessionKey.Port, options);
        var serializer = new DeterministicEventSerializer();

        var inbound = new FakeInboundResilientConnection(sessionKey);
        var outbound = new FakeOutboundResilientConnection(sessionKey);
        var session = new FakeResilientPeerSession(sessionKey, inbound, outbound);
        sessionManager.InjectSession(session);

        await using var manager = new InboundConnectionManager(sessionManager, serializer);
        var inboundManager = (IInboundConnectionManager)manager;

        var eventReceived = new TaskCompletionSource<(IDomainEvent DomainEvent, SessionKey Key)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<IDomainEvent, SessionKey, Task> eventHandler = (domainEvent, key) =>
        {
            eventReceived.TrySetResult((domainEvent, key));
            return Task.CompletedTask;
        };
        inboundManager.EventReceived += eventHandler;

        var ackSource = new TaskCompletionSource<(Guid EventId, SessionKey Key)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Guid, SessionKey, Task> ackHandler = (eventId, key) =>
        {
            ackSource.TrySetResult((eventId, key));
            return Task.CompletedTask;
        };
        manager.AckReceived += ackHandler;

        var pingSource = new TaskCompletionSource<SessionKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<SessionKey, Task> pingHandler = key =>
        {
            pingSource.TrySetResult(key);
            return Task.CompletedTask;
        };
        manager.PingReceived += pingHandler;

        try
        {
            manager.AttachFrameHandler(session);

            var expectedEventId = Guid.NewGuid();
            await inbound.RaiseFrameAsync(SessionFrame.CreateEventFrame(expectedEventId, DeterministicEventSerializer.ExpectedPayload), CancellationToken.None)
                .ConfigureAwait(false);

            var expectedAck = Guid.NewGuid();
            await inbound.RaiseFrameAsync(SessionFrame.CreateAck(expectedAck), CancellationToken.None).ConfigureAwait(false);

            await inbound.RaiseFrameAsync(SessionFrame.CreatePing(), CancellationToken.None).ConfigureAwait(false);

            (IDomainEvent DomainEvent, SessionKey Key) eventArgs = await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            eventArgs.DomainEvent.Should().BeOfType<DummyEvent>();
            eventArgs.DomainEvent.As<DummyEvent>().Text.Should().Be(DeterministicEventSerializer.ExpectedPayload);
            eventArgs.Key.Should().Be(sessionKey);

            (Guid EventId, SessionKey Key) ackArgs = await ackSource.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            ackArgs.EventId.Should().Be(expectedAck);
            ackArgs.Key.Should().Be(sessionKey);

            SessionKey pingKey = await pingSource.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            pingKey.Should().Be(sessionKey);

            session.TouchCount.Should().BeGreaterThanOrEqualTo(3);
        }
        finally
        {
            inboundManager.EventReceived -= eventHandler;
            manager.AckReceived -= ackHandler;
            manager.PingReceived -= pingHandler;
        }
    }

    [Test]
    public async Task HandleIncomingTransientConnectionAsync_FramesBeforeHandlers_DoNotThrowAndHandlersFireAfterAttachment()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(200),
        };

        const int clientListenerPort = 62000;
        options.CallbackPort = clientListenerPort;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var sessionManager = new SessionManager(((IPEndPoint)listener.LocalEndpoint).Port, options);
        var serializer = new FakeEventSerializer();

        await using var manager = new InboundConnectionManager(sessionManager, serializer);
        var inboundManager = (IInboundConnectionManager)manager;

        using var client = new TcpClient();
        Task connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);

        using var serverClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        await connectTask.ConfigureAwait(false);

        try
        {
            IPEndPoint remoteEndpoint = (IPEndPoint) client.Client.LocalEndPoint!;
            var sessionKey = new SessionKey(Guid.NewGuid(), IPAddress.Loopback.ToString(), remoteEndpoint.Port);
            var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);
            var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);

            Task<ConnectionInitializationResult> initializationTask =
                manager.HandleIncomingTransientConnectionAsync(serverClient, CancellationToken.None);

            await WriteFrameAsync(client, authFrame).ConfigureAwait(false);

            ConnectionInitializationResult initialization = await initializationTask.ConfigureAwait(false);
            initialization.IsSuccess.Should().BeTrue();
            initialization.Session.Should().NotBeNull();

            var expectedAckId = Guid.NewGuid();

            Func<Task> sendFramesWithoutHandlers = async () =>
            {
                await WriteFrameAsync(client, SessionFrame.CreateAck(Guid.NewGuid())).ConfigureAwait(false);
                await WriteFrameAsync(client, SessionFrame.CreatePing()).ConfigureAwait(false);
                await WriteFrameAsync(client, SessionFrame.CreateEventFrame(Guid.NewGuid(), FakeEventSerializer.ExpectedPayload))
                    .ConfigureAwait(false);
            };

            await sendFramesWithoutHandlers.Should().NotThrowAsync();

            await Task.Delay(TimeSpan.FromSeconds(5)); // Since I kept intercepting the frames sent above later on, screwing my assertions

            var ackReceived = new TaskCompletionSource<(Guid eventId, SessionKey key)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task ackHandler(Guid eventId, SessionKey key)
            {
                ackReceived.TrySetResult((eventId, key));
                return Task.CompletedTask;
            }
            manager.AckReceived += ackHandler;

            var pingReceived = new TaskCompletionSource<SessionKey>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task pingHandler(SessionKey key)
            {
                pingReceived.TrySetResult(key);
                return Task.CompletedTask;
            }
            manager.PingReceived += pingHandler;

            var eventReceived = new TaskCompletionSource<(IDomainEvent domainEvent, SessionKey key)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var eventInvocationCount = 0;
            Task eventHandler(IDomainEvent domainEvent, SessionKey key)
            {
                Interlocked.Increment(ref eventInvocationCount);
                eventReceived.TrySetResult((domainEvent, key));
                return Task.CompletedTask;
            }
            inboundManager.EventReceived += eventHandler;

            try
            {
                const string expectedEventId = "post-handler-event";

                await WriteFrameAsync(client, SessionFrame.CreateAck(expectedAckId)).ConfigureAwait(false);
                await WriteFrameAsync(client, SessionFrame.CreatePing()).ConfigureAwait(false);
                await WriteFrameAsync(client, SessionFrame.CreateEventFrame(FakeEventSerializer.ExpectedEventId, expectedEventId))
                    .ConfigureAwait(false);

                (Guid eventId, SessionKey key) ack = await ackReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                ack.eventId.Should().Be(expectedAckId);
                var expectedKey = new SessionKey(sessionKey.UserId, IPAddress.Loopback.ToString(), clientListenerPort)
                {
                    IsAnonymousKey = sessionKey.IsAnonymousKey,
                };
                ack.key.Should().Be(expectedKey);

                SessionKey pingKey = await pingReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                pingKey.Should().Be(expectedKey);

                (IDomainEvent domainEvent, SessionKey key) =
                    await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                domainEvent.Should().BeOfType<DummyEvent>();
                domainEvent.As<DummyEvent>().Text.Should().Be(expectedEventId);
                key.Should().Be(expectedKey);

                Volatile.Read(ref eventInvocationCount).Should().Be(1);
            }
            finally
            {
                inboundManager.EventReceived -= eventHandler;
                manager.PingReceived -= pingHandler;
                manager.AckReceived -= ackHandler;
            }

            if (initialization.ConnectionCancellation is not null)
            {
                await initialization.ConnectionCancellation.CancelAsync().ConfigureAwait(false);
                initialization.ConnectionCancellation.Dispose();
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task HandleIncomingTransientConnectionAsync_FailedAuthenticationDisposesTransientClient()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = true,
            AuthenticationToken = "expected-secret",
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(200),
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var sessionManager = new SessionManager(((IPEndPoint)listener.LocalEndpoint).Port, options);
        var serializer = new FakeEventSerializer();

        await using var manager = new InboundConnectionManager(sessionManager, serializer);

        using var client = new TcpClient();
        Task connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);

        var serverClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        await connectTask.ConfigureAwait(false);

        try
        {
            var remotePort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            var sessionKey = new SessionKey(Guid.NewGuid(), IPAddress.Loopback.ToString(), remotePort);
            var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);
            var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: "invalid-secret");

            Task<ConnectionInitializationResult> initializationTask =
                manager.HandleIncomingTransientConnectionAsync(serverClient, CancellationToken.None);

            await WriteFrameAsync(client, authFrame).ConfigureAwait(false);

            ConnectionInitializationResult initialization = await initializationTask.ConfigureAwait(false);
            initialization.IsSuccess.Should().BeFalse();
            initialization.Session.Should().BeNull();
            initialization.ConnectionCancellation.Should().BeNull();

            serverClient.Invoking(c => c.GetStream())
                .Should()
                .Throw<Exception>()
                .Where(ex => ex.GetType().Equals(typeof(ObjectDisposedException)) || ex.GetType().Equals(typeof(InvalidOperationException)),
                    "failed initialization should dispose of the transient server-side socket");
        }
        finally
        {
            serverClient.Dispose();
            listener.Stop();
        }
    }

    [Test]
    public async Task HandleIncomingTransientConnectionAsync_ReconnectedSessionFiresFrameHandlerOncePerFrame()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(200),
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var sessionManager = new SessionManager(((IPEndPoint)listener.LocalEndpoint).Port, options);
        var serializer = new FakeEventSerializer();

        await using var manager = new InboundConnectionManager(sessionManager, serializer);
        var inboundManager = (IInboundConnectionManager)manager;

        const string firstPayload = FakeEventSerializer.ExpectedPayload;
        const string secondPayload = "inbound-payload-after-reconnect";

        var firstEventReceived = new TaskCompletionSource<IDomainEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInvocationCount = 0;
        var secondEventReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<IDomainEvent, SessionKey, Task> handler = (domainEvent, _) =>
        {
            if (domainEvent is DummyEvent dummy)
            {
                if (dummy.Text == firstPayload)
                {
                    firstEventReceived.TrySetResult(domainEvent);
                }
                else if (dummy.Text == secondPayload)
                {
                    var count = Interlocked.Increment(ref secondInvocationCount);
                    secondEventReceived.TrySetResult(count);
                }
            }

            return Task.CompletedTask;
        };

        inboundManager.EventReceived += handler;

        TcpClient? firstServerClient = null;
        TcpClient? secondServerClient = null;
        CancellationTokenSource? firstConnectionCancellation = null;
        CancellationTokenSource? secondConnectionCancellation = null;

        try
        {
            using var firstClient = new TcpClient();
            Task firstConnectTask = firstClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            firstServerClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await firstConnectTask.ConfigureAwait(false);

            var remotePort = ((IPEndPoint)firstClient.Client.LocalEndPoint!).Port;
            var sessionKey = new SessionKey(Guid.NewGuid(), IPAddress.Loopback.ToString(), remotePort);
            var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);
            var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);

            Task<ConnectionInitializationResult> firstInitializationTask = manager.HandleIncomingTransientConnectionAsync(firstServerClient, CancellationToken.None);

            await WriteFrameAsync(firstClient, authFrame).ConfigureAwait(false);

            ConnectionInitializationResult firstInitialization = await firstInitializationTask.ConfigureAwait(false);
            firstInitialization.IsSuccess.Should().BeTrue();
            firstInitialization.Session.Should().NotBeNull();
            firstInitialization.ConnectionCancellation.Should().NotBeNull();

            firstConnectionCancellation = firstInitialization.ConnectionCancellation;

            var firstEventFrame = SessionFrame.CreateEventFrame(Guid.NewGuid(), firstPayload);
            await WriteFrameAsync(firstClient, firstEventFrame).ConfigureAwait(false);

            var firstDomainEvent = await firstEventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            firstDomainEvent.Should().BeOfType<DummyEvent>();
            firstDomainEvent.As<DummyEvent>().Text.Should().Be(firstPayload);

            firstClient.Dispose();
            firstServerClient.Dispose();

            if (firstConnectionCancellation is not null)
            {
                await firstConnectionCancellation.CancelAsync().ConfigureAwait(false);
            }

            using var secondClient = new TcpClient();
            Task secondConnectTask = secondClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            secondServerClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await secondConnectTask.ConfigureAwait(false);

            Task<ConnectionInitializationResult> secondInitializationTask = manager.HandleIncomingTransientConnectionAsync(secondServerClient, CancellationToken.None);

            await WriteFrameAsync(secondClient, authFrame).ConfigureAwait(false);

            ConnectionInitializationResult secondInitialization = await secondInitializationTask.ConfigureAwait(false);
            secondInitialization.IsSuccess.Should().BeTrue();
            secondInitialization.Session.Should().NotBeNull();
            secondInitialization.ConnectionCancellation.Should().NotBeNull();

            secondConnectionCancellation = secondInitialization.ConnectionCancellation;

            var secondEventFrame = SessionFrame.CreateEventFrame(Guid.NewGuid(), secondPayload);
            await WriteFrameAsync(secondClient, secondEventFrame).ConfigureAwait(false);

            var invocationCount = await secondEventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            invocationCount.Should().Be(1);
            Volatile.Read(ref secondInvocationCount).Should().Be(1);

            if (secondConnectionCancellation is not null)
            {
                await secondConnectionCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            inboundManager.EventReceived -= handler;

            secondConnectionCancellation?.Dispose();
            firstConnectionCancellation?.Dispose();

            secondServerClient?.Dispose();
            firstServerClient?.Dispose();

            listener.Stop();
        }
    }

    [Test]
    public async Task HandleIncomingTransientConnectionAsync_NewlyAuthenticatedSessionRemainsActiveUntilHeartbeatTimeoutExpires()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(200),
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var sessionManager = new SessionManager(((IPEndPoint)listener.LocalEndpoint).Port, options);
        var serializer = new FakeEventSerializer();

        await using var manager = new InboundConnectionManager(sessionManager, serializer);

        using var client = new TcpClient();
        Task connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);

        using var serverClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        await connectTask.ConfigureAwait(false);

        try
        {
            var remotePort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            var sessionKey = new SessionKey(Guid.NewGuid(), IPAddress.Loopback.ToString(), remotePort);
            var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);
            var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);

            var initializationTask = manager.HandleIncomingTransientConnectionAsync(serverClient, CancellationToken.None);

            await WriteFrameAsync(client, authFrame).ConfigureAwait(false);

            var initialization = await initializationTask.ConfigureAwait(false);
            initialization.IsSuccess.Should().BeTrue();
            initialization.Session.Should().NotBeNull();
            initialization.ConnectionCancellation.Should().NotBeNull();

            var session = initialization.Session!;
            session.InboundConnection.IsPastTimeout.Should().BeFalse("authentication should count as remote activity");

            await Task.Delay(options.HeartbeatTimeout / 2).ConfigureAwait(false);
            session.InboundConnection.IsPastTimeout.Should().BeFalse("connection should remain active before the timeout elapses");

            await Task.Delay(options.HeartbeatTimeout + options.HeartbeatInterval).ConfigureAwait(false);
            session.InboundConnection.IsPastTimeout.Should().BeTrue("connection should become stale once the timeout elapses");

            await initialization.ConnectionCancellation!.CancelAsync().ConfigureAwait(false);
            initialization.ConnectionCancellation.Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test, Explicit("Since it is flaky, I don't want it to fail the CI pipeline")]
    public async Task MonitorConnectionsAsync_NoDropHandler_DoesNotRaiseUnobservedExceptions()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(75),
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var unobservedRaised = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            unobservedRaised = true;
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            var sessionManager = new SessionManager(((IPEndPoint)listener.LocalEndpoint).Port, options);
            var serializer = new FakeEventSerializer();

            await using var manager = new InboundConnectionManager(sessionManager, serializer);
            using var client = new TcpClient();
            Task connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint) listener.LocalEndpoint).Port);
            using var serverClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await connectTask.ConfigureAwait(false);

            var remotePort = ((IPEndPoint) client.Client.LocalEndPoint!).Port;
            var sessionKey = new SessionKey(Guid.NewGuid(), IPAddress.Loopback.ToString(), remotePort);
            var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);
            var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);

            Task<ConnectionInitializationResult> initializationTask =
                manager.HandleIncomingTransientConnectionAsync(serverClient, CancellationToken.None);

            await WriteFrameAsync(client, authFrame).ConfigureAwait(false);

            ConnectionInitializationResult initialization = await initializationTask.ConfigureAwait(false);
            initialization.IsSuccess.Should().BeTrue();
            initialization.Session.Should().NotBeNull();

            var session = initialization.Session!;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!session.InboundConnection.IsPastTimeout)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Assert.Fail("Session never became stale.");
                }

                await Task.Delay(options.HeartbeatInterval).ConfigureAwait(false);
            }

            await Task.Delay(options.HeartbeatInterval * 2).ConfigureAwait(false);

            if (initialization.ConnectionCancellation is not null)
            {
                await initialization.ConnectionCancellation.CancelAsync().ConfigureAwait(false);
                initialization.ConnectionCancellation.Dispose();
            }
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            listener.Stop();
        }

        unobservedRaised.Should().BeFalse("the monitor loop should ignore stale connections when no drop handler is registered");
    }

    private static async Task WriteFrameAsync(TcpClient client, SessionFrame frame)
    {
        var stream = client.GetStream();
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var length = BitConverter.GetBytes(payload.Length);

        await stream.WriteAsync(length, 0, length.Length).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private sealed class FakeEventSerializer : IEventSerializer
    {
        internal const string ExpectedPayload = "inbound-payload";
        internal static readonly Guid ExpectedEventId = Guid.NewGuid();

        public string Serialize<T>(T evt) where T : class, IDomainEvent =>
            throw new NotSupportedException();

        public (Type? type, IDomainEvent? domainEvent) Deserialize(string data) =>
            string.IsNullOrEmpty(data)
                ? (null, null)
                : (typeof(DummyEvent), new DummyEvent(data, eventId: ExpectedEventId));
    }
}
