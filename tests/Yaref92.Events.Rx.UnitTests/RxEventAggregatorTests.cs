using System.Reactive.Linq;

using FluentAssertions;

using NSubstitute;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Rx.Abstractions;

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

    [TearDown]
    public void TearDown()
    {
        _aggregator.Dispose();
    }

    [Test]
    public void EventStream_ReceivesEvents_AsObservable()
    {
        // Arrange
        DummyEvent received = null;
        var evt = new DummyEvent();
        var tcs = new TaskCompletionSource<bool>();
        var sub = _aggregator.EventStream.OfType<DummyEvent>().Subscribe(e => { received = e; tcs.SetResult(true); });

        // Act
        _aggregator.PublishEvent(evt);
        tcs.Task.Wait(TimeSpan.FromSeconds(1));

        // Assert
        received.Should().Be(evt);
        sub.Dispose();
    }

    public class RxDummySubscriber : IRxSubscriber<DummyEvent>
    {
        public DummyEvent ReceivedEvent { get; private set; } = new DummyEvent();
        public int OnNextCount { get; private set; }
        public void OnNext(DummyEvent value)
        {
            ReceivedEvent = value;
            OnNextCount++;
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Test]
    public void SubscribeAndUnsubscribe_RxSubscriber_ViaUnifiedApi_Works()
    {
        // Arrange
        var rxSubscriber = new RxDummySubscriber();

        // Act
        _aggregator.SubscribeToEventType(rxSubscriber);
        var evt = new DummyEvent();
        _aggregator.PublishEvent(evt);
        _aggregator.UnsubscribeFromEventType(rxSubscriber);
        _aggregator.PublishEvent(new DummyEvent());

        // Assert
        rxSubscriber.ReceivedEvent.Should().Be(evt);
        rxSubscriber.OnNextCount.Should().Be(1);
    }

    [Test]
    public void UnsubscribeFromEventType_RxSubscriber_IsIdempotent()
    {
        // Arrange
        var rxSubscriber = new RxDummySubscriber();
        _aggregator.SubscribeToEventType(rxSubscriber);
        _aggregator.UnsubscribeFromEventType(rxSubscriber);

        // Act & Assert
        _aggregator.Invoking(a => a.UnsubscribeFromEventType(rxSubscriber)).Should().NotThrow();
    }

    [Test]
    public void PublishEvent_MixedRegularAndRxSubscribers_BothReceiveEvent()
    {
        // Arrange
        var rxSubscriber = new RxDummySubscriber();
        var regularSubscriber = Substitute.For<IEventSubscriber<DummyEvent>>();
        _aggregator.SubscribeToEventType(rxSubscriber);
        _aggregator.SubscribeToEventType(regularSubscriber);
        var evt = new DummyEvent();

        // Act
        _aggregator.PublishEvent(evt);

        // Assert
        rxSubscriber.ReceivedEvent.Should().Be(evt);
        regularSubscriber.Received(1).OnNext(evt);
    }

    [Test]
    public void Dispose_DisposesAllRxSubscriptions()
    {
        // Arrange
        var rxSubscriber = new RxDummySubscriber();
        _aggregator.SubscribeToEventType(rxSubscriber);
        _aggregator.Dispose();

        // Act
        // After dispose, publishing should not deliver to Rx subscriber
        _aggregator.PublishEvent(new DummyEvent());

        // Assert
        rxSubscriber.OnNextCount.Should().Be(0);
    }

    [Test]
    public void PublishEvent_DifferentEventTypes_AreIsolated()
    {
        // Arrange
        _aggregator.RegisterEventType<OtherDummyEvent>();
        var dummySubscriber = new RxDummySubscriber();
        var otherSubscriber = new OtherRxDummySubscriber();
        _aggregator.SubscribeToEventType(dummySubscriber);
        _aggregator.SubscribeToEventType(otherSubscriber);
        var dummyEvent = new DummyEvent();
        var otherEvent = new OtherDummyEvent();

        // Act
        _aggregator.PublishEvent(dummyEvent);
        _aggregator.PublishEvent(otherEvent);

        // Assert
        dummySubscriber.ReceivedEvent.Should().Be(dummyEvent);
        otherSubscriber.ReceivedEvent.Should().Be(otherEvent);
    }

    public class OtherRxDummySubscriber : IRxSubscriber<OtherDummyEvent>
    {
        public OtherDummyEvent ReceivedEvent { get; private set; } = new OtherDummyEvent();
        public int OnNextCount { get; private set; }
        public void OnNext(OtherDummyEvent value)
        {
            ReceivedEvent = value;
            OnNextCount++;
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

public class OtherDummyEvent : DomainEventBase
{
} 
