using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using NUnit.Framework;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.UnitTests.Sessions;

[TestFixture]
public class SessionFrameIOTests
{
    [Test]
    public async Task ReadFrameAsync_ParsesPayload_FromMemoryStreamBackedNetworkStream()
    {
        var frame = SessionFrame.CreateEventFrame(Guid.NewGuid(), "{\"value\":42}");
        var bytes = BuildLengthPrefixedPayload(frame);
        using var context = NetworkStreamTestContext.FromBytes(bytes);
        var lengthBuffer = new byte[4];

        var result = await SessionFrameIO.ReadFrameAsync(context.Stream, lengthBuffer, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Frame.Should().NotBeNull();
        result.Frame!.Kind.Should().Be(SessionFrameKind.Event);
        result.Frame.Id.Should().Be(frame.Id);
        result.Frame.Payload.Should().Be(frame.Payload);
    }

    [Test]
    public async Task ReadFrameAsync_ReturnsFailure_WhenStreamEndsBeforePayloadCompletes()
    {
        var frame = SessionFrame.CreatePing();
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var truncatedPayload = payload[..(payload.Length - 1)];
        var header = BitConverter.GetBytes(payload.Length);
        var bytes = header.Concat(truncatedPayload).ToArray();
        using var context = NetworkStreamTestContext.FromBytes(bytes);
        var lengthBuffer = new byte[4];

        var result = await SessionFrameIO.ReadFrameAsync(context.Stream, lengthBuffer, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Frame.Should().BeNull();
    }

    [Test]
    public async Task ReadFrameAsync_ReturnsFailure_ForZeroOrNegativeLengths()
    {
        var zeroBytes = BitConverter.GetBytes(0);
        using var zeroContext = NetworkStreamTestContext.FromBytes(zeroBytes);
        var buffer = new byte[4];
        var zeroResult = await SessionFrameIO.ReadFrameAsync(zeroContext.Stream, buffer, CancellationToken.None);
        zeroResult.IsSuccess.Should().BeFalse();

        var negativeBytes = BitConverter.GetBytes(-10);
        using var negativeContext = NetworkStreamTestContext.FromBytes(negativeBytes);
        buffer = new byte[4];
        var negativeResult = await SessionFrameIO.ReadFrameAsync(negativeContext.Stream, buffer, CancellationToken.None);
        negativeResult.IsSuccess.Should().BeFalse();
    }

    private static byte[] BuildLengthPrefixedPayload(SessionFrame frame)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SessionFrameSerializer.Options);
        var length = BitConverter.GetBytes(payload.Length);
        return [.. length, .. payload];
    }

    private sealed class NetworkStreamTestContext : IDisposable
    {
        private readonly TcpClient _client;

        private NetworkStreamTestContext(TcpClient client)
        {
            _client = client;
            Stream = client.GetStream();
        }

        public NetworkStream Stream { get; }

        public static NetworkStreamTestContext FromBytes(byte[] bytes)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;

            var client = new TcpClient();
            client.Connect(endpoint.Address, endpoint.Port);
            using var server = listener.AcceptTcpClient();
            listener.Stop();

            using (var writer = server.GetStream())
            using (var source = new MemoryStream(bytes))
            {
                source.CopyTo(writer);
                writer.Flush();
            }

            server.Close();
            return new NetworkStreamTestContext(client);
        }

        public void Dispose()
        {
            Stream.Dispose();
            _client.Dispose();
        }
    }
}
