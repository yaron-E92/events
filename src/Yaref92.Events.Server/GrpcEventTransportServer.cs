using Yaref92.Events.Transports;

namespace Yaref92.Events.Server;

public sealed partial class GrpcEventTransportServer : IAsyncDisposable
{
    private readonly GrpcEventTransport _transport;
    private readonly IGrpcEventTransportServerHost _host;

    public GrpcEventTransportServer(GrpcEventTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _host = CreateServerHost(_transport);
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        return _host.StartListeningAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _host.DisposeAsync();
    }

    private static partial IGrpcEventTransportServerHost CreateServerHost(GrpcEventTransport transport);
}

internal interface IGrpcEventTransportServerHost : IAsyncDisposable
{
    Task StartListeningAsync(CancellationToken cancellationToken);
}
