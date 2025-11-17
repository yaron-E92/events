using System.Threading.Tasks;

using FluentAssertions;

using Yaref92.Events;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.IntegrationTests;

[TestFixture, Explicit("Integration test, requires open ports and async timing.")]
[Category("Integration")]
public class TCPEventTransportTests
{
    [Test]
    public async Task Event_Is_Transmitted_Between_Transports()
    {
        // Arrange
        int portA = 15000;
        int portB = 15001;

        var aggregatorA = new EventAggregator();
        var aggregatorB = new EventAggregator();

        var tcsA = new TaskCompletionSource<DummyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsB = new TaskCompletionSource<DummyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var transportA = new TCPEventTransport(portA);
        await using var transportB = new TCPEventTransport(portB);

        using NetworkedEventAggregator networkedEventAggregatorA = new(aggregatorA, transportA);
        networkedEventAggregatorA.RegisterEventType<DummyEvent>();
        using NetworkedEventAggregator networkedEventAggregatorB = new(aggregatorB, transportB);
        networkedEventAggregatorB.RegisterEventType<DummyEvent>();

        networkedEventAggregatorA.SubscribeToEventType(new TaskCompletionAsyncHandler<DummyEvent>(tcsA));
        networkedEventAggregatorB.SubscribeToEventType(new TaskCompletionAsyncHandler<DummyEvent>(tcsB));

        await transportA.StartListeningAsync();
        await transportB.StartListeningAsync();
        await transportA.ConnectToPeerAsync("localhost", portB);
        await transportB.ConnectToPeerAsync("localhost", portA);

        var evt1 = new DummyEvent();
        var evt2 = new DummyEvent();

        // Act
        await networkedEventAggregatorA.PublishEventAsync(evt1);
        await networkedEventAggregatorB.PublishEventAsync(evt2);

        // Assert
        (await Task.WhenAny(tcsB.Task, Task.Delay(2000))).Should().Be(tcsB.Task);
        (await Task.WhenAny(tcsA.Task, Task.Delay(2000))).Should().Be(tcsA.Task);
        tcsB.Task.Result.Should().NotBeNull();
        tcsA.Task.Result.Should().NotBeNull();
    }

    [Test]
    [Explicit("Integration test, requires open ports and async timing.")]
    public async Task Authenticated_Transports_Exchange_Acks_And_Pongs()
    {
        int portA = 16000;
        int portB = 16001;
        string authenticationToken = $"token-{Guid.NewGuid():N}";
        TimeSpan heartbeat = TimeSpan.FromMilliseconds(50);

        await using var transportA = new TCPEventTransport(portA, heartbeatInterval: heartbeat, authenticationToken: authenticationToken);
        await using var transportB = new TCPEventTransport(portB, heartbeatInterval: heartbeat, authenticationToken: authenticationToken);

        var receivedByA = new TaskCompletionSource<DummyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedByB = new TaskCompletionSource<DummyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackObservedAtA = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackObservedAtB = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingObservedAtA = new TaskCompletionSource<SessionKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingObservedAtB = new TaskCompletionSource<SessionKey>(TaskCreationOptions.RunContinuationsAsynchronously);

        ((IEventTransport) transportA).EventReceived += domainEvent =>
        {
            if (domainEvent is DummyEvent dummy)
            {
                receivedByA.TrySetResult(dummy);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };

        ((IEventTransport) transportB).EventReceived += domainEvent =>
        {
            if (domainEvent is DummyEvent dummy)
            {
                receivedByB.TrySetResult(dummy);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };

        var inboundA = ((IEventTransport) transportA).PersistentPortListener.ConnectionManager;
        inboundA.AckReceived += (eventId, _) =>
        {
            ackObservedAtA.TrySetResult(eventId);
            return Task.CompletedTask;
        };
        inboundA.PingReceived += sessionKey =>
        {
            pingObservedAtA.TrySetResult(sessionKey);
            return Task.CompletedTask;
        };

        var inboundB = ((IEventTransport) transportB).PersistentPortListener.ConnectionManager;
        inboundB.AckReceived += (eventId, _) =>
        {
            ackObservedAtB.TrySetResult(eventId);
            return Task.CompletedTask;
        };
        inboundB.PingReceived += sessionKey =>
        {
            pingObservedAtB.TrySetResult(sessionKey);
            return Task.CompletedTask;
        };

        await transportA.StartListeningAsync();
        await transportB.StartListeningAsync();

        await transportA.ConnectToPeerAsync(Guid.NewGuid(), "localhost", portB);
        await transportB.ConnectToPeerAsync(Guid.NewGuid(), "localhost", portA);

        var outboundFromA = new DummyEvent(DateTime.UtcNow, "from-a");
        var outboundFromB = new DummyEvent(DateTime.UtcNow, "from-b");

        await transportA.PublishEventAsync(outboundFromA);
        await transportB.PublishEventAsync(outboundFromB);

        (await Task.WhenAny(receivedByA.Task, Task.Delay(500))).Should().Be(receivedByA.Task);
        (await Task.WhenAny(receivedByB.Task, Task.Delay(500))).Should().Be(receivedByB.Task);

        var ackAtA = await ackObservedAtA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ackAtB = await ackObservedAtB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ackAtA.Should().Be(outboundFromA.EventId);
        ackAtB.Should().Be(outboundFromB.EventId);

        await pingObservedAtA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await pingObservedAtB.Task.WaitAsync(TimeSpan.FromSeconds(10));

        receivedByA.Task.Result.Text.Should().Be("from-b");
        receivedByB.Task.Result.Text.Should().Be("from-a");
    }

    private sealed class TaskCompletionAsyncHandler<TEvent>(TaskCompletionSource<TEvent> source)
        : IAsyncEventHandler<TEvent> where TEvent : class, IDomainEvent
    {
        private readonly TaskCompletionSource<TEvent> _source = source;

        public Task OnNextAsync(TEvent domainEvent, CancellationToken cancellationToken = default)
        {
            if (_source.TrySetResult(domainEvent))
            {
                return Task.CompletedTask;

            }
            return Task.FromException(new InvalidOperationException("Event was already set."));
        }
    }
}
