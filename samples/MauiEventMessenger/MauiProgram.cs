using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MauiEventMessenger.Events;
using MauiEventMessenger.ViewModels;
using Yaref92.Events;
using Yaref92.Events.Transports;

namespace MauiEventMessenger;

public static class MauiProgram
{
    private const int DefaultListenPort = 5050;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton(new MessengerSettings { ListenPort = DefaultListenPort });

        builder.Services.AddSingleton(provider => new TCPEventTransport(DefaultListenPort));
        builder.Services.AddSingleton<EventAggregator>();
        builder.Services.AddSingleton(provider =>
        {
            var localAggregator = provider.GetRequiredService<EventAggregator>();
            var transport = provider.GetRequiredService<TCPEventTransport>();
            var aggregator = new NetworkedEventAggregator(localAggregator, transport, ownsLocalAggregator: false, ownsTransport: false);
            aggregator.RegisterEventType<MessageEvent>();
            return aggregator;
        });

        return builder.Build();
    }
}
