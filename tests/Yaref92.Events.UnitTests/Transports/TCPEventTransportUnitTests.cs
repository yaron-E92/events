using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class TCPEventTransportUnitTests
{
    [Test]
    public async Task PublishEventAsync_SerializesPayload_AndBroadcasts()
    {
        var inboundManager = new FakeInboundConnectionManager();
        var outboundManager = new FakeOutboundConnectionManager();
        var serializer = new FakeEventSerializer("serialized-payload");
        await using var transport = CreateTransport(inboundManager, outboundManager, serializer: serializer);

        var domainEvent = new DummyEvent();
        await transport.PublishEventAsync(domainEvent).ConfigureAwait(false);

        serializer.SerializedEvents.Should().ContainSingle().Which.Should().Be(domainEvent);
        outboundManager.Broadcasts.Should().ContainSingle(b => b.EventId == domainEvent.EventId && b.Payload == "serialized-payload");
    }

    [Test]
    public async Task AckReceived_ForwardsToOutboundConnectionManager()
    {
        var inboundManager = new FakeInboundConnectionManager();
        var outboundManager = new FakeOutboundConnectionManager();
        await using var transport = CreateTransport(inboundManager, outboundManager);

        var sessionKey = new SessionKey(Guid.NewGuid(), "localhost", 1234);
        var eventId = Guid.NewGuid();
        await inboundManager.RaiseAckReceivedAsync(eventId, sessionKey).ConfigureAwait(false);

        outboundManager.AckNotifications.Should().ContainSingle(call => call.EventId == eventId && call.SessionKey == sessionKey);
    }

    [Test]
    public async Task PingReceived_SendsPongThroughPublisher()
    {
        var inboundManager = new FakeInboundConnectionManager();
        var outboundManager = new FakeOutboundConnectionManager();
        await using var transport = CreateTransport(inboundManager, outboundManager);

        var sessionKey = new SessionKey(Guid.NewGuid(), "remote", 5678);
        await inboundManager.RaisePingReceivedAsync(sessionKey).ConfigureAwait(false);

        outboundManager.Pongs.Should().ContainSingle(item => item.Equals(sessionKey));
    }

    [Test]
    public async Task EventReceived_WhenHandlerCompletes_AcknowledgesEvent()
    {
        var inboundManager = new FakeInboundConnectionManager();
        var outboundManager = new FakeOutboundConnectionManager();
        var publisher = new FakePersistentFramePublisher(outboundManager);
        await using var transport = CreateTransport(inboundManager, outboundManager, publisher);

        var sessionKey = new SessionKey(Guid.NewGuid(), "peer", 9000);
        var domainEvent = new DummyEvent();
        ((IEventTransport)transport).EventReceived += _ => Task.FromResult(true);

        await inboundManager.RaiseEventReceivedAsync(domainEvent, sessionKey).ConfigureAwait(false);

        publisher.Acknowledgements.Should().ContainSingle(a => a.EventId == domainEvent.EventId && a.SessionKey == sessionKey);
    }

    [Test]
    public async Task SessionInboundConnectionDropped_AttemptsReconnect()
    {
        var inboundManager = new FakeInboundConnectionManager();
        var outboundManager = new FakeOutboundConnectionManager();
        await using var transport = CreateTransport(inboundManager, outboundManager);

        var sessionKey = new SessionKey(Guid.NewGuid(), "peer", 5555);
        await inboundManager.RaiseSessionDroppedAsync(sessionKey).ConfigureAwait(false);

        outboundManager.ReconnectAttempts.Should().ContainSingle(attempt => attempt.SessionKey == sessionKey);
    }

    [Test]
    public void Serialization_Envelope_RoundTrip_Works()
    {
        DummyEvent dummy = new();
        string? typeName = typeof(DummyEvent).AssemblyQualifiedName;
        string json = JsonSerializer.Serialize(dummy, dummy.GetType());
        var envelope = new { EventId = Guid.NewGuid(), TypeName = typeName, EventJson = json };
        string payload = JsonSerializer.Serialize(envelope);

        TcpEventEnvelope? deserialized = JsonSerializer.Deserialize<TcpEventEnvelope>(payload);
        Type? returnType = Type.GetType(deserialized!.TypeName!);
        object? evt = JsonSerializer.Deserialize(deserialized!.EventJson!, returnType!);

        evt.Should().BeOfType<DummyEvent>();
    }

    private static TCPEventTransport CreateTransport(
        FakeInboundConnectionManager? inbound = null,
        FakeOutboundConnectionManager? outbound = null,
        FakePersistentFramePublisher? publisher = null,
        FakeEventSerializer? serializer = null)
    {
        inbound ??= new FakeInboundConnectionManager();
        outbound ??= new FakeOutboundConnectionManager();
        serializer ??= new FakeEventSerializer("payload");
        var listener = new FakePortListener(inbound);
        var publisherInstance = publisher ?? new FakePersistentFramePublisher(outbound);
        return new TCPEventTransport(listener, publisherInstance, serializer);
    }

    private sealed class FakeEventSerializer : IEventSerializer
    {
        private readonly string _payload;

        internal FakeEventSerializer(string payload)
        {
            _payload = payload;
        }

        public List<IDomainEvent> SerializedEvents { get; } = new();

        public string Serialize<T>(T evt) where T : class, IDomainEvent
        {
            SerializedEvents.Add(evt);
            return _payload;
        }

        public (Type? type, IDomainEvent? domainEvent) Deserialize(string data) => (null, null);
    }

    private sealed class FakePersistentFramePublisher : IPersistentFramePublisher
    {
        public FakePersistentFramePublisher(FakeOutboundConnectionManager outbound)
        {
            ConnectionManager = outbound;
        }

        public List<(Guid EventId, SessionKey SessionKey)> Acknowledgements { get; } = new();

        public FakeOutboundConnectionManager ConnectionManager { get; }

        IOutboundConnectionManager IPersistentFramePublisher.ConnectionManager => ConnectionManager;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void AcknowledgeEventReceipt(Guid eventId, SessionKey sessionKey)
        {
            Acknowledgements.Add((eventId, sessionKey));
            ConnectionManager.SendAck(eventId, sessionKey);
        }

        public Task PublishToAllAsync(Guid eventId, string eventEnvelopePayload, CancellationToken cancellationToken)
        {
            ConnectionManager.QueueEventBroadcast(eventId, eventEnvelopePayload);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePortListener : IPersistentPortListener
    {
        public FakePortListener(FakeInboundConnectionManager inboundConnectionManager)
        {
            ConnectionManager = inboundConnectionManager;
        }

        public IInboundConnectionManager ConnectionManager { get; }

        public int Port => 0;

        public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;

        event IEventTransport.SessionInboundConnectionDroppedHandler? IPersistentPortListener.SessionInboundConnectionDropped
        {
            add => ConnectionManager.SessionInboundConnectionDropped += value;
            remove => ConnectionManager.SessionInboundConnectionDropped -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task RaiseSessionAcceptedAsync(SessionKey sessionKey, CancellationToken token)
        {
            return SessionConnectionAccepted?.Invoke(sessionKey, token) ?? Task.CompletedTask;
        }
    }

    private sealed class FakeInboundConnectionManager : IInboundConnectionManager
    {
        public SessionManager SessionManager { get; } = new(0, new ResilientSessionOptions());

        public event Func<IDomainEvent, SessionKey, Task>? EventReceived;
        public event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;
        public event Func<Guid, SessionKey, Task>? AckReceived;
        public event Func<SessionKey, Task>? PingReceived;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ConnectionInitializationResult> HandleIncomingTransientConnectionAsync(TcpClient incomingTransientConnection, CancellationToken serverToken)
        {
            return Task.FromResult(ConnectionInitializationResult.Failed());
        }

        public Task RaiseEventReceivedAsync(IDomainEvent domainEvent, SessionKey sessionKey)
        {
            return EventReceived?.Invoke(domainEvent, sessionKey) ?? Task.CompletedTask;
        }

        public Task RaiseAckReceivedAsync(Guid eventId, SessionKey sessionKey)
        {
            return AckReceived?.Invoke(eventId, sessionKey) ?? Task.CompletedTask;
        }

        public Task RaisePingReceivedAsync(SessionKey sessionKey)
        {
            return PingReceived?.Invoke(sessionKey) ?? Task.CompletedTask;
        }

        public Task<bool> RaiseSessionDroppedAsync(SessionKey sessionKey, CancellationToken cancellationToken = default)
        {
            return SessionInboundConnectionDropped?.Invoke(sessionKey, cancellationToken) ?? Task.FromResult(false);
        }
    }

    private sealed class FakeOutboundConnectionManager : IOutboundConnectionManager
    {
        public SessionManager SessionManager { get; } = new(0, new ResilientSessionOptions());

        public List<(Guid EventId, string Payload)> Broadcasts { get; } = new();
        public List<(Guid EventId, SessionKey SessionKey)> SentAcks { get; } = new();
        public List<(Guid EventId, SessionKey SessionKey)> AckNotifications { get; } = new();
        public List<SessionKey> Pongs { get; } = new();
        public List<(SessionKey SessionKey, CancellationToken Token)> ReconnectAttempts { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ConnectAsync(SessionKey sessionKey, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void QueueEventBroadcast(Guid eventId, string eventEnvelopeJson)
        {
            Broadcasts.Add((eventId, eventEnvelopeJson));
        }

        public Task<bool> TryReconnectAsync(SessionKey sessionKey, CancellationToken token)
        {
            ReconnectAttempts.Add((sessionKey, token));
            return Task.FromResult(true);
        }

        public void SendAck(Guid eventId, SessionKey sessionKey)
        {
            SentAcks.Add((eventId, sessionKey));
        }

        public Task OnAckReceived(Guid eventId, SessionKey sessionKey)
        {
            AckNotifications.Add((eventId, sessionKey));
            return Task.CompletedTask;
        }

        public void SendPong(SessionKey sessionKey)
        {
            Pongs.Add(sessionKey);
        }
    }

    private sealed class TcpEventEnvelope
    {
        public Guid EventId { get; set; }
        public string? TypeName { get; set; }
        public string? EventJson { get; set; }
    }
}
