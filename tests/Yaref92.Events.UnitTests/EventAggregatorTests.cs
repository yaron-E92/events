using Yaref92.Events.Abstractions;

using FluentAssertions;

using NSubstitute;
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
    public void PublishEvent_ThrowsArgumentNullException_And_LogsError_When_NullEvent()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();

        // Act
        Action act = () => _aggregator.PublishEvent<DummyEvent>(null);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("domainEvent");
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }

    [Test]
    public void SubscribeToEventType_IsIdempotent_When_CalledMultipleTimesWithSameSubscriber()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        var callCount = 0;
        _aggregator.SubscribeToEventType(_subscriber);
        _subscriber.When(x => x.OnNext(Arg.Any<DummyEvent>())).Do(_ => callCount++);

        // Act
        _aggregator.SubscribeToEventType(_subscriber); // Should be idempotent
        _aggregator.PublishEvent(new DummyEvent());

        // Assert
        callCount.Should().Be(1, "subscribing the same subscriber multiple times should not result in multiple event deliveries");
        // Optionally, check for a log message if your implementation logs duplicate subscriptions
        // _logger.Received().Log(
        //     LogLevel.Warning,
        //     Arg.Any<EventId>(),
        //     Arg.Any<object>(),
        //     Arg.Any<Exception>(),
        //     Arg.Any<Func<object, Exception?, string>>()
        // );
    }

    [Test]
    public void PublishEvent_DifferentEventTypes_AreIsolated()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.RegisterEventType<OtherDummyEvent>();
        var dummySubscriber = Substitute.For<IEventSubscriber<DummyEvent>>();
        var otherSubscriber = Substitute.For<IEventSubscriber<OtherDummyEvent>>();
        _aggregator.SubscribeToEventType(dummySubscriber);
        _aggregator.SubscribeToEventType(otherSubscriber);
        var dummyEvent = new DummyEvent();
        var otherEvent = new OtherDummyEvent();

        // Act
        _aggregator.PublishEvent(dummyEvent);
        _aggregator.PublishEvent(otherEvent);

        // Assert
        dummySubscriber.Received(1).OnNext(dummyEvent);
        otherSubscriber.Received(1).OnNext(otherEvent);
        dummySubscriber.DidNotReceive().OnNext(Arg.Is<DummyEvent>(e => e == (object)otherEvent));
        otherSubscriber.DidNotReceive().OnNext(Arg.Is<OtherDummyEvent>(e => e == (object)dummyEvent));
    }

    [Test]
    public void PublishEvent_MultipleUniqueSubscribers_AllReceiveEvent()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        var sub1 = Substitute.For<IEventSubscriber<DummyEvent>>();
        var sub2 = Substitute.For<IEventSubscriber<DummyEvent>>();
        _aggregator.SubscribeToEventType(sub1);
        _aggregator.SubscribeToEventType(sub2);
        var evt = new DummyEvent();

        // Act
        _aggregator.PublishEvent(evt);

        // Assert
        sub1.Received(1).OnNext(evt);
        sub2.Received(1).OnNext(evt);
    }

    [Test]
    public void RegisterEventType_LogsWarning_When_DuplicateRegistration()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();

        // Act
        _aggregator.RegisterEventType<DummyEvent>();

        // Assert
        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }

    [Test]
    public void SubscribeToEventType_LogsWarning_When_DuplicateSubscription()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.SubscribeToEventType(_subscriber);
        _logger.ClearReceivedCalls();

        // Act
        _aggregator.SubscribeToEventType(_subscriber);

        // Assert
        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()
        );
    }

    [Test]
    public void UnsubscribeFromEventType_IsIdempotent_When_CalledMultipleTimes()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.SubscribeToEventType(_subscriber);
        _aggregator.UnsubscribeFromEventType(_subscriber);

        // Act & Assert
        _aggregator.Invoking(a => a.UnsubscribeFromEventType(_subscriber)).Should().NotThrow();
    }

    [Test]
    public void SubscribeToEventType_ThrowsArgumentNullException_When_SubscriberIsNull()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();

        // Act
        Action act = () => _aggregator.SubscribeToEventType(null as IEventSubscriber<DummyEvent>);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void UnsubscribeFromEventType_ThrowsArgumentNullException_When_SubscriberIsNull()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();

        // Act
        Action act = () => _aggregator.UnsubscribeFromEventType(null as IEventSubscriber<DummyEvent>);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task PublishEventAsync_CallsOnEvent_When_EventTypeAlreadyRegisteredAndRelevantSubscriberExists()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        _aggregator.SubscribeToEventType(_subscriber);
        DummyEvent domainEvent = new();

        // Act
        await _aggregator.PublishEventAsync(domainEvent);

        // Assert
        _subscriber.Received(1).OnNext(domainEvent);
    }

    [Test]
    public async Task PublishEventAsync_WorksWithSubscriberDoingAsyncWorkInternally()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();
        var completionSource = new TaskCompletionSource<bool>();
        var asyncSubscriber = Substitute.For<IEventSubscriber<DummyEvent>>();
        asyncSubscriber.When(x => x.OnNext(Arg.Any<DummyEvent>())).Do(_ =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(50); // Simulate async work
                completionSource.SetResult(true);
            });
        });
        _aggregator.SubscribeToEventType(asyncSubscriber);
        DummyEvent domainEvent = new();

        // Act
        await _aggregator.PublishEventAsync(domainEvent);
        var completed = await Task.WhenAny(completionSource.Task, Task.Delay(500));

        // Assert
        asyncSubscriber.Received(1).OnNext(domainEvent);
        completionSource.Task.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task PublishEventAsync_ThrowsException_When_EventTypeIsNotRegistered()
    {
        // Arrange
        DummyEvent domainEvent = new();

        // Act
        Func<Task> act = async () => await _aggregator.PublishEventAsync(domainEvent);

        // Assert
        await act.Should().ThrowAsync<MissingEventTypeException>();
    }

    [Test]
    public async Task PublishEventAsync_ThrowsArgumentNullException_When_NullEvent()
    {
        // Arrange
        _aggregator.RegisterEventType<DummyEvent>();

        // Act
        Func<Task> act = async () => await _aggregator.PublishEventAsync<DummyEvent>(null);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private class DummyAsyncSubscriber : IAsyncEventSubscriber<DummyEvent>
    {
        public TaskCompletionSource<DummyEvent> Received { get; } = new();
        public Task OnNextAsync(DummyEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Received.TrySetResult(domainEvent);
            return Task.CompletedTask;
        }
    }

    private class CancellableAsyncSubscriber : IAsyncEventSubscriber<DummyEvent>
    {
        public TaskCompletionSource<DummyEvent> Received { get; } = new();
        public TaskCompletionSource<bool> Cancelled { get; } = new();
        
        public Task OnNextAsync(DummyEvent domainEvent, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult(true);
                return Task.FromCanceled(cancellationToken);
            }
            
            Received.TrySetResult(domainEvent);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task AsyncSubscriber_ReceivesEvent_OnPublishEventAsync()
    {
        _aggregator.RegisterEventType<DummyEvent>();
        var asyncSubscriber = new DummyAsyncSubscriber();
        _aggregator.SubscribeToEventType(asyncSubscriber);
        var evt = new DummyEvent();
        await _aggregator.PublishEventAsync(evt);
        (await asyncSubscriber.Received.Task).Should().Be(evt);
    }

    [Test]
    public async Task MultipleAsyncSubscribers_AllReceiveEvent()
    {
        _aggregator.RegisterEventType<DummyEvent>();
        var sub1 = new DummyAsyncSubscriber();
        var sub2 = new DummyAsyncSubscriber();
        _aggregator.SubscribeToEventType(sub1);
        _aggregator.SubscribeToEventType(sub2);
        var evt = new DummyEvent();
        await _aggregator.PublishEventAsync(evt);
        (await sub1.Received.Task).Should().Be(evt);
        (await sub2.Received.Task).Should().Be(evt);
    }

    [Test]
    public async Task MixedSyncAndAsyncSubscribers_AllReceiveEvent()
    {
        _aggregator.RegisterEventType<DummyEvent>();
        var asyncSub = new DummyAsyncSubscriber();
        _aggregator.SubscribeToEventType(asyncSub);
        _aggregator.SubscribeToEventType(_subscriber);
        var evt = new DummyEvent();
        await _aggregator.PublishEventAsync(evt);
        (await asyncSub.Received.Task).Should().Be(evt);
        _subscriber.Received(1).OnNext(evt);
    }

    [Test]
    public void AsyncSubscriber_Unsubscribed_DoesNotReceiveEvent()
    {
        _aggregator.RegisterEventType<DummyEvent>();
        var asyncSubscriber = new DummyAsyncSubscriber();
        _aggregator.SubscribeToEventType(asyncSubscriber);
        _aggregator.UnsubscribeFromEventType(asyncSubscriber);
        Func<Task> act = async () =>
        {
            await _aggregator.PublishEventAsync(new DummyEvent());
            await Task.Delay(100);
        };
        asyncSubscriber.Received.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public async Task AsyncSubscriber_Exception_IsPropagated()
    {
        _aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new FailingAsyncSubscriber();
        _aggregator.SubscribeToEventType(subscriber);
        Func<Task> act = async () => await _aggregator.PublishEventAsync(new DummyEvent());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task AsyncSubscriber_Cancellation_IsRespected()
    {
        _aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new CancellableAsyncSubscriber();
        _aggregator.SubscribeToEventType(subscriber);
        
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately
        
        Func<Task> act = async () => await _aggregator.PublishEventAsync(new DummyEvent(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        
        subscriber.Cancelled.Task.IsCompleted.Should().BeTrue();
    }

    private class FailingAsyncSubscriber : IAsyncEventSubscriber<DummyEvent>
    {
        public Task OnNextAsync(DummyEvent value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("fail");
    }
}

public class OtherDummyEvent : IDomainEvent
{
    public DateTime DateTimeOccurredUtc => DateTime.UtcNow;
}
