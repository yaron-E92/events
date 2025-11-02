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

            await using (var writer = new ResilientSessionClient(Guid.NewGuid(), "localhost", 12345))
            {
                ResilientSessionClientTestHelper.OverrideOutboxPath(writer, outboxPath);
                firstId = await writer.EnqueueEventAsync("first", CancellationToken.None).ConfigureAwait(false);
                secondId = await writer.EnqueueEventAsync("second", CancellationToken.None).ConfigureAwait(false);
                await ResilientSessionClientTestHelper.PersistOutboxAsync(writer, CancellationToken.None).ConfigureAwait(false);
            }

            await using var reader = new ResilientSessionClient(Guid.NewGuid(), "localhost", 12345);
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
        await using var client = new ResilientSessionClient(
            "localhost",
            12345,
            (_, _, _) => Task.CompletedTask,
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
        await using var client = new ResilientSessionClient(
            "localhost",
            12345,
            (_, _, _) => Task.CompletedTask,
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
    private static readonly FieldInfo OutboxPathField = typeof(ResilientSessionClient).GetField("_outboxPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo OutboxEntriesField = typeof(ResilientSessionClient).GetField("_outboxEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LastRemoteActivityField = typeof(ResilientSessionClient).GetField("_lastRemoteActivityTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PersistOutboxMethod = typeof(ResilientSessionClient).GetMethod("PersistOutboxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo LoadOutboxMethod = typeof(ResilientSessionClient).GetMethod("LoadOutboxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo HeartbeatLoopMethod = typeof(ResilientSessionClient).GetMethod("RunHeartbeatLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo BackoffDelayMethod = typeof(ResilientSessionClient).GetMethod("GetBackoffDelay", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo NotifySendFailureMethod = typeof(ResilientSessionClient).GetMethod("NotifySendFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Type OutboxEntryType = typeof(ResilientSessionClient).GetNestedType("OutboxEntry", BindingFlags.NonPublic)!;
    private static readonly PropertyInfo PayloadProperty = OutboxEntryType.GetProperty("Payload", BindingFlags.Instance | BindingFlags.Public)!;

    public static void OverrideOutboxPath(ResilientSessionClient client, string path)
    {
        OutboxPathField.SetValue(client, path);
    }

    public static async Task PersistOutboxAsync(ResilientSessionClient client, CancellationToken cancellationToken)
    {
        var task = (Task)PersistOutboxMethod.Invoke(client, new object[] { cancellationToken })!;
        await task.ConfigureAwait(false);
    }

    public static async Task LoadOutboxAsync(ResilientSessionClient client, CancellationToken cancellationToken)
    {
        var task = (Task)LoadOutboxMethod.Invoke(client, new object[] { cancellationToken })!;
        await task.ConfigureAwait(false);
    }

    public static Dictionary<Guid, string> GetOutboxSnapshot(ResilientSessionClient client)
    {
        var entries = (System.Collections.IDictionary)OutboxEntriesField.GetValue(client)!;
        var snapshot = new Dictionary<Guid, string>();
        foreach (var key in entries.Keys)
        {
            if (key is not Guid id)
            {
                continue;
            }

            var entry = entries[id];
            var payload = (string)PayloadProperty.GetValue(entry)!;
            snapshot[id] = payload;
        }

        return snapshot;
    }

    public static void SetLastRemoteActivity(ResilientSessionClient client, DateTime timestamp)
    {
        LastRemoteActivityField.SetValue(client, timestamp.Ticks);
    }

    public static Task RunHeartbeatLoopAsync(ResilientSessionClient client, CancellationToken cancellationToken)
    {
        return (Task)HeartbeatLoopMethod.Invoke(client, new object[] { cancellationToken })!;
    }

    public static TimeSpan GetBackoffDelay(ResilientSessionClient client, int attempt)
    {
        return (TimeSpan)BackoffDelayMethod.Invoke(client, new object[] { attempt })!;
    }

    public static void NotifySendFailure(ResilientSessionClient client, Exception exception)
    {
        NotifySendFailureMethod.Invoke(client, new object[] { exception });
    }
}
