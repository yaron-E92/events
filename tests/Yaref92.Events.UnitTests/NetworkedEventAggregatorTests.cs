using FluentAssertions;

using NSubstitute;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.UnitTests;

[TestFixture, TestOf(typeof(NetworkedEventAggregator))]
internal class NetworkedEventAggregatorTests
{
    private IEventAggregator _localAggregator;
    private IEventTransport _transport;
    private NetworkedEventAggregator _networkedAggregator;
    private IEventHandler<DummyEvent> _subscriber;
    private IAsyncEventHandler<DummyEvent> _asyncSubscriber;

    [SetUp]
    public void SetUp()
    {
        _localAggregator = Substitute.For<IEventAggregator>();
        Substitute.For<IPersistentPortListener>();
        _transport = Substitute.For<IEventTransport>();
        _networkedAggregator = new NetworkedEventAggregator(_localAggregator, _transport);
        _subscriber = Substitute.For<IEventHandler<DummyEvent>>();
        _asyncSubscriber = Substitute.For<IAsyncEventHandler<DummyEvent>>();
    }

    [Test]
    public void RegisterEventType_RegistersLocally()
    {
        // Arrange
        _localAggregator.RegisterEventType<DummyEvent>().Returns(true);

        // Act
        var result = _networkedAggregator.RegisterEventType<DummyEvent>();

        // Assert
        result.Should().BeTrue();
        _localAggregator.Received(1).RegisterEventType<DummyEvent>();
    }

    [Test]
    public void PublishEvent_PublishesLocally_AndOverTransport()
    {
        // Arrange
        DummyEvent evt = new();

        // Act
        _networkedAggregator.PublishEvent(evt);

        // Assert
        _localAggregator.Received(1).PublishEvent(evt);
        _transport.Received(1).PublishEventAsync(evt, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishEventAsync_PublishesLocally_AndOverTransport()
    {
        // Arrange
        DummyEvent evt = new();

        // Act
        await _networkedAggregator.PublishEventAsync(evt);

        // Assert
        await _localAggregator.Received(1).PublishEventAsync(evt, Arg.Any<CancellationToken>());
        await _transport.Received(1).PublishEventAsync(evt, Arg.Any<CancellationToken>());
    }

    [Test]
    public void SubscribeToEventType_DelegatesToLocalAggregator()
    {
        // Act
        _networkedAggregator.SubscribeToEventType(_subscriber);

        // Assert
        _localAggregator.Received(1).SubscribeToEventType(_subscriber);
    }

    [Test]
    public void SubscribeToEventType_Async_DelegatesToLocalAggregator()
    {
        // Act
        _networkedAggregator.SubscribeToEventType(_asyncSubscriber);

        // Assert
        _localAggregator.Received(1).SubscribeToEventType(_asyncSubscriber);
    }

    [Test]
    public void UnsubscribeFromEventType_DelegatesToLocalAggregator()
    {
        // Act
        _networkedAggregator.UnsubscribeFromEventType(_subscriber);

        // Assert
        _localAggregator.Received(1).UnsubscribeFromEventType(_subscriber);
    }

    [Test]
    public void UnsubscribeFromEventType_Async_DelegatesToLocalAggregator()
    {
        // Act
        _networkedAggregator.UnsubscribeFromEventType(_asyncSubscriber);

        // Assert
        _localAggregator.Received(1).UnsubscribeFromEventType(_asyncSubscriber);
    }

    [Test]
    public async Task NetworkEvent_IsDispatchedToLocalAggregator()
    {
        // Arrange
        _localAggregator.RegisterEventType<DummyEvent>().Returns(true);
        _networkedAggregator.RegisterEventType<DummyEvent>();
        DummyEvent evt = new();

        // Act
        await _networkedAggregator.PublishEventAsync(evt, CancellationToken.None);

        // Assert
        await _localAggregator.Received(1).PublishEventAsync(evt, Arg.Any<CancellationToken>());
    }

    [TearDown]
    public void TearDown()
    {
        _networkedAggregator?.Dispose();
    }
} 
