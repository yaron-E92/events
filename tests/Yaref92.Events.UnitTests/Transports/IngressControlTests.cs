using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using FluentAssertions;

using NUnit.Framework;

using Yaref92.Events.Sessions;
using Yaref92.Events.Serialization;
using Yaref92.Events.Transport.Tcp;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class IngressControlTests
{
    [Test]
    public void PeerIngressLimiter_RejectsPeerDeterministicallyAfterAllowance()
    {
        var options = new ResilientSessionOptions
        {
            MaxFramesPerSecondPerPeer = 2,
            FrameRateBurstAllowance = 1,
        };
        var limiter = new PeerIngressLimiter(options);
        var peer = new IPEndPoint(IPAddress.Loopback, 5000);

        limiter.TryAcquire(peer, out _).Should().BeTrue();
        limiter.TryAcquire(peer, out _).Should().BeTrue();
        limiter.TryAcquire(peer, out _).Should().BeTrue();
        limiter.TryAcquire(peer, out _).Should().BeFalse();
    }

    [TestCase(0, 1, 1, 0)]
    [TestCase(1, 0, 1, 0)]
    [TestCase(1, 1, 0, 0)]
    [TestCase(1, 1, 1, -1)]
    public void Validate_RejectsInvalidIngressLimits(
        int maxFrameBytes,
        int maxInboundConnections,
        int framesPerSecond,
        int burstAllowance)
    {
        var options = new ResilientSessionOptions
        {
            MaxFrameBytes = maxFrameBytes,
            MaxInboundConnections = maxInboundConnections,
            MaxFramesPerSecondPerPeer = framesPerSecond,
            FrameRateBurstAllowance = burstAllowance,
        };

        options.Validate().Should().BeFalse();
    }

    [Test]
    public async Task PersistentPortListener_RejectsConnectionBeyondCapacity_AndLogsSafeCategory()
    {
        int port = GetFreeTcpPort();
        var options = new ResilientSessionOptions { MaxInboundConnections = 1 };
        var logger = new CapturingLogger();
        var sessionManager = new TcpSessionManager(port, options);
        await using var listener = new PersistentPortListener(port, new JsonEventSerializer(), sessionManager, logger);
        var healthyPing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectionManager.PingReceived += _ =>
        {
            healthyPing.TrySetResult();
            return Task.CompletedTask;
        };
        await listener.StartAsync();

        using var occupyingClient = new TcpClient();
        await occupyingClient.ConnectAsync(IPAddress.Loopback, port);
        await Task.Delay(50);

        using var rejectedClient = new TcpClient();
        await rejectedClient.ConnectAsync(IPAddress.Loopback, port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int read = await rejectedClient.GetStream().ReadAsync(new byte[1], timeout.Token);

        read.Should().Be(0);
        logger.Messages.Should().Contain(message => message.Contains("connection-limit", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(message => message.Contains("payload", StringComparison.OrdinalIgnoreCase));

        occupyingClient.Dispose();
        await Task.Delay(100);
        using var healthyClient = new TcpClient();
        await healthyClient.ConnectAsync(IPAddress.Loopback, port);
        byte[] ping = JsonSerializer.SerializeToUtf8Bytes(SessionFrame.CreatePing(), SessionFrameSerializer.Options);
        byte[] pingFrame = [.. BitConverter.GetBytes(ping.Length), .. ping];
        await healthyClient.GetStream().WriteAsync(pingFrame);

        await healthyPing.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
