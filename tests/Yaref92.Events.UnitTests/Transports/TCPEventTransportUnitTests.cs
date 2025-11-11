using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;
using NUnit.Framework;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class TCPEventTransportUnitTests
{
    [Test]
    public async Task Subscribe_RegistersHandler_And_PublishesInnerEvent()
    {
        // Arrange
        var aggregator = new FakeEventAggregator();
        await using var transport = new TCPEventTransport(0, eventAggregator: aggregator);
        transport.Subscribe<DummyEvent>();

        // Act
        var handler = aggregator.GetAsyncSubscriber<EventReceived<DummyEvent>>();
        handler.Should().NotBeNull();

        var dummy = new DummyEvent();
        await handler!.OnNextAsync(new EventReceived<DummyEvent>(DateTime.UtcNow, dummy), CancellationToken.None).ConfigureAwait(false);

        // Assert
        aggregator.PublishedEvents.OfType<DummyEvent>().Should().ContainSingle().Which.Should().Be(dummy);
    }

    [Test]
    public void Serialization_Envelope_RoundTrip_Works()
    {
        // Arrange
        DummyEvent dummy = new();
        string? typeName = typeof(DummyEvent).AssemblyQualifiedName;
        string json = JsonSerializer.Serialize(dummy, dummy.GetType());
        var envelope = new { EventId = Guid.NewGuid(), TypeName = typeName, EventJson = json };
        string payload = JsonSerializer.Serialize(envelope);

        // Act
        TcpEventEnvelope? deserialized = JsonSerializer.Deserialize<TcpEventEnvelope>(payload);
        Type? returnType = Type.GetType(deserialized!.TypeName!);
        object? evt = JsonSerializer.Deserialize(deserialized!.EventJson!, returnType!);

        // Assert
        evt.Should().BeOfType<DummyEvent>();
    }

    [Test]
    public async Task PublishAsync_WithPersistentSessions_EnqueuesPayload_ForEachSession()
    {
        // Arrange
        await using var transport = new TCPEventTransport(0);
        ConcurrentDictionary<string, ResilientSessionConnection> sessions = GetPersistentSessionsDictionary(transport);

        var tempDirectory = CreateTempDirectory();
        try
        {
            var sessionA = CreateSession(tempDirectory);
            var sessionB = CreateSession(tempDirectory);
            sessions.TryAdd(sessionA.Key, sessionA);
            sessions.TryAdd(sessionB.Key, sessionB);

            // Act
            await transport.PublishAsync(new DummyEvent()).ConfigureAwait(false);

            // Assert
            var snapshotA = ResilientSessionClientTestHelper.GetOutboxSnapshot(sessionA.PersistentClient);
            var snapshotB = ResilientSessionClientTestHelper.GetOutboxSnapshot(sessionB.PersistentClient);

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
    public async Task ConnectAsync_WithProvidedUserId_ReusesExistingSession()
    {
        var aggregator = new FakeEventAggregator();
        await using var transport = new TCPEventTransport(0, eventAggregator: aggregator);
        var sessions = GetPersistentSessionsDictionary(transport);

        var tempDirectory = CreateTempDirectory();
        try
        {
            var userId = Guid.NewGuid();
            var sessionKey = new SessionKey(userId, "localhost", 23456);
            var session = CreateSession(tempDirectory, sessionKey);
            sessions.TryAdd(sessionKey, session);

            await transport.PublisherForTesting.ConnectAsync(userId, sessionKey.Host, sessionKey.Port, CancellationToken.None).ConfigureAwait(false);

            session.StartInvocationCount.Should().Be(1);
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
        await using var transport = new TCPEventTransport(0);
        ConcurrentDictionary<string, ResilientSessionConnection> sessions = GetPersistentSessionsDictionary(transport);

        var tempDirectory = CreateTempDirectory();
        try
        {
            var session = CreateSession(tempDirectory);
            var sessionKey = session.Key;
            sessions.TryAdd(sessionKey, session);
            await session.DisposeAsync().ConfigureAwait(false);
            transport.ConnectToPeerAsync("localhost", 12345).Wait();
            Func<Task> act = () => transport.PublishAsync(new DummyEvent());
            await act.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);

            sessions.Should().ContainKey(sessionKey);
        }
        finally
        {
            await DisposeSessionsAsync(sessions.Values).ConfigureAwait(false);
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Test]
    public async Task NotifySendFailure_PublishesEventToRegisteredSubscriber()
    {
        // Arrange
        var aggregator = new FakeEventAggregator();
        await using var transport = new TCPEventTransport(0, eventAggregator: aggregator);
        var session = new ResilientSessionConnection(
            Guid.NewGuid(),
            "localhost",
            12345,
            eventAggregator: aggregator);
        var exception = new IOException("boom");

        // Act
        ResilientSessionClientTestHelper.NotifySendFailure(session, exception);

        // Assert
        aggregator.PublishFailedHandlerExecuted.Should().BeTrue();
        aggregator.PublishedFailures.Should().ContainSingle();
        var failure = aggregator.PublishedFailures.Single();
        failure.Endpoint.Should().BeEquivalentTo(session.RemoteEndPoint);
        failure.Exception.Should().Be(exception);
    }

    [Test]
    public async Task Subscribe_MessageReceived_PublishesInnerEvent()
    {
        // Arrange
        var aggregator = new FakeEventAggregator();
        await using var transport = new TCPEventTransport(0, eventAggregator: aggregator);
        transport.Subscribe<MessageReceived>();

        // Act
        var handler = aggregator.GetAsyncSubscriber<EventReceived<MessageReceived>>();
        handler.Should().NotBeNull();

        var message = new MessageReceived("session", "payload");
        await handler!.OnNextAsync(new EventReceived<MessageReceived>(DateTime.UtcNow, message), CancellationToken.None).ConfigureAwait(false);

        // Assert
        aggregator.PublishedMessages.Should().ContainSingle().Which.Should().BeSameAs(message);
    }

    private static ConcurrentDictionary<SessionKey, IResilientPeerSession> GetPersistentSessionsDictionary(TCPEventTransport transport)
    {
        return transport.PublisherForTesting.SessionsForTesting;
    }

    private static TestPeerSession CreateSession(string tempDirectory, SessionKey? sessionKey = null)
    {
        var key = sessionKey ?? new SessionKey(Guid.NewGuid(), "localhost", 12345);
        var client = new ResilientSessionConnection(key, new ResilientSessionOptions());
        var path = Path.Combine(tempDirectory, $"outbox-{Guid.NewGuid():N}.json");
        ResilientSessionClientTestHelper.OverrideOutboxPath(client, path);
        return new TestPeerSession(key, client);
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

    private static async Task DisposeSessionsAsync(IEnumerable<IResilientPeerSession> sessions)
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

    private sealed class TestPeerSession : IResilientPeerSession
    {
        private readonly ResilientSessionConnection _client;

        public TestPeerSession(SessionKey sessionKey, ResilientSessionConnection client)
        {
            Key = sessionKey;
            _client = client;
        }

        public SessionKey Key { get; }

        public string SessionToken => _client.SessionToken;

        public ResilientSessionConnection PersistentClient => _client;

        public int StartInvocationCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartInvocationCount++;
            return Task.CompletedTask;
        }

        public Task PublishAsync(string payload, CancellationToken cancellationToken)
        {
            return _client.EnqueueEventAsync(payload, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _client.DisposeAsync();
        }
    }

    private sealed class FakeEventAggregator : IEventAggregator
    {
        private readonly List<IEventHandler> _subscribers = new();
        private readonly List<IAsyncEventHandler<PublishFailed>> _publishFailedSubscribers = new();
        private readonly List<IAsyncEventHandler<MessageReceived>> _messageReceivedSubscribers = new();
        private readonly Dictionary<Type, List<IEventHandler>> _asyncSubscribers = new();

        public bool PublishFailedHandlerExecuted { get; private set; }

        public List<PublishFailed> PublishedFailures { get; } = new();
        public List<MessageReceived> PublishedMessages { get; } = new();
        public List<IDomainEvent> PublishedEvents { get; } = new();

        public ISet<Type> EventTypes { get; } = new HashSet<Type>();

        public IReadOnlyCollection<IEventHandler> Subscribers => _subscribers;

        public bool RegisterEventType<T>() where T : class, IDomainEvent
        {
            return EventTypes.Add(typeof(T));
        }

        public void PublishEvent<T>(T domainEvent) where T : class, IDomainEvent
        {
            PublishedEvents.Add(domainEvent);
        }

        public void SubscribeToEventType<T>(IEventHandler<T> subscriber) where T : class, IDomainEvent
        {
            if (subscriber is null)
            {
                throw new ArgumentNullException(nameof(subscriber));
            }

            _subscribers.Add(subscriber);
        }

        public void SubscribeToEventType<T>(IAsyncEventHandler<T> subscriber) where T : class, IDomainEvent
        {
            if (subscriber is null)
            {
                throw new ArgumentNullException(nameof(subscriber));
            }

            if (!_asyncSubscribers.TryGetValue(typeof(T), out var subscribers))
            {
                subscribers = new List<IEventHandler>();
                _asyncSubscribers[typeof(T)] = subscribers;
            }

            subscribers.Add(subscriber);

            if (subscriber is IEventHandler eventSubscriber)
            {
                _subscribers.Add(eventSubscriber);
            }

            if (subscriber is IAsyncEventHandler<PublishFailed> publishFailedSubscriber)
            {
                _publishFailedSubscribers.Add(publishFailedSubscriber);
            }

            if (subscriber is IAsyncEventHandler<MessageReceived> messageReceivedSubscriber)
            {
                _messageReceivedSubscribers.Add(messageReceivedSubscriber);
            }
        }

        public void UnsubscribeFromEventType<T>(IEventHandler<T> subscriber) where T : class, IDomainEvent
        {
            if (subscriber is null)
            {
                throw new ArgumentNullException(nameof(subscriber));
            }

            _subscribers.Remove(subscriber);
        }

        public void UnsubscribeFromEventType<T>(IAsyncEventHandler<T> subscriber) where T : class, IDomainEvent
        {
            if (subscriber is null)
            {
                throw new ArgumentNullException(nameof(subscriber));
            }

            if (_asyncSubscribers.TryGetValue(typeof(T), out var subscribers))
            {
                subscribers.Remove(subscriber);
                if (subscribers.Count == 0)
                {
                    _asyncSubscribers.Remove(typeof(T));
                }
            }

            if (subscriber is IEventHandler eventSubscriber)
            {
                _subscribers.Remove(eventSubscriber);
            }

            if (subscriber is IAsyncEventHandler<PublishFailed> publishFailedSubscriber)
            {
                _publishFailedSubscribers.Remove(publishFailedSubscriber);
            }

            if (subscriber is IAsyncEventHandler<MessageReceived> messageReceivedSubscriber)
            {
                _messageReceivedSubscribers.Remove(messageReceivedSubscriber);
            }
        }

        public Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
        {
            if (domainEvent is PublishFailed publishFailed)
            {
                PublishedFailures.Add(publishFailed);

                var tasks = _publishFailedSubscribers.Select(async subscriber =>
                {
                    await subscriber.OnNextAsync(publishFailed, cancellationToken).ConfigureAwait(false);
                    PublishFailedHandlerExecuted = true;
                });

                return Task.WhenAll(tasks);
            }

            if (domainEvent is MessageReceived messageReceived)
            {
                PublishedMessages.Add(messageReceived);

                var tasks = _messageReceivedSubscribers
                    .Select(subscriber => subscriber.OnNextAsync(messageReceived, cancellationToken));

                return Task.WhenAll(tasks);
            }

            PublishedEvents.Add(domainEvent);
            return Task.CompletedTask;
        }

        public IAsyncEventHandler<T>? GetAsyncSubscriber<T>() where T : class, IDomainEvent
        {
            if (_asyncSubscribers.TryGetValue(typeof(T), out var subscribers))
            {
                return subscribers.OfType<IAsyncEventHandler<T>>().FirstOrDefault();
            }

            return null;
        }
    }

    private class TcpEventEnvelope
    {
        public Guid EventId { get; set; }
        public string? TypeName { get; set; }
        public string? EventJson { get; set; }
    }

}
