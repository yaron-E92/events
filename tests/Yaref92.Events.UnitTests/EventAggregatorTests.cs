using Yaref92.Events.Abstractions;

using FluentAssertions;

using NSubstitute;

namespace Yaref92.Events.UnitTests;

[TestFixture, TestOf(typeof(EventAggregator))]
internal class EventAggregatorTests
{
    private IEventAggregator _aggregator;
    ISubscription _subscription;
    IEventSubscriber<DummyEvent> _subscriber;

    [SetUp]
    public void SetUp()
    {
        _aggregator = new EventAggregator();
        _subscription = Substitute.For<ISubscription>();
        _subscriber = Substitute.For<IEventSubscriber<DummyEvent>>();
        _subscriber.Subscription.Returns(_subscription);
    }

    [Test]
    public void RegisterEventType_AddsEventType_When_NotExisting()
    {
        // Arrange
        _aggregator.EventTypes.Should().HaveCount(0);

        // Act
        _aggregator.RegisterEventType<DummyEvent>();

        // Assert
        _aggregator.EventTypes.Should().HaveCount(1);
        _aggregator.EventTypes.Should().Contain(typeof(DummyEvent));

    }

    [Test]
    public void RegisterEventType_DoesNotAddNewSet_When_EventTypeAlreadyRegistered()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.EventTypes.Should().HaveCount(1);
        _aggregator.EventTypes.Should().OnlyContain(et => et.Equals(typeof(DummyEvent)));

        // Act
        _aggregator.RegisterEventType<DummyEvent>();

        // Assert
        _aggregator.EventTypes.Should().HaveCount(1);
        _aggregator.EventTypes.Should().OnlyContain(et => et.Equals(typeof(DummyEvent)));

    }

    [Test]
    public void PublishEvent_CallsOnEvent_When_EventTypeAlreadyRegisteredAndRelevantSubscriberExists()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.SubscribeToEventType(_subscriber);
        DummyEvent domainEvent = new();
        DummyEvent domainEvent2 = new();

        // Act
        _aggregator.PublishEvent(domainEvent);

        // Assert
        _subscriber.Received(1).OnNext(domainEvent);
        _subscriber.DidNotReceive().OnNext(domainEvent2);
    }

    [Test]
    public void PublishEvent_DoesNothing_When_EventTypeAlreadyRegisteredAndNoSubscriber()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        
        DummyEvent domainEvent = new();

        // Act
        _aggregator.PublishEvent(domainEvent);

        // Assert
        _subscriber.DidNotReceive().OnNext(domainEvent);
    }

    [Test]
    public void PublishEvent_ThrowsException_When_EventTypeIsNotRegistered()
    {
        // Arrange

        // Act
        Action act = () => _aggregator.PublishEvent(new DummyEvent());

        // Assert
        act.Should().Throw<MissingEventTypeException>();
    }

    [Test]
    public void SubscribeToEventType_AddsNewSubscriver_When_EventTypeAlreadyRegistered()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.EventTypes.Should().HaveCount(1);
        

        // Act
        //DummySubscriber subscriber = new();
        Action act = () => _aggregator.SubscribeToEventType(_subscriber);

        // Assert
        act.Should().NotThrow();
        _subscription.Received(1).AddSubscription(Arg.Any<IDisposable>());
    }

    [Test]
    public void SubscribeToEventType_ThrowsException_When_EventTypeIsNotRegistered()
    {
        // Arrange
        _aggregator.EventTypes.Should().HaveCount(0);

        // Act
        Action act = () => _aggregator.SubscribeToEventType<DummyEvent>(_subscriber);

        // Assert
        act.Should().ThrowExactly<MissingEventTypeException>();
        _aggregator.EventTypes.Should().HaveCount(0);
    }

    [Test]
    public void UnsubscribeFromEventType_RemovesSubscriber_When_EventTypeAlreadyRegisteredAndSubscriberIsThere()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.EventTypes.Should().HaveCount(1);
        _aggregator.SubscribeToEventType<DummyEvent>(_subscriber);

        // Act
        Action act = () => _aggregator.UnsubscribeFromEventType<DummyEvent>(_subscriber);

        // Assert
        act.Should().NotThrow();
        _subscription.Received(1).Dispose();
    }

    [Test]
    public void UnsubscribeFromEventType_RemovesNoSubscriber_When_EventTypeAlreadyRegisteredAndSubscriberIsNotThere()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.EventTypes.Should().HaveCount(1);

        // Act
        Action act = () => _aggregator.UnsubscribeFromEventType<DummyEvent>(_subscriber);

        // Assert
        act.Should().NotThrow();
        _subscription.ObservableSubscription.DidNotReceive().Dispose();
    }

    [Test]
    public void UnsubscribeFromEventType_ThrowsException_When_EventTypeIsNotRegistered()
    {
        // Arrange
        _aggregator.EventTypes.Should().HaveCount(0);

        // Act
        Action act = () => _aggregator.SubscribeToEventType<DummyEvent>(_subscriber);

        // Assert
        act.Should().ThrowExactly<MissingEventTypeException>();
        _aggregator.EventTypes.Should().HaveCount(0);
    }
}
