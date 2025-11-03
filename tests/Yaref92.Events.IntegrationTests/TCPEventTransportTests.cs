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
        aggregatorA.RegisterEventType<DummyEvent>();

        var aggregatorB = new EventAggregator();
        aggregatorB.RegisterEventType<DummyEvent>();

        var tcsA = new TaskCompletionSource<DummyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsB = new TaskCompletionSource<DummyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        aggregatorA.SubscribeToEventType(new TaskCompletionAsyncHandler<DummyEvent>(tcsA));
        aggregatorB.SubscribeToEventType(new TaskCompletionAsyncHandler<DummyEvent>(tcsB));

        await using var transportA = new TCPEventTransport(portA, eventAggregator: aggregatorA);
        await using var transportB = new TCPEventTransport(portB, eventAggregator: aggregatorB);
        await transportA.StartListeningAsync();
        await transportB.StartListeningAsync();
        await transportA.ConnectToPeerAsync("localhost", portB);
        await transportB.ConnectToPeerAsync("localhost", portA);

        transportA.Subscribe<DummyEvent>();
        transportB.Subscribe<DummyEvent>();

        var evt1 = new DummyEvent();
        var evt2 = new DummyEvent();

        // Act
        await transportA.PublishAsync(evt1);
        await transportB.PublishAsync(evt2);

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
            _source.TrySetResult(domainEvent);
            return Task.CompletedTask;
        }
    }
}
