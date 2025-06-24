using Yaref92.Events.Abstractions;

using FluentAssertions;

using NSubstitute;
using System.Reactive.Disposables;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Yaref92.Events.UnitTests;

[TestFixture, TestOf(typeof(EventAggregator))]
internal class EventAggregatorTests
{
    private IEventAggregator _aggregator;
    private IEventSubscriber<DummyEvent> _subscriber;
    private ILogger<EventAggregator> _logger;

    [SetUp]
    public void SetUp()
    {
        _logger = Substitute.For<ILogger<EventAggregator>>();
        _aggregator = new EventAggregator(_logger);
        _subscriber = Substitute.For<IEventSubscriber<DummyEvent>>();
        _subscriber.ClearReceivedCalls();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            _aggregator.UnsubscribeFromEventType(_subscriber);
        }
        catch
        {
            /* ignore if not subscribed */
        }
    }

    [Test]
    public void RegisterEventType_AddsEventType_When_NotExisting()
    {
        // Arrange
        _aggregator.EventTypes.Should().HaveCount(0);

        // Act
        var result = _aggregator.RegisterEventType<DummyEvent>();

        // Assert
        result.Should().BeTrue();
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
        var result = _aggregator.RegisterEventType<DummyEvent>();

        // Assert
        result.Should().BeFalse();
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
        // Do not subscribe any subscriber for this test case.
        
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
        _aggregator.Subscribers.Should().HaveCount(1);
        _aggregator.Subscriptions.Should().HaveCount(1);
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
        _aggregator.SubscribeToEventType(_subscriber);

        // Act
        Action act = () => _aggregator.UnsubscribeFromEventType<DummyEvent>(_subscriber);

        // Assert
        act.Should().NotThrow();
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

    [Test]
    public void RegisterEventType_IsThreadSafe_When_CalledConcurrently()
    {
        // Arrange
        const int threadCount = 20;
        _aggregator.EventTypes.Should().HaveCount(0);
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < threadCount; i++)
        {
            tasks.Add(Task.Run(() => _aggregator.RegisterEventType<DummyEvent>()));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert
        _aggregator.EventTypes.Should().HaveCount(1);
        _aggregator.EventTypes.Should().Contain(typeof(DummyEvent));
    }

    [Test]
    public void PublishEvent_And_SubscribeToEventType_AreThreadSafe_When_CalledConcurrently()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        // Ensure at least one subscriber is present before publishing
        _aggregator.SubscribeToEventType(_subscriber);
        int publishCount = 100;
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < publishCount; i++)
        {
            tasks.Add(Task.Run(() => _aggregator.PublishEvent(new DummyEvent())));
            if (i % 10 == 0)
            {
                tasks.Add(Task.Run(() => _aggregator.SubscribeToEventType(Substitute.For<IEventSubscriber<DummyEvent>>())));
            }
        }
        Task.WaitAll(tasks.ToArray());

        // Assert
        // At least one OnNext should be received
        _subscriber.ReceivedWithAnyArgs().OnNext(Arg.Any<DummyEvent>());
    }

    [Test]
    public void PublishEvent_And_SubscribeToEventType_AreThreadSafe_NoExceptions_When_CalledConcurrently()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        int publishCount = 100;
        var tasks = new List<Task>();

        // Act & Assert
        Action act = () =>
        {
            for (int i = 0; i < publishCount; i++)
            {
                tasks.Add(Task.Run(() => _aggregator.PublishEvent(new DummyEvent())));
                if (i % 10 == 0)
                {
                    tasks.Add(Task.Run(() => _aggregator.SubscribeToEventType(Substitute.For<IEventSubscriber<DummyEvent>>())));
                }
            }
            Task.WaitAll(tasks.ToArray());
        };

        act.Should().NotThrow();
    }

    [Test]
    public void PublishEvent_ThrowsArgumentNullException_And_LogsWarning_When_NullEvent()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();

        // Act
        Action act = () => _aggregator.PublishEvent<DummyEvent>(null);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("domainEvent");
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }

    [Test]
    public void SubscribeToEventType_AllowsMultipleSubscriptions_PerSubscriberAndEventType()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        var callCount = 0;
        _aggregator.SubscribeToEventType(_subscriber);
        _subscriber.When(x => x.OnNext(Arg.Any<DummyEvent>())).Do(_ => callCount++);

        // Act
        _aggregator.SubscribeToEventType(_subscriber);
        _aggregator.PublishEvent(new DummyEvent());

        // Assert
        callCount.Should().Be(2, "each subscription should receive the event");
    }

    [Test]
    public void UnsubscribeFromEventType_DisposesAllSubscriptions_ForSubscriberAndEventType()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.SubscribeToEventType(_subscriber);
        _aggregator.SubscribeToEventType(_subscriber);
        // Unsubscribe
        _aggregator.UnsubscribeFromEventType(_subscriber);
        // There is no direct way to access the disposables, but we can check that no further events are received
        _aggregator.PublishEvent(new DummyEvent());
        // Assert
        _subscriber.DidNotReceive().OnNext(Arg.Any<DummyEvent>());
    }
}
