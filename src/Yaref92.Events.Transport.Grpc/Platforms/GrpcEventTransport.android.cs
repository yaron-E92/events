#if ANDROID
using Grpc.Core;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport
{
    private Server _server;

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            return Task.CompletedTask;
        }

        _server = new Server
        {
            Services = { global::EventStream.BindService(new EventStreamService(this)) },
            Ports = { new ServerPort("0.0.0.0", _listenPort, ServerCredentials.Insecure) },
        };

        _server.Start();
        return Task.CompletedTask;
    }

    private async Task DisposeAsyncCore()
    {
        if (_server is not null)
        {
            await _server.ShutdownAsync();
            _server = null;
        }

        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }
}
#endif
