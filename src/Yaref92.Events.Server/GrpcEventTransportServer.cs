using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Yaref92.Events.Transports;

namespace Yaref92.Events.Server;

public sealed class GrpcEventTransportServer : IAsyncDisposable
{
    private readonly GrpcEventTransport _transport;
    private IHost? _host;
    private Task? _disposeTask;
    private int _disposeState;

    public GrpcEventTransportServer(GrpcEventTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_host is not null)
        {
            return Task.CompletedTask;
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(_transport.ListenPort, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddGrpc();
                    services.AddSingleton(_transport);
                    services.AddSingleton<EventStreamService>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<EventStreamService>();
                    });
                });
            })
            .Build();

        return _host.StartAsync(cancellationToken);
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
        if (_host is null)
        {
            return;
        }

        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
        _host = null;
    }
}
