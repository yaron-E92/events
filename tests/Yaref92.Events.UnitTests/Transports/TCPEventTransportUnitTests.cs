using System.Text.Json;

using FluentAssertions;

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

    private class TcpEventEnvelope
    {
        public string? TypeName { get; set; }
        public string? Json { get; set; }
    }
}
