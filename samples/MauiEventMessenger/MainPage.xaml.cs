using MauiEventMessenger.ViewModels;
using Microsoft.Maui.Controls;

namespace MauiEventMessenger;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private async void OnStartListening(object sender, EventArgs e)
    {
        await _viewModel.StartListenerAsync();
    }

    private async void OnConnect(object sender, EventArgs e)
    {
        await _viewModel.ConnectToPeerAsync();
    }

    private async void OnSendMessage(object sender, EventArgs e)
    {
        await _viewModel.SendMessageAsync();
    }
}
