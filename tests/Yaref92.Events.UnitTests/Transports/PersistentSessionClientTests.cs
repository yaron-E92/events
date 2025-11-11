using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using NUnit.Framework;

using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class ResilientSessionClientTests
{
    [Test]
    public async Task Outbox_Persistence_RoundTrips_PendingEntries()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"psc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var outboxPath = Path.Combine(tempDirectory, "outbox.json");

        try
        {
            Guid firstId;
            Guid secondId;

            Guid userId = Guid.NewGuid();
            await using (var writer = new ResilientSessionConnection(userId, "localhost", 12345))
            {
                ResilientSessionClientTestHelper.OverrideOutboxPath(writer, outboxPath);
                firstId = await writer.EnqueueEventAsync("first", CancellationToken.None).ConfigureAwait(false);
                secondId = await writer.EnqueueEventAsync("second", CancellationToken.None).ConfigureAwait(false);
                await ResilientSessionClientTestHelper.PersistOutboxAsync(writer, CancellationToken.None).ConfigureAwait(false);
            }

            await using var reader = new ResilientSessionConnection(userId, "localhost", 12345);
            ResilientSessionClientTestHelper.OverrideOutboxPath(reader, outboxPath);
            await ResilientSessionClientTestHelper.LoadOutboxAsync(reader, CancellationToken.None).ConfigureAwait(false);

            var snapshot = ResilientSessionClientTestHelper.GetOutboxSnapshot(reader);
            snapshot.Should().ContainKey(firstId).WhoseValue.Should().Be("first");
            snapshot.Should().ContainKey(secondId).WhoseValue.Should().Be("second");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task HeartbeatLoop_Throws_When_Remote_Is_Inactive()
    {
        await using var client = new ResilientSessionConnection(
            "localhost",
            12345,
            (_, _, _) => ValueTask.CompletedTask,
            new ResilientSessionOptions
            {
                HeartbeatInterval = TimeSpan.Zero,
                HeartbeatTimeout = TimeSpan.FromMilliseconds(10),
            });

        ResilientSessionClientTestHelper.SetLastRemoteActivity(client, DateTime.UtcNow - TimeSpan.FromSeconds(5));

        Func<Task> act = () => ResilientSessionClientTestHelper.RunHeartbeatLoopAsync(client, CancellationToken.None);
        await act.Should().ThrowAsync<IOException>().ConfigureAwait(false);
    }

    [Test]
    public async Task BackoffDelay_GrowsExponentially_And_Respects_Maximum()
    {
        await using var client = new ResilientSessionConnection(
            "localhost",
            12345,
            (_, _, _) => ValueTask.CompletedTask,
            new ResilientSessionOptions
            {
                BackoffInitialDelay = TimeSpan.FromMilliseconds(5),
                BackoffMaxDelay = TimeSpan.FromMilliseconds(40),
            });

        var attempt1 = ResilientSessionClientTestHelper.GetBackoffDelay(client, 1);
        var attempt2 = ResilientSessionClientTestHelper.GetBackoffDelay(client, 2);
        var attempt5 = ResilientSessionClientTestHelper.GetBackoffDelay(client, 5);

        attempt1.Should().Be(TimeSpan.FromMilliseconds(5));
        attempt2.Should().Be(TimeSpan.FromMilliseconds(10));
        attempt5.Should().Be(TimeSpan.FromMilliseconds(40));
    }
}

internal static class ResilientSessionClientTestHelper
{
    public static void OverrideOutboxPath(ResilientSessionConnection client, string path)
    {
        client.SetOutboxPathForTesting(path);
    }

    public static Task PersistOutboxAsync(ResilientSessionConnection client, CancellationToken cancellationToken)
    {
        return client.PersistOutboxForTestingAsync(cancellationToken);
    }

    public static Task LoadOutboxAsync(ResilientSessionConnection client, CancellationToken cancellationToken)
    {
        return client.LoadOutboxForTestingAsync(cancellationToken);
    }

    public static Dictionary<Guid, string> GetOutboxSnapshot(ResilientSessionConnection client)
    {
        return new Dictionary<Guid, string>(client.GetOutboxSnapshotForTesting());
    }

    public static void SetLastRemoteActivity(ResilientSessionConnection client, DateTime timestamp)
    {
        client.SetLastRemoteActivityForTesting(timestamp);
    }

    public static Task RunHeartbeatLoopAsync(ResilientSessionConnection client, CancellationToken cancellationToken)
    {
        return client.RunHeartbeatLoopForTestingAsync(cancellationToken);
    }

    public static TimeSpan GetBackoffDelay(ResilientSessionConnection client, int attempt)
    {
        return client.GetBackoffDelayForTesting(attempt);
    }

    public static void NotifySendFailure(ResilientSessionConnection client, Exception exception)
    {
        client.NotifySendFailureForTesting(exception);
    }
}
