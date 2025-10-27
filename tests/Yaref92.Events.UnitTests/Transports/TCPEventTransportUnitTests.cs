using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

    [Test]
    public async Task PublishAsync_WhenWriteFails_RaisesPublishFailureAndRemovesClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        using var transport = new TCPEventTransport(0);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var publishFailureTcs = new TaskCompletionSource<(EndPoint? Endpoint, Exception Exception)>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PublishFailure += (endpoint, exception) => publishFailureTcs.TrySetResult((endpoint, exception));

        var connectTask = transport.ConnectToPeerAsync(IPAddress.Loopback.ToString(), port);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask.ConfigureAwait(false);

        try
        {
            serverClient.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            serverClient.Close();

            Func<Task> act = () => transport.PublishAsync(new DummyEvent());
            var aggregateException = await act.Should().ThrowAsync<AggregateException>();
            aggregateException.Which.InnerExceptions.Should().NotBeEmpty();

            var completed = await Task.WhenAny(publishFailureTcs.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            completed.Should().Be(publishFailureTcs.Task);
            var failure = await publishFailureTcs.Task.ConfigureAwait(false);
            failure.Endpoint.Should().NotBeNull();
            failure.Exception.Should().NotBeNull();

            var clientsField = typeof(TCPEventTransport).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
            var clients = (ConcurrentDictionary<TcpClient, byte>)clientsField!.GetValue(transport)!;
            clients.Should().BeEmpty();
        }
        finally
        {
            serverClient.Dispose();
        }
    }

    [Test]
    public async Task PublishAsync_WhenMultipleClientsFail_AggregatesAllExceptions()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        using var transport = new TCPEventTransport(0);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connectTask1 = transport.ConnectToPeerAsync(IPAddress.Loopback.ToString(), port);
        var serverClient1 = await listener.AcceptTcpClientAsync();
        await connectTask1.ConfigureAwait(false);

        var connectTask2 = transport.ConnectToPeerAsync(IPAddress.Loopback.ToString(), port);
        var serverClient2 = await listener.AcceptTcpClientAsync();
        await connectTask2.ConfigureAwait(false);

        try
        {
            serverClient1.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            serverClient1.Close();
            serverClient2.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            serverClient2.Close();

            var failureCount = 0;
            transport.PublishFailure += (_, __) => Interlocked.Increment(ref failureCount);

            Func<Task> act = () => transport.PublishAsync(new DummyEvent());
            var aggregateException = await act.Should().ThrowAsync<AggregateException>();
            aggregateException.Which.InnerExceptions.Should().HaveCount(2);

            SpinWait.SpinUntil(() => Volatile.Read(ref failureCount) == 2, TimeSpan.FromSeconds(5)).Should().BeTrue();

            var clientsField = typeof(TCPEventTransport).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
            var clients = (ConcurrentDictionary<TcpClient, byte>)clientsField!.GetValue(transport)!;
            clients.Should().BeEmpty();
        }
        finally
        {
            serverClient1.Dispose();
            serverClient2.Dispose();
        }
    }

    private class TcpEventEnvelope
    {
        public string? TypeName { get; set; }
        public string? Json { get; set; }
    }
}
