using System.Net;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Tcp.Connections;

internal sealed partial class ResilientCompositSessionConnection : IAsyncDisposable
{
    public ResilientInboundConnection InboundConnection { get; }
    public ResilientOutboundConnection OutboundConnection { get; }
    public string SessionToken => InboundConnection.SessionToken;
    public DnsEndPoint RemoteEndPoint => OutboundConnection.RemoteEndPoint;

    public ResilientCompositSessionConnection(
        Guid userId,
        string host,
        int port,
        ResilientSessionOptions? options = null)
        : this(new(userId, host, port), options)
    {
    }

    internal ResilientCompositSessionConnection(
        SessionKey sessionKey,
        ResilientSessionOptions? options = null)
    {
        OutboundConnection = new(options!, sessionKey);
        InboundConnection = new ResilientInboundConnection(options!, sessionKey, OutboundConnection);
    }

    public static async Task CancelAndDisposeTokenSource(CancellationTokenSource? cts)
    {
        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                await Console.Error.WriteLineAsync($"CTS already disposed");
                cts = null;
            }
            finally
            {
                cts?.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await OutboundConnection.DisposeAsync();
        await InboundConnection.DisposeAsync();
    }

    internal void RecordRemoteActivity()
    {
        InboundConnection.RecordRemoteActivity();
    }
}
