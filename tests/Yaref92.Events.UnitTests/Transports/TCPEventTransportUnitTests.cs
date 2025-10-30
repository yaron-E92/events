using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;
using NUnit.Framework;

using Yaref92.Events.Transports;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class TCPEventTransportUnitTests
{
    [Test]
    public void Subscribe_RegistersHandler_And_InvokesIt()
    {
        // Arrange
        var transport = new TCPEventTransport(0); // Port 0 for no listening
        DummyEvent? received = null;
        transport.Subscribe<DummyEvent>(async (evt, ct) => received = evt);

        // Act
        var handlersField = typeof(TCPEventTransport).GetField("_handlers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handlers = handlersField!.GetValue(transport) as System.Collections.Concurrent.ConcurrentDictionary<Type, System.Collections.Concurrent.ConcurrentBag<Func<object, CancellationToken, Task>>>;
        var bag = handlers![typeof(DummyEvent)];
        DummyEvent dummy = new();
        foreach (var h in bag) h(dummy, CancellationToken.None).Wait();

        // Assert
        received.Should().NotBeNull();
    }

    [Test]
    public void Serialization_Envelope_RoundTrip_Works()
    {
        // Arrange
        DummyEvent dummy = new();
        string? typeName = typeof(DummyEvent).AssemblyQualifiedName;
        string json = JsonSerializer.Serialize(dummy, dummy.GetType());
        var envelope = new { TypeName = typeName, Json = json };
        string payload = JsonSerializer.Serialize(envelope);

        // Act
        TcpEventEnvelope? deserialized = JsonSerializer.Deserialize<TcpEventEnvelope>(payload);
        Type? returnType = Type.GetType(deserialized!.TypeName!);
        object? evt = JsonSerializer.Deserialize(deserialized!.Json!, returnType!);

        // Assert
        evt.Should().BeOfType<DummyEvent>();
    }

    [Test]
    public async Task PublishAsync_WithPersistentSessions_EnqueuesPayload_ForEachSession()
    {
        // Arrange
        using var transport = new TCPEventTransport(0);
        var sessions = GetPersistentSessionsDictionary(transport);

        var tempDirectory = CreateTempDirectory();
        try
        {
            var sessionA = CreateSession(tempDirectory);
            var sessionB = CreateSession(tempDirectory);
            sessions.TryAdd("peer-a", sessionA);
            sessions.TryAdd("peer-b", sessionB);

            // Act
            await transport.PublishAsync(new DummyEvent()).ConfigureAwait(false);

            // Assert
            var snapshotA = PersistentSessionClientTestHelper.GetOutboxSnapshot(sessionA);
            var snapshotB = PersistentSessionClientTestHelper.GetOutboxSnapshot(sessionB);

            snapshotA.Should().HaveCount(1);
            snapshotB.Should().HaveCount(1);
            snapshotA.Values.Single().Should().Be(snapshotB.Values.Single());
        }
        finally
        {
            await DisposeSessionsAsync(sessions.Values).ConfigureAwait(false);
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Test]
    public async Task PublishAsync_WhenPersistentSessionThrows_PropagatesException()
    {
        using var transport = new TCPEventTransport(0);
        var sessions = GetPersistentSessionsDictionary(transport);

        var tempDirectory = CreateTempDirectory();
        try
        {
            var session = CreateSession(tempDirectory);
            sessions.TryAdd("peer", session);
            await session.DisposeAsync().ConfigureAwait(false);

            Func<Task> act = () => transport.PublishAsync(new DummyEvent());
            await act.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);

            sessions.Should().ContainKey("peer");
        }
        finally
        {
            await DisposeSessionsAsync(sessions.Values).ConfigureAwait(false);
            DeleteTempDirectory(tempDirectory);
        }
    }

    private static ConcurrentDictionary<string, PersistentSessionClient> GetPersistentSessionsDictionary(TCPEventTransport transport)
    {
        var field = typeof(TCPEventTransport).GetField("_persistentSessions", BindingFlags.Instance | BindingFlags.NonPublic);
        return (ConcurrentDictionary<string, PersistentSessionClient>)field!.GetValue(transport)!;
    }

    private static PersistentSessionClient CreateSession(string tempDirectory)
    {
        var session = new PersistentSessionClient("localhost", 12345, (_, _, _) => Task.CompletedTask);
        var path = Path.Combine(tempDirectory, $"outbox-{Guid.NewGuid():N}.json");
        PersistentSessionClientTestHelper.OverrideOutboxPath(session, path);
        return session;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // ignore cleanup failures in tests
        }
    }

    private static async Task DisposeSessionsAsync(IEnumerable<PersistentSessionClient> sessions)
    {
        foreach (var session in sessions.ToArray())
        {
            if (session is null)
            {
                continue;
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private class TcpEventEnvelope
    {
        public string? TypeName { get; set; }
        public string? Json { get; set; }
    }

}
