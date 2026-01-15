#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Tcp.Connections;

namespace Yaref92.Events.UnitTests.Connections;

[TestFixture]
public sealed class ResilientOutboundConnectionTests
{
    [Test]
    public async Task FullyReleaseReconnectGate_AllowsRepeatedAttempts_WhenMaxAttemptsIsOne()
    {
        var options = new ResilientSessionOptions
        {
            MaximalReconnectAttempts = 1,
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 12345);

        await using var connection = new ResilientOutboundConnection(options, sessionKey);

        connection.GetReconnectGateCurrentCountForTesting().Should().Be(1);

        using var firstWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(firstWaitCts.Token).ConfigureAwait(false);

        connection.GetReconnectGateCurrentCountForTesting().Should().Be(0);

        var firstRefillSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);

        connection.FullyReleaseReconnectGateForTesting();

        await firstRefillSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        connection.GetReconnectGateCurrentCountForTesting().Should().Be(1);

        using var secondWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(secondWaitCts.Token).ConfigureAwait(false);

        connection.GetReconnectGateCurrentCountForTesting().Should().Be(0);

        var secondRefillSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);

        connection.FullyReleaseReconnectGateForTesting();

        await secondRefillSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        connection.GetReconnectGateCurrentCountForTesting().Should().Be(1);
    }

    [Test]
    public async Task FullyReleaseReconnectGate_SignalsOnlyWhenGateRefilled()
    {
        var options = new ResilientSessionOptions
        {
            MaximalReconnectAttempts = 1,
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 12345);

        await using var connection = new ResilientOutboundConnection(options, sessionKey);

        var refillSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);

        connection.FullyReleaseReconnectGateForTesting();
        refillSignal.IsCompleted.Should().BeFalse();

        using var firstWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(firstWaitCts.Token).ConfigureAwait(false);

        connection.FullyReleaseReconnectGateForTesting();
        await refillSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        var nextSignal = connection.WaitForReconnectGateSignalForTestingAsync(CancellationToken.None);
        nextSignal.IsCompleted.Should().BeFalse();

        using var secondWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await connection.WaitReconnectGateForTestingAsync(secondWaitCts.Token).ConfigureAwait(false);

        connection.FullyReleaseReconnectGateForTesting();
        await nextSignal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }

    [Test]
    public async Task RefreshConnectionAsync_StopsAfterConfiguredNumberOfFailures()
    {
        var options = new ResilientSessionOptions
        {
            MaximalReconnectAttempts = 2,
            BackoffInitialDelay = TimeSpan.FromMilliseconds(1),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(1),
        };

        var sessionKey = new SessionKey(Guid.NewGuid(), "127.0.0.1", 65000);

        await using var connection = new ResilientOutboundConnection(options, sessionKey);

        using var cts = new CancellationTokenSource();
        var refreshResult = await connection.RefreshConnectionAsync(cts.Token).ConfigureAwait(false);

        refreshResult.Should().BeFalse();
        connection.GetReconnectGateCurrentCountForTesting().Should().Be(0);
    }

    [Test]
    public async Task DumpBuffer_RemainsConsistent_WhenConcurrentEnqueueAndAckOccur()
    {
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(10),
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 12345);

        await using var connection = new ResilientOutboundConnection(options, sessionKey);

        var framesToDump = Enumerable.Range(0, 64)
            .Select(index => SessionFrame.CreateEventFrame(Guid.NewGuid(), $"initial-{index}"))
            .ToList();

        foreach (var frame in framesToDump)
        {
            connection.EnqueueFrame(frame);
        }

        var framesToAck = framesToDump.Take(framesToDump.Count / 2).ToList();

        var concurrentFrames = Enumerable.Range(0, 64)
            .Select(index => SessionFrame.CreateEventFrame(Guid.NewGuid(), $"concurrent-{index}"))
            .ToList();

        var dumpTask = connection.DumpBuffer();
        var enqueueTask = Task.Run(() =>
        {
            foreach (var frame in concurrentFrames)
            {
                connection.EnqueueFrame(frame);
                Thread.Yield();
            }
        });

        var ackTask = Task.Run(() =>
        {
            foreach (var frame in framesToAck)
            {
                connection.OnAckReceived(frame.Id);
                Thread.Yield();
            }
        });

        await Task.WhenAll(dumpTask, enqueueTask, ackTask).ConfigureAwait(false);

        await WaitForAcknowledgementsToClearAsync(connection, options.HeartbeatInterval).ConfigureAwait(false);

        var snapshot = connection.GetOutboxSnapshotForTesting();

        var expectedIds = new HashSet<Guid>(framesToDump.Select(frame => frame.Id));
        expectedIds.ExceptWith(framesToAck.Select(frame => frame.Id));
        expectedIds.UnionWith(concurrentFrames.Select(frame => frame.Id));

        snapshot.Keys.Should().BeEquivalentTo(expectedIds);
        connection.AcknowledgedEventIds.Should().BeEmpty();
    }

    [Test]
    public async Task OnAckReceived_DoesNotLeakAcknowledgementStates()
    {
        var options = new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(10),
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 12345);

        await using var connection = new ResilientOutboundConnection(options, sessionKey);

        var frames = Enumerable.Range(0, 256)
            .Select(index => SessionFrame.CreateEventFrame(Guid.NewGuid(), $"frame-{index}"))
            .ToList();

        foreach (var frame in frames)
        {
            connection.EnqueueFrame(frame);
            connection.OnAckReceived(frame.Id);
        }

        await WaitForAcknowledgementsToClearAsync(connection, options.HeartbeatInterval).ConfigureAwait(false);
        connection.AcknowledgedEventIds.Should().BeEmpty();
    }

    private static async Task WaitForAcknowledgementsToClearAsync(
        ResilientOutboundConnection connection,
        TimeSpan heartbeatInterval)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < timeout)
        {
            if (connection.AcknowledgedEventIds.IsEmpty)
            {
                return;
            }

            await Task.Delay(heartbeatInterval).ConfigureAwait(false);
        }

        connection.AcknowledgedEventIds.Should().BeEmpty();
    }
}
#endif
