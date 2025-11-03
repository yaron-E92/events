using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

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
        await using var server = new PersistentInboundSession(port, options);
        var deliveries = new List<string>();
        var firstDeliveryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayDeliveryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.FrameReceived += (key, frame, cancellationToken) =>
        {
            if (frame.Kind == SessionFrameKind.Event && frame.Payload is not null)
            {
                lock (deliveries)
                {
                    deliveries.Add(frame.Payload);
                    if (deliveries.Count == 1)
                    {
                        firstDeliveryTcs.TrySetResult();
                    }
                    else if (deliveries.Count == 2)
                    {
                        replayDeliveryTcs.TrySetResult();
                    }
                }
            }

            return Task.CompletedTask;
        };

        await server.StartAsync().ConfigureAwait(false);

        await using var clientHost = new TestPersistentClientHost("127.0.0.1", port, options);
        clientHost.DropNextAck();
        await clientHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var payload = $"payload-{Guid.NewGuid():N}";
        var messageId = await clientHost.Client.EnqueueEventAsync(payload, CancellationToken.None).ConfigureAwait(false);

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
        await using var server = new PersistentInboundSession(port, options);
        await server.StartAsync().ConfigureAwait(false);

        await using var peerA = new TestPersistentClientHost("127.0.0.1", port, options);
        await using var peerB = new TestPersistentClientHost("127.0.0.1", port, options);
        peerB.DropNextMessage();

        await peerA.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await peerB.StartAsync(CancellationToken.None).ConfigureAwait(false);

        await WaitForAuthenticatedSessionsAsync(server, 2, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        var payload = $"broadcast-{Guid.NewGuid():N}";
        server.QueueBroadcast(payload);

        var peerAPayloads = await peerA.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var peerBPayloads = await peerB.WaitForMessageCountAsync(2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await peerA.WaitForAckCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await peerB.WaitForAckCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForServerInflightToDrainAsync(server, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        peerAPayloads.Should().ContainSingle(p => p == payload);
        peerBPayloads.Should().HaveCount(2).And.OnlyContain(p => p == payload);
        peerB.ConnectionCount.Should().BeGreaterThanOrEqualTo(2);
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
        await using var server = new PersistentInboundSession(port, options);

        var sessionKeyTcs = new TaskCompletionSource<SessionKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameTcs = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.FrameReceived += (key, frame, cancellationToken) =>
        {
            if (frame.Kind == SessionFrameKind.Event)
            {
                sessionKeyTcs.TrySetResult(key);
                frameTcs.TrySetResult(frame);
            }

            return Task.CompletedTask;
        };

        await server.StartAsync().ConfigureAwait(false);

        await using var clientHost = new TestPersistentClientHost("127.0.0.1", port, options);
        await clientHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var payload = $"dispatch-{Guid.NewGuid():N}";
        var messageId = await clientHost.Client.EnqueueEventAsync(payload, CancellationToken.None).ConfigureAwait(false);

        var frame = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var sessionKey = await sessionKeyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await clientHost.WaitForAckCountAsync(1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await clientHost.WaitForOutboxCountAsync(0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        frame.Kind.Should().Be(SessionFrameKind.Event);
        frame.Id.Should().Be(messageId);
        frame.Payload.Should().Be(payload);

        sessionKey.UserId.Should().Be(clientHost.Client.SessionKey.UserId);
        sessionKey.Host.Should().Be(clientHost.Client.SessionKey.Host);
        sessionKey.Port.Should().Be(clientHost.Client.SessionKey.Port);

        clientHost.AcknowledgedMessageIds.Should().ContainSingle(id => id == messageId);
    }

    private static async Task WaitForServerInflightToDrainAsync(PersistentInboundSession server, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var sessions = server.Sessions.Values;
            var drained = true;
            foreach (var sessionState in sessions)
            {
                if (sessionState is null)
                {
                    continue;
                }

                var outboundProperty = sessionState.GetType().GetProperty("Outbound", BindingFlags.Instance | BindingFlags.Public);
                if (outboundProperty is null)
                {
                    continue;
                }

                var outbound = outboundProperty.GetValue(sessionState);
                if (outbound is null)
                {
                    continue;
                }

                var inflightProperty = outbound.GetType().GetProperty("InflightCount", BindingFlags.Instance | BindingFlags.Public);
                if (inflightProperty is null)
                {
                    continue;
                }

                var inflightCount = (int) (inflightProperty.GetValue(outbound) ?? 0);
                if (inflightCount != 0)
                {
                    drained = false;
                    break;
                }
            }

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
        await using var server = new PersistentInboundSession(port, options);
        await server.StartAsync().ConfigureAwait(false);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

        var stream = client.GetStream();
        var anonymousFrame = SessionFrame.CreateMessage(Guid.NewGuid(), "anonymous-payload");
        await SendFrameAsync(stream, anonymousFrame, CancellationToken.None).ConfigureAwait(false);

        await WaitForAuthenticatedSessionsAsync(server, 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var sessions = server.Sessions;
        sessions.Should().HaveCount(1);
        var sessionEntry = sessions.Single();
        sessionEntry.Value.Should().NotBeNull();
        sessionEntry.Value.HasAuthenticated.Should().BeTrue();

        var sessionKey = sessionEntry.Key;
        sessionKey.UserId.Should().NotBe(Guid.Empty);
        sessionKey.Host.Should().NotBeNullOrWhiteSpace();
        sessionKey.Port.Should().BeGreaterThan(0);

        var fallbackFactory = typeof(PersistentInboundSession).GetMethod("CreateFallbackSessionKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var remoteEndPoint = (IPEndPoint) client.Client.RemoteEndPoint!;
        var fallbackKey = (SessionKey) fallbackFactory.Invoke(server, [new IPEndPoint(remoteEndPoint.Address, remoteEndPoint.Port)])!;
        fallbackKey.UserId.Should().Be(sessionKey.UserId);
        fallbackKey.Host.Should().Be(sessionKey.Host);
        fallbackKey.Port.Should().Be(sessionKey.Port);

        var listener = new PersistentSessionListener(GetFreeTcpPort(), new ResilientSessionOptions(), new NoopEventTransport());
        await using var publisher = new PersistentEventPublisher(listener, new ResilientSessionOptions(), null, new NoopEventSerializer());

        Func<Task> act = () => publisher.OnNextAsync(new SessionJoined(sessionKey), CancellationToken.None);
        await act.Should().NotThrowAsync<ArgumentException>();
    }

    private static async Task SendFrameAsync(NetworkStream stream, SessionFrame frame, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForAuthenticatedSessionsAsync(PersistentInboundSession server, int expectedCount, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var sessions = server.Sessions;
            var authenticatedCount = 0;

            foreach (var session in sessions.Values)
            {
                if (session is { HasAuthenticated: true })
                {
                    authenticatedCount++;
                }
            }

            if (authenticatedCount >= expectedCount)
            {
                return;
            }

            await Task.Delay(1).ConfigureAwait(false);
        }

        throw new TimeoutException($"Server did not authenticate {expectedCount} sessions within the timeout window.");
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

internal sealed class NoopEventTransport : IEventTransport
{
    public Task AcceptIncomingTrafficAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
        => Task.CompletedTask;

    public Task PublishAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
        => Task.CompletedTask;

    public void Subscribe<TEvent>() where TEvent : class, IDomainEvent
    {
    }
}

internal sealed class NoopEventSerializer : IEventSerializer
{
    public string Serialize<T>(T evt) where T : class, IDomainEvent => string.Empty;

    public (Type? type, IDomainEvent? domainEvent) Deserialize(string data) => (null, null);
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

    public TestPersistentClientHost(string host, int port, ResilientSessionOptions options)
    {
        _outboxPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.json");
        Client = new ResilientSessionClient(Guid.NewGuid(), host, port, options);
        SetOutboxPath(Client, _outboxPath);
        Client.ConnectionEstablished += OnConnectionEstablishedAsync;
        Client.FrameReceived += OnFrameReceivedAsync;
    }

    public ResilientSessionClient Client { get; }

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
        Client.ConnectionEstablished -= OnConnectionEstablishedAsync;
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

    private ValueTask OnConnectionEstablishedAsync(ResilientSessionClient session, CancellationToken cancellationToken)
    {
        var connections = Interlocked.Increment(ref _connectionCount);
        NotifyWaiters(_connectionWaiters, connections);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnFrameReceivedAsync(ResilientSessionClient session, SessionFrame frame, CancellationToken cancellationToken)
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

                session.EnqueueControlMessage(SessionFrame.CreateAck(frame.Id));
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
        var entriesField = typeof(ResilientSessionClient).GetField("_outboxEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
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

    private static void SetOutboxPath(ResilientSessionClient client, string path)
    {
        var field = typeof(ResilientSessionClient).GetField("_outboxPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
