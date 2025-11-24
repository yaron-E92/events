using FluentAssertions;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Rx.Abstractions;

namespace Yaref92.Events.Rx.UnitTests;

public class DummyAsyncRxSubscriber : AsyncRxSubscriber<DummyEvent>
{
    public int OnNextCallCount { get; private set; }
    public TaskCompletionSource<DummyEvent> Received { get; } = new();
    public override Task OnNextAsync(DummyEvent value, CancellationToken cancellationToken = default)
    {
        OnNextCallCount++;
        Received.TrySetResult(value);
        return Task.CompletedTask;
    }
}

public class DummySyncRxSubscriber : IRxSubscriber<DummyEvent>
{
    public DummyEvent? LastReceived { get; private set; }
    public void OnNext(DummyEvent value) => LastReceived = value;
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}

public class DummyAsyncSubscriber : IAsyncEventHandler<DummyEvent>
{
    public TaskCompletionSource<DummyEvent> Received { get; } = new();
    public Task OnNextAsync(DummyEvent value, CancellationToken cancellationToken = default)
    {
        Received.TrySetResult(value);
        return Task.CompletedTask;
    }
}

public class CancellableAsyncSubscriber : IAsyncEventHandler<DummyEvent>
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

[TestFixture]
public class RxEventAggregatorAsyncTests
{
    [Test]
    public async Task AsyncRxSubscriber_ReceivesEvent_OnPublishEventAsync()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new DummyAsyncRxSubscriber();
        aggregator. SubscribeToEventType<DummyEvent>(subscriber);

        var evt = new DummyEvent();
        await aggregator.PublishEventAsync(evt);

        (await subscriber.Received.Task).Should().Be(evt);
    }

    [Test]
    public async Task MultipleAsyncRxSubscribers_AllReceiveEvent()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var sub1 = new DummyAsyncRxSubscriber();
        var sub2 = new DummyAsyncRxSubscriber();
        aggregator.SubscribeToEventType(sub1);
        aggregator.SubscribeToEventType(sub2);

        var evt = new DummyEvent();
        await aggregator.PublishEventAsync(evt);

        (await sub1.Received.Task).Should().Be(evt);
        (await sub2.Received.Task).Should().Be(evt);
    }

    [Test]
    public async Task AsyncRxSubscriber_DoubleSubscribe_DoesNotLeaveUntrackedSubscriptions()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new DummyAsyncRxSubscriber();

        aggregator.SubscribeToEventType(subscriber);
        aggregator.SubscribeToEventType(subscriber);

        var evt = new DummyEvent();
        await aggregator.PublishEventAsync(evt);

        subscriber.OnNextCallCount.Should().Be(1);
        (await subscriber.Received.Task).Should().Be(evt);

        aggregator.UnsubscribeFromEventType(subscriber);
        await aggregator.PublishEventAsync(new DummyEvent());

        subscriber.OnNextCallCount.Should().Be(1);
    }

    [Test]
    public async Task MixedSyncAndAsyncRxSubscribers_AllReceiveEvent()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var asyncSub = new DummyAsyncRxSubscriber();
        var syncSub = new DummySyncRxSubscriber();
        aggregator.SubscribeToEventType(asyncSub);
        aggregator.SubscribeToEventType(syncSub);

        var evt = new DummyEvent();
        await aggregator.PublishEventAsync(evt);

        (await asyncSub.Received.Task).Should().Be(evt);
        syncSub.LastReceived.Should().Be(evt);
    }

    [Test]
    public async Task AsyncRxSubscriber_Unsubscribed_DoesNotReceiveEvent()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new DummyAsyncRxSubscriber();
        aggregator.SubscribeToEventType(subscriber);
        aggregator.UnsubscribeFromEventType(subscriber);

        Func<Task> act = async () =>
        {
            await aggregator.PublishEventAsync(new DummyEvent());
            // Wait a bit to ensure no event is received
            await Task.Delay(100);
        };

        await act();

        subscriber.Received.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public async Task AsyncRxSubscriber_Exception_IsPropagated()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new FailingAsyncRxSubscriber();
        aggregator.SubscribeToEventType(subscriber);

        Func<Task> act = async () => await aggregator.PublishEventAsync(new DummyEvent());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private class FailingAsyncRxSubscriber : AsyncRxSubscriber<DummyEvent>
    {
        public override Task OnNextAsync(DummyEvent value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("fail");
    }

    [Test]
    public async Task AsyncSubscriber_ReceivesEvent_OnPublishEventAsync()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var asyncSubscriber = new DummyAsyncSubscriber();
        aggregator.SubscribeToEventType(asyncSubscriber);
        var evt = new DummyEvent();
        await aggregator.PublishEventAsync(evt);
        (await asyncSubscriber.Received.Task).Should().Be(evt);
    }

    [Test]
    public async Task MultipleAsyncSubscribers_AllReceiveEvent()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var sub1 = new DummyAsyncSubscriber();
        var sub2 = new DummyAsyncSubscriber();
        aggregator.SubscribeToEventType(sub1);
        aggregator.SubscribeToEventType(sub2);
        var evt = new DummyEvent();
        await aggregator.PublishEventAsync(evt);
        (await sub1.Received.Task).Should().Be(evt);
        (await sub2.Received.Task).Should().Be(evt);
    }

    [Test]
    public async Task MixedSyncAndAsyncSubscribers_AllReceiveEvent()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var asyncSub = new DummyAsyncSubscriber();
        var syncSub = new DummySyncRxSubscriber();
        aggregator.SubscribeToEventType(asyncSub);
        aggregator.SubscribeToEventType(syncSub);
        var evt = new DummyEvent();
        await aggregator.PublishEventAsync(evt);
        (await asyncSub.Received.Task).Should().Be(evt);
        syncSub.LastReceived.Should().Be(evt);
    }

    [Test]
    public async Task AsyncSubscriber_Unsubscribed_DoesNotReceiveEvent()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var asyncSubscriber = new DummyAsyncSubscriber();
        aggregator.SubscribeToEventType(asyncSubscriber);
        aggregator.UnsubscribeFromEventType(asyncSubscriber);
        Func<Task> act = async () =>
        {
            await aggregator.PublishEventAsync(new DummyEvent());
            await Task.Delay(100);
        };
        await act();

        asyncSubscriber.Received.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public async Task AsyncSubscriber_Exception_IsPropagated()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new FailingAsyncSubscriber();
        aggregator.SubscribeToEventType(subscriber);
        Func<Task> act = async () => await aggregator.PublishEventAsync(new DummyEvent());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private class FailingAsyncSubscriber : IAsyncEventHandler<DummyEvent>
    {
        public Task OnNextAsync(DummyEvent value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("fail");
    }

    [Test]
    public async Task AsyncSubscriber_Cancellation_IsRespected()
    {
        var aggregator = new RxEventAggregator();
        aggregator.RegisterEventType<DummyEvent>();
        var subscriber = new CancellableAsyncSubscriber();
        aggregator.SubscribeToEventType(subscriber);
        
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately
        
        Func<Task> act = async () => await aggregator.PublishEventAsync(new DummyEvent(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        
        subscriber.Cancelled.Task.IsCompleted.Should().BeTrue();
    }
} 
