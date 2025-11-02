using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using FluentAssertions;

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
        await using var server = new ResilientTcpServer(port, options);
        var deliveries = new List<string>();
        var firstDeliveryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayDeliveryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
        await using var server = new ResilientTcpServer(port, options);
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

    private static async Task WaitForServerInflightToDrainAsync(ResilientTcpServer server, TimeSpan timeout)
    {
        var sessionsField = typeof(ResilientTcpServer).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var sessions = (ConcurrentDictionary<string, ResilientTcpServer.SessionState>) sessionsField.GetValue(server)!;
            var drained = true;
            foreach (var sessionState in sessions.Values)
            {
                if (sessionState is null)
                {
                    continue;
                }

                var inflightField = sessionState.GetType().GetField("_inflight", BindingFlags.Instance | BindingFlags.NonPublic);
                if (inflightField is null)
                {
                    continue;
                }

                var inflight = (ConcurrentDictionary<long, SessionFrame>) inflightField.GetValue(sessionState)!;
                if (!inflight.IsEmpty)
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

    private static async Task WaitForAuthenticatedSessionsAsync(ResilientTcpServer server, int expectedCount, TimeSpan timeout)
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

internal sealed class TestPersistentClientHost : IAsyncDisposable
{
    private readonly string _outboxPath;
    private readonly ConcurrentQueue<string> _payloads = new();
    private readonly List<Guid> _acknowledged = new();
    private readonly List<(int Target, TaskCompletionSource Completion)> _messageWaiters = new();
    private readonly List<(int Target, TaskCompletionSource Completion)> _ackWaiters = new();
    private readonly List<(int Target, TaskCompletionSource Completion)> _connectionWaiters = new();
    private readonly object _waiterLock = new();

    private TcpClient? _activeClient;
    private int _connectionCount;
    private int _dropAckFlag;
    private int _dropMessageFlag;

    public TestPersistentClientHost(string host, int port, ResilientSessionOptions options)
    {
        _outboxPath = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.json");
        Client = new ResilientSessionClient(Guid.NewGuid(), host, port, options);
        SetOutboxPath(Client, _outboxPath);
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
        await Client.DisposeAsync().ConfigureAwait(false);
        var client = Interlocked.Exchange(ref _activeClient, null);
        client?.Dispose();

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

    private Task OnClientConnectedAsync(ResilientSessionClient session, TcpClient client, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _activeClient, client);
        var connections = Interlocked.Increment(ref _connectionCount);
        NotifyWaiters(_connectionWaiters, connections);

        _ = Task.Run(() => ReceiveLoopAsync(session, client, cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(ResilientSessionClient session, TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SessionFrameIO.FrameReadResult result = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess || result.Frame is null)
                {
                    break;
                }

                await HandleFrameAsync(session, client, result.Frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutdown requested
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            await Console.Error.WriteLineAsync($"Test receive loop terminated: {ex}").ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(client, Interlocked.CompareExchange(ref _activeClient, null, client)))
            {
                client.Dispose();
            }
        }
    }

    private Task HandleFrameAsync(ResilientSessionClient session, TcpClient client, SessionFrame frame)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Event when frame.Payload is not null && frame.Id != Guid.Empty:
                _payloads.Enqueue(frame.Payload);
                session.RecordRemoteActivity();
                NotifyWaiters(_messageWaiters, _payloads.Count);

                if (Interlocked.Exchange(ref _dropMessageFlag, 0) == 1)
                {
                    DropClient(client);
                    break;
                }

                session.EnqueueControlMessage(SessionFrame.CreateAck(frame.Id));
                break;
            case SessionFrameKind.Ack when frame.Id != Guid.Empty:
                session.RecordRemoteActivity();

                if (Interlocked.Exchange(ref _dropAckFlag, 0) == 1)
                {
                    DropClient(client);
                    break;
                }

                session.Acknowledge(frame.Id);
                lock (_acknowledged)
                {
                    _acknowledged.Add(frame.Id);
                }

                NotifyWaiters(_ackWaiters, _acknowledged.Count);
                break;
            case SessionFrameKind.Ping:
                session.RecordRemoteActivity();
                session.EnqueueControlMessage(SessionFrame.CreatePong());
                break;
            case SessionFrameKind.Pong:
            case SessionFrameKind.Auth:
                session.RecordRemoteActivity();
                break;
        }

        return Task.CompletedTask;
    }

    private void DropClient(TcpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch
        {
            // ignored for test cleanup
        }
    }

    private IReadOnlyCollection<long> GetOutboxEntries()
    {
        var entriesField = typeof(ResilientSessionClient).GetField("_outboxEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary) entriesField.GetValue(Client)!;
        var keys = new List<long>();
        foreach (var key in entries.Keys)
        {
            if (key is long id)
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
