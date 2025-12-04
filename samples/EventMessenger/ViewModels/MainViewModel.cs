using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

using EventMessenger.Events;

using Yaref92.Events;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports;

namespace EventMessenger.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly NetworkedEventAggregator _aggregator;
    private readonly TCPEventTransport _transport;
    private readonly MessengerSettings _settings;
    private bool _isListening;
    private string _peerHost = "localhost";
    private string _peerPort = "5050";
    private string _myPort = "5050";
    private string _messageText = string.Empty;

    public MainViewModel(NetworkedEventAggregator aggregator, TCPEventTransport transport, MessengerSettings settings)
    {
        _aggregator = aggregator;
        _transport = transport;
        _settings = settings;
        Messages = new ObservableCollection<string>();
        HostHint = ResolveLocalHost();

        _aggregator.SubscribeToEventType(new AsyncMessageEventHandler(this));
        _myPort = _settings.ListenPort.ToString(CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Messages { get; }

    public string HostHint { get; }

    public string PeerHost
    {
        get => _peerHost;
        set => SetProperty(ref _peerHost, value);
    }

    public string PeerPort
    {
        get => _peerPort;
        set => SetProperty(ref _peerPort, value);
    }

    public string MyPort
    {
        get => _myPort;
        set => SetProperty(ref _myPort, value);
    }

    public string MessageText
    {
        get => _messageText;
        set => SetProperty(ref _messageText, value);
    }

    public async Task StartListenerAsync()
    {
        if (_isListening)
        {
            return;
        }

        if (!string.Equals(MyPort, _settings.ListenPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            MyPort = _settings.ListenPort.ToString(CultureInfo.InvariantCulture);
        }

        await _transport.StartListeningAsync();
        _isListening = true;
        await ShowToastAsync($"Listening on {HostHint}:{MyPort}");
    }

    public async Task ConnectToPeerAsync()
    {
        if (!int.TryParse(PeerPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            await ShowToastAsync("Enter a valid peer port.");
            return;
        }

        await _transport.ConnectToPeerAsync(PeerHost, port);
        await ShowToastAsync($"Connected to {PeerHost}:{PeerPort}");
    }

    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageText))
        {
            return;
        }

        var outgoingEvent = new MessageEvent(HostHint, MessageText.Trim(), DateTime.UtcNow);
        await _aggregator.PublishEventAsync(outgoingEvent);
        Messages.Add($"Me: {outgoingEvent.Text} ({outgoingEvent.Timestamp:T} UTC)");
        MessageText = string.Empty;
    }

    internal async Task HandleIncomingMessageAsync(MessageEvent messageEvent)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            Messages.Add($"{messageEvent.Sender}: {messageEvent.Text} ({messageEvent.Timestamp:T} UTC)");
        });

        await ShowToastAsync($"Message from {messageEvent.Sender}");
    }

    public async Task InitializeAsync()
    {
        await StartListenerAsync();
    }

    private static string ResolveLocalHost()
    {
        try
        {
            string hostName = Dns.GetHostName();
            var entry = Dns.GetHostEntry(hostName);
            var address = entry.AddressList.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return address?.ToString() ?? hostName;
        }
        catch
        {
            return "localhost";
        }
    }

    private static Task ShowToastAsync(string message)
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Application.Current?.MainPage;
            if (page is not null)
            {
                await page.DisplayAlert("Notification", message, "OK");
            }
        });
    }

    protected void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class AsyncMessageEventHandler : IAsyncEventHandler<MessageEvent>
    {
        private readonly MainViewModel _viewModel;

        public AsyncMessageEventHandler(MainViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public Task OnNextAsync(MessageEvent domainEvent, CancellationToken cancellationToken = default)
        {
            return _viewModel.HandleIncomingMessageAsync(domainEvent);
        }
    }
}
