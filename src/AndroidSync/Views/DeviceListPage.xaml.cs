using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Services;
using Com.AndroidSync.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Com.AndroidSync.App.Views;

public sealed partial class DeviceListPage : Page
{
    private readonly IDeviceManagerService _deviceManager;
    private CancellationTokenSource? _trackingCts;

    public DeviceListPage()
    {
        ViewModel = App.GetService<DeviceListViewModel>();
        _deviceManager = App.GetService<IDeviceManagerService>();
        InitializeComponent();
    }

    public DeviceListViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();

        _trackingCts?.Cancel();
        _trackingCts?.Dispose();
        _trackingCts = new CancellationTokenSource();
        var token = _trackingCts.Token;

        _ = Task.Run(() => _deviceManager.TrackDevicesAsync(
            devices => DispatcherQueue.TryEnqueue(() => ViewModel.ApplyDeviceList(devices)),
            token), token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _trackingCts?.Cancel();
        _trackingCts?.Dispose();
        _trackingCts = null;
    }

    private void StartMirrorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AndroidDevice device })
        {
            ViewModel.StartMirrorCommand.Execute(device);
        }
    }

    private async void SwitchWirelessButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AndroidDevice device })
        {
            await ViewModel.SwitchToWirelessCommand.ExecuteAsync(device);
        }
    }

    private async void ReconnectSavedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SavedWirelessDevice saved })
        {
            await ViewModel.ReconnectSavedCommand.ExecuteAsync(saved);
        }
    }
}
