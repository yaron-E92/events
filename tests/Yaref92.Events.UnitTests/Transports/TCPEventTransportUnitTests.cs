using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Yaref92.Events;
using Yaref92.Events.Abstractions;
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
        var aggregator = new EventAggregator();
        using var transport = new TestableTCPEventTransport(aggregator);

        var publishFailureTcs = new TaskCompletionSource<PublishFailed>(TaskCreationOptions.RunContinuationsAsynchronously);
        aggregator.SubscribeToEventType(new CapturePublishFailedSubscriber(publishFailureTcs));

        var (failingClient, serverClient) = await CreateConnectedClientPairAsync().ConfigureAwait(false);
        var endpoint = failingClient.Client.RemoteEndPoint;
        transport.AddFailingClient(failingClient, new IOException("Simulated write failure."));

        GetClientsDictionary(transport).Count.Should().Be(1, "the tracked client should still be present before publishing");

        try
        {
            Func<Task> act = () => transport.PublishAsync(new DummyEvent());
            var aggregateException = await act.Should().ThrowAsync<AggregateException>();
            aggregateException.Which.InnerExceptions.Should().ContainSingle();

            var failure = await publishFailureTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            failure.Endpoint.Should().Be(endpoint);
            failure.Exception.Should().BeOfType<IOException>();

            GetClientsDictionary(transport).Should().BeEmpty();
        }
        finally
        {
            serverClient.Dispose();
        }
    }

    [Test]
    public async Task PublishAsync_WhenMultipleClientsFail_AggregatesAllExceptions()
    {
        var aggregator = new EventAggregator();
        using var transport = new TestableTCPEventTransport(aggregator);

        var (failingClient1, serverClient1) = await CreateConnectedClientPairAsync().ConfigureAwait(false);
        var (failingClient2, serverClient2) = await CreateConnectedClientPairAsync().ConfigureAwait(false);

        var endpoint1 = failingClient1.Client.RemoteEndPoint;
        var endpoint2 = failingClient2.Client.RemoteEndPoint;

        transport.AddFailingClient(failingClient1, new IOException("First write failure."));
        transport.AddFailingClient(failingClient2, new IOException("Second write failure."));

        var publishFailures = new CountingPublishFailedSubscriber(expectedCount: 2);
        aggregator.SubscribeToEventType(publishFailures);

        GetClientsDictionary(transport).Count.Should().Be(2, "both clients should be tracked before the publish attempt");

        try
        {
            Func<Task> act = () => transport.PublishAsync(new DummyEvent());
            var aggregateException = await act.Should().ThrowAsync<AggregateException>();
            aggregateException.Which.InnerExceptions.Should().HaveCount(2);

            await publishFailures.WaitForCountAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            publishFailures.Failures.Should().HaveCount(2);
            publishFailures.Failures.Select(f => f.Endpoint).Should().BeEquivalentTo(new[] { endpoint1, endpoint2 });
            publishFailures.Failures.Should().AllSatisfy(f => f.Exception.Should().BeOfType<IOException>());

            GetClientsDictionary(transport).Should().BeEmpty();
        }
        finally
        {
            serverClient1.Dispose();
            serverClient2.Dispose();
        }
    }

    private static ConcurrentDictionary<TcpClient, byte> GetClientsDictionary(TCPEventTransport transport)
    {
        var clientsField = typeof(TCPEventTransport).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ConcurrentDictionary<TcpClient, byte>)clientsField!.GetValue(transport)!;
    }

    private static async Task<(TcpClient Client, TcpClient Server)> CreateConnectedClientPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();

            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            var server = await acceptTask.ConfigureAwait(false);

            return (client, server);
        }
        finally
        {
            listener.Stop();
        }
    }

    private class TcpEventEnvelope
    {
        public string? TypeName { get; set; }
        public string? Json { get; set; }
    }

    private sealed class TestableTCPEventTransport : TCPEventTransport
    {
        private readonly ConcurrentDictionary<TcpClient, Exception> _failures = new();

        public TestableTCPEventTransport(IEventAggregator eventAggregator)
            : base(0, eventAggregator: eventAggregator)
        {
        }

        public void AddFailingClient(TcpClient client, Exception exception)
        {
            if (client is null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            var clientsField = typeof(TCPEventTransport).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
            var clients = (ConcurrentDictionary<TcpClient, byte>)clientsField!.GetValue(this)!;
            if (!clients.TryAdd(client, 0))
            {
                throw new InvalidOperationException("The TCP client is already tracked by the transport.");
            }

            _failures[client] = exception;
        }

        protected Task WriteToClientAsync(
            TcpClient client,
            ReadOnlyMemory<byte> lengthPrefix,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            if (_failures.TryGetValue(client, out var exception))
            {
                throw exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CapturePublishFailedSubscriber : IEventSubscriber<PublishFailed>
    {
        private readonly TaskCompletionSource<PublishFailed> _tcs;

        public CapturePublishFailedSubscriber(TaskCompletionSource<PublishFailed> tcs)
        {
            _tcs = tcs;
        }

        public void OnNext(PublishFailed domainEvent)
        {
            _tcs.TrySetResult(domainEvent);
        }
    }

    private sealed class CountingPublishFailedSubscriber : IEventSubscriber<PublishFailed>
    {
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedCount;
        private readonly ConcurrentBag<PublishFailed> _failures = new();
        private int _count;

        public CountingPublishFailedSubscriber(int expectedCount)
        {
            _expectedCount = expectedCount;
        }

        public IReadOnlyCollection<PublishFailed> Failures => _failures.ToArray();

        public void OnNext(PublishFailed domainEvent)
        {
            _failures.Add(domainEvent);
            if (Interlocked.Increment(ref _count) >= _expectedCount)
            {
                _tcs.TrySetResult(true);
            }
        }

        public async Task WaitForCountAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
    }
}
