using EventMessenger.Events;
using EventMessenger.ViewModels;

using Yaref92.Events;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace EventMessenger;

public static class MauiProgram
{
    private const int DefaultListenPort = 5050;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();
        int port = DefaultListenPort + Random.Shared.Next(1, 500);
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton(new MessengerSettings { ListenPort = port });

        builder.Services.AddSingleton(provider => new GrpcEventTransport(port, new SessionManager(port, new ResilientSessionOptions())));
        builder.Services.AddSingleton<EventAggregator>();
        builder.Services.AddSingleton(provider =>
        {
            var localAggregator = provider.GetRequiredService<EventAggregator>();
            var transport = provider.GetRequiredService<GrpcEventTransport>();
            var aggregator = new NetworkedEventAggregator(localAggregator, transport, ownsLocalAggregator: false, ownsTransport: false);
            aggregator.RegisterEventType<MessageEvent>();
            return aggregator;
        });

        return builder.Build();
    }
}
