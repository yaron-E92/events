using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using NUnit.Framework;

using Yaref92.Events.Transports;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class PersistentSessionClientTests
{
    [Test]
    public async Task Outbox_Persistence_RoundTrips_PendingEntries()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"psc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var outboxPath = Path.Combine(tempDirectory, "outbox.json");

        try
        {
            long firstId;
            long secondId;

            await using (var writer = new PersistentSessionClient("localhost", 12345, (_, _, _) => Task.CompletedTask))
            {
                PersistentSessionClientTestHelper.OverrideOutboxPath(writer, outboxPath);
                firstId = await writer.EnqueueEventAsync("first", CancellationToken.None).ConfigureAwait(false);
                secondId = await writer.EnqueueEventAsync("second", CancellationToken.None).ConfigureAwait(false);
                await PersistentSessionClientTestHelper.PersistOutboxAsync(writer, CancellationToken.None).ConfigureAwait(false);
            }

            await using var reader = new PersistentSessionClient("localhost", 12345, (_, _, _) => Task.CompletedTask);
            PersistentSessionClientTestHelper.OverrideOutboxPath(reader, outboxPath);
            await PersistentSessionClientTestHelper.LoadOutboxAsync(reader, CancellationToken.None).ConfigureAwait(false);

            var snapshot = PersistentSessionClientTestHelper.GetOutboxSnapshot(reader);
            snapshot.Should().ContainKey(firstId).WhoseValue.Should().Be("first");
            snapshot.Should().ContainKey(secondId).WhoseValue.Should().Be("second");

            var nextMessageId = PersistentSessionClientTestHelper.GetNextMessageId(reader);
            nextMessageId.Should().BeGreaterThanOrEqualTo(secondId);
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
        await using var client = new PersistentSessionClient(
            "localhost",
            12345,
            (_, _, _) => Task.CompletedTask,
            new ResilientSessionOptions
            {
                HeartbeatInterval = TimeSpan.Zero,
                HeartbeatTimeout = TimeSpan.FromMilliseconds(10),
            });

        PersistentSessionClientTestHelper.SetLastRemoteActivity(client, DateTime.UtcNow - TimeSpan.FromSeconds(5));

        Func<Task> act = () => PersistentSessionClientTestHelper.RunHeartbeatLoopAsync(client, CancellationToken.None);
        await act.Should().ThrowAsync<IOException>().ConfigureAwait(false);
    }

    [Test]
    public async Task BackoffDelay_GrowsExponentially_And_Respects_Maximum()
    {
        await using var client = new PersistentSessionClient(
            "localhost",
            12345,
            (_, _, _) => Task.CompletedTask,
            new ResilientSessionOptions
            {
                BackoffInitialDelay = TimeSpan.FromMilliseconds(5),
                BackoffMaxDelay = TimeSpan.FromMilliseconds(40),
            });

        var attempt1 = PersistentSessionClientTestHelper.GetBackoffDelay(client, 1);
        var attempt2 = PersistentSessionClientTestHelper.GetBackoffDelay(client, 2);
        var attempt5 = PersistentSessionClientTestHelper.GetBackoffDelay(client, 5);

        attempt1.Should().Be(TimeSpan.FromMilliseconds(5));
        attempt2.Should().Be(TimeSpan.FromMilliseconds(10));
        attempt5.Should().Be(TimeSpan.FromMilliseconds(40));
    }
}

internal static class PersistentSessionClientTestHelper
{
    private static readonly FieldInfo OutboxPathField = typeof(PersistentSessionClient).GetField("_outboxPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo OutboxEntriesField = typeof(PersistentSessionClient).GetField("_outboxEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo NextMessageIdField = typeof(PersistentSessionClient).GetField("_nextMessageId", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LastRemoteActivityField = typeof(PersistentSessionClient).GetField("_lastRemoteActivityTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PersistOutboxMethod = typeof(PersistentSessionClient).GetMethod("PersistOutboxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo LoadOutboxMethod = typeof(PersistentSessionClient).GetMethod("LoadOutboxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo HeartbeatLoopMethod = typeof(PersistentSessionClient).GetMethod("RunHeartbeatLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo BackoffDelayMethod = typeof(PersistentSessionClient).GetMethod("GetBackoffDelay", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Type OutboxEntryType = typeof(PersistentSessionClient).GetNestedType("OutboxEntry", BindingFlags.NonPublic)!;
    private static readonly PropertyInfo PayloadProperty = OutboxEntryType.GetProperty("Payload", BindingFlags.Instance | BindingFlags.Public)!;

    public static void OverrideOutboxPath(PersistentSessionClient client, string path)
    {
        OutboxPathField.SetValue(client, path);
    }

    public static async Task PersistOutboxAsync(PersistentSessionClient client, CancellationToken cancellationToken)
    {
        var task = (Task)PersistOutboxMethod.Invoke(client, new object[] { cancellationToken })!;
        await task.ConfigureAwait(false);
    }

    public static async Task LoadOutboxAsync(PersistentSessionClient client, CancellationToken cancellationToken)
    {
        var task = (Task)LoadOutboxMethod.Invoke(client, new object[] { cancellationToken })!;
        await task.ConfigureAwait(false);
    }

    public static Dictionary<long, string> GetOutboxSnapshot(PersistentSessionClient client)
    {
        var entries = (System.Collections.IDictionary)OutboxEntriesField.GetValue(client)!;
        var snapshot = new Dictionary<long, string>();
        foreach (var key in entries.Keys)
        {
            if (key is not long id)
            {
                continue;
            }

            var entry = entries[id];
            var payload = (string)PayloadProperty.GetValue(entry)!;
            snapshot[id] = payload;
        }

        return snapshot;
    }

    public static long GetNextMessageId(PersistentSessionClient client)
    {
        return (long)NextMessageIdField.GetValue(client)!;
    }

    public static void SetLastRemoteActivity(PersistentSessionClient client, DateTime timestamp)
    {
        LastRemoteActivityField.SetValue(client, timestamp.Ticks);
    }

    public static Task RunHeartbeatLoopAsync(PersistentSessionClient client, CancellationToken cancellationToken)
    {
        return (Task)HeartbeatLoopMethod.Invoke(client, new object[] { cancellationToken })!;
    }

    public static TimeSpan GetBackoffDelay(PersistentSessionClient client, int attempt)
    {
        return (TimeSpan)BackoffDelayMethod.Invoke(client, new object[] { attempt })!;
    }
}
