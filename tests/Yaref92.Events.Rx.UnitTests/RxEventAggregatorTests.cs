using System.Reactive;
using System.Reactive.Linq;

using FluentAssertions;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Rx.UnitTests;

[TestFixture, TestOf(typeof(RxEventAggregator))]
public class RxEventAggregatorTests
{
    private RxEventAggregator _aggregator;

    [SetUp]
    public void SetUp()
    {
        _aggregator = new RxEventAggregator();
        _aggregator.RegisterEventType<DummyEvent>();
    }

    [Test]
    public void SubscribeToEventTypeRx_ReceivesPublishedEvent()
    {
        // Arrange
        DummyEvent received = null;
        var subscription = _aggregator.SubscribeToEventTypeRx<DummyEvent>(Observer.Create<DummyEvent>(e => received = e));
        var evt = new DummyEvent();

        // Act
        _aggregator.PublishEventRx(evt);

        // Assert
        received.Should().Be(evt);
        subscription.Dispose();
    }

    [Test]
    public void SubscribeToEventTypeRx_DoesNotReceiveAfterDispose()
    {
        // Arrange
        DummyEvent received = null;
        var subscription = _aggregator.SubscribeToEventTypeRx<DummyEvent>(Observer.Create<DummyEvent>(e => received = e));
        var evt1 = new DummyEvent();
        _aggregator.PublishEventRx(evt1);
        subscription.Dispose();
        var evt2 = new DummyEvent();

        // Act
        _aggregator.PublishEventRx(evt2);

        // Assert
        received.Should().Be(evt1);
    }

    [Test]
    public void SubscribeToEventTypeRx_MultipleSubscribers_AllReceiveEvent()
    {
        // Arrange
        DummyEvent received1 = null, received2 = null;
        var sub1 = _aggregator.SubscribeToEventTypeRx<DummyEvent>(Observer.Create<DummyEvent>(e => received1 = e));
        var sub2 = _aggregator.SubscribeToEventTypeRx<DummyEvent>(Observer.Create<DummyEvent>(e => received2 = e));
        var evt = new DummyEvent();

        // Act
        _aggregator.PublishEventRx(evt);

        // Assert
        received1.Should().Be(evt);
        received2.Should().Be(evt);
        sub1.Dispose();
        sub2.Dispose();
    }

    [Test]
    public void SubscribeToEventTypeRx_DifferentEventTypes_Isolated()
    {
        // Arrange
        _aggregator.RegisterEventType<OtherDummyEvent>();
        DummyEvent receivedDummy = null;
        OtherDummyEvent receivedOther = null;
        var sub1 = _aggregator.SubscribeToEventTypeRx<DummyEvent>(Observer.Create<DummyEvent>(e => receivedDummy = e));
        var sub2 = _aggregator.SubscribeToEventTypeRx<OtherDummyEvent>(Observer.Create<OtherDummyEvent>(e => receivedOther = e));
        var evt1 = new DummyEvent();
        var evt2 = new OtherDummyEvent();

        // Act
        _aggregator.PublishEventRx(evt1);
        _aggregator.PublishEventRx(evt2);

        // Assert
        receivedDummy.Should().Be(evt1);
        receivedOther.Should().Be(evt2);
        sub1.Dispose();
        sub2.Dispose();
    }

    [Test]
    public async Task EventStream_ReceivesEvents_AsObservable()
    {
        // Arrange
        DummyEvent received = null;
        var evt = new DummyEvent();
        var tcs = new TaskCompletionSource<bool>();
        var sub = _aggregator.EventStream.OfType<DummyEvent>().Subscribe(e => { received = e; tcs.SetResult(true); });

        // Act
        _aggregator.PublishEventRx(evt);
        await tcs.Task.TimeoutAfter(TimeSpan.FromSeconds(1));

        // Assert
        received.Should().Be(evt);
        sub.Dispose();
    }
}

public class OtherDummyEvent : IDomainEvent
{
    public DateTime DateTimeOccurredUtc => DateTime.UtcNow;
}

public static class TaskExtensions
{
    public static async Task TimeoutAfter(this Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            throw new TimeoutException();
        await task;
    }
} 
