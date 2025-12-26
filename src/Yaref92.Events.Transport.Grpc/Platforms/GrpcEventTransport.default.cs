#if NOT_ANDROID
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport
{
    private IHost? _host;

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
                    options.ListenAnyIP(_listenPort, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddGrpc();
                    services.AddSingleton(this);
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

    private async Task DisposeAsyncCore()
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
            _host = null;
        }

        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }
}
#endif
