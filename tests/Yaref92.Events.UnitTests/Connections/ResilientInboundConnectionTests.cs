using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Connections;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.UnitTests.Connections;

[TestFixture]
public class ResilientInboundConnectionTests
{
    [Test]
    public async Task RunInboundAsync_AllowsSubsequentAttachmentsAfterDisconnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

        var options = new ResilientSessionOptions();
        var sessionKey = new SessionKey(Guid.NewGuid(), listenerEndPoint.Address.ToString(), listenerEndPoint.Port);

        await using var outboundConnection = new ResilientOutboundConnection(options, sessionKey);
        var inboundConnection = new ResilientInboundConnection(options, sessionKey, outboundConnection);
        IInboundResilientConnection inbound = inboundConnection;

        await inbound.InitAsync(CancellationToken.None).ConfigureAwait(false);

        var frameReceived = new TaskCompletionSource<SessionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionKey? observedSessionKey = null;

        Task FrameHandler(SessionFrame frame, SessionKey key, CancellationToken cancellationToken)
        {
            observedSessionKey = key;
            frameReceived.TrySetResult(frame);
            return Task.CompletedTask;
        }

        inbound.FrameReceived += FrameHandler;

        CancellationTokenSource? firstAttachmentCts = null;
        CancellationTokenSource? secondAttachmentCts = null;
        TcpClient? firstServerConnection = null;
        TcpClient? secondServerConnection = null;

        try
        {
            using (var firstClient = new TcpClient())
            {
                await firstClient.ConnectAsync(listenerEndPoint.Address, listenerEndPoint.Port).ConfigureAwait(false);
                firstServerConnection = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                firstAttachmentCts = new CancellationTokenSource();

                await inbound.AttachTransientConnection(firstServerConnection, firstAttachmentCts).ConfigureAwait(false);

                CloseTcpClient(firstClient);
            }

            await WaitForDisconnectAsync(firstServerConnection!, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            using var secondClient = new TcpClient();
            await secondClient.ConnectAsync(listenerEndPoint.Address, listenerEndPoint.Port).ConfigureAwait(false);
            secondServerConnection = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            secondAttachmentCts = new CancellationTokenSource();

            await inbound.AttachTransientConnection(secondServerConnection, secondAttachmentCts).ConfigureAwait(false);

            var payload = JsonSerializer.Serialize(new { message = "after-reconnect" });
            var eventFrame = SessionFrame.CreateEventFrame(Guid.NewGuid(), payload);

            await SendFrameAsync(secondClient.GetStream(), eventFrame, CancellationToken.None).ConfigureAwait(false);

            var receivedFrame = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Assert.That(receivedFrame.Kind, Is.EqualTo(SessionFrameKind.Event));
            Assert.That(receivedFrame.Payload, Is.EqualTo(payload));
            Assert.That(observedSessionKey, Is.EqualTo(sessionKey));
        }
        finally
        {
            inbound.FrameReceived -= FrameHandler;

            await inboundConnection.DisposeAsync().ConfigureAwait(false);
            await outboundConnection.DisposeAsync().ConfigureAwait(false);

            secondServerConnection?.Dispose();
            firstServerConnection?.Dispose();
            secondAttachmentCts?.Dispose();
            firstAttachmentCts?.Dispose();
            listener.Stop();
        }
    }

    private static async Task SendFrameAsync(NetworkStream stream, SessionFrame frame, CancellationToken cancellationToken)
    {
        var serialized = JsonSerializer.Serialize(frame, SessionFrameSerializer.Options);
        var payload = Encoding.UTF8.GetBytes(serialized);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);

        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForDisconnectAsync(TcpClient client, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(client);

        var stopwatch = Stopwatch.StartNew();
        while (!HasDisconnected(client))
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The transient connection did not disconnect as expected.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static bool HasDisconnected(TcpClient client)
    {
        try
        {
            if (!client.Connected)
            {
                return true;
            }

            var socket = client.Client;
            if (socket is null)
            {
                return true;
            }

            return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void CloseTcpClient(TcpClient client)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        client.Dispose();
    }
}
