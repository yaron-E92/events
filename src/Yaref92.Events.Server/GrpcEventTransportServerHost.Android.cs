#if ANDROID
using Grpc.Core;

using Yaref92.Events.Transports;

namespace Yaref92.Events.Server;

public sealed partial class GrpcEventTransportServer
{
    private static partial IGrpcEventTransportServerHost CreateServerHost(GrpcEventTransport transport)
    {
        return new GrpcCoreEventTransportServerHost(transport);
    }
}

internal sealed class GrpcCoreEventTransportServerHost : IGrpcEventTransportServerHost
{
    private readonly GrpcEventTransport _transport;
    private Server? _server;
    private Task? _disposeTask;
    private int _disposeState;

    public GrpcCoreEventTransportServerHost(GrpcEventTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public Task StartListeningAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            return Task.CompletedTask;
        }

        _server = new Server
        {
            Services = { global::EventStream.BindService(new EventStreamService(_transport)) },
            Ports = { new ServerPort("0.0.0.0", _transport.ListenPort, ServerCredentials.Insecure) },
        };

        _server.Start();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
        {
            _disposeTask = DisposeAsyncCore();
        }

        return _disposeTask is null ? ValueTask.CompletedTask : new ValueTask(_disposeTask);
    }

    private async Task DisposeAsyncCore()
    {
        if (_server is null)
        {
            return;
        }

        await _server.ShutdownAsync().ConfigureAwait(false);
        _server = null;
    }
}
#endif
