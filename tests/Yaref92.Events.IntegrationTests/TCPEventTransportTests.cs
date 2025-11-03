using System.Threading.Tasks;

using FluentAssertions;

using Yaref92.Events;
using Yaref92.Events.Abstractions;
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

        await using var transportA = new TCPEventTransport(portA, eventAggregator: aggregatorA);
        await using var transportB = new TCPEventTransport(portB, eventAggregator: aggregatorB);

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
