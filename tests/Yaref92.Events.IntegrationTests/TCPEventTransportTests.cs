using FluentAssertions;

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
        await using var transportA = new TCPEventTransport(portA);
        await using var transportB = new TCPEventTransport(portB);
        await transportA.StartListeningAsync();
        await transportB.StartListeningAsync();
        await transportA.ConnectToPeerAsync("localhost", portB);
        await transportB.ConnectToPeerAsync("localhost", portA);

        var tcsA = new TaskCompletionSource<DummyEvent>();
        var tcsB = new TaskCompletionSource<DummyEvent>();
        //transportA.Subscribe<DummyEvent>(async (evt, ct) => tcsA.TrySetResult(evt));
        //transportB.Subscribe<DummyEvent>(async (evt, ct) => tcsB.TrySetResult(evt));
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
}
