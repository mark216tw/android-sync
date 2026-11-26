using System.Collections.ObjectModel;
using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Com.AndroidSync.App.ViewModels;

public sealed partial class DeviceListViewModel : ObservableObject
{
    private readonly IDeviceManagerService _deviceManager;
    private readonly ISettingsService _settingsService;
    private readonly IAdbService _adbService;

    public DeviceListViewModel(
        IDeviceManagerService deviceManager,
        ISettingsService settingsService,
        IAdbService adbService)
    {
        _deviceManager = deviceManager;
        _settingsService = settingsService;
        _adbService = adbService;
    }

    public ObservableCollection<AndroidDevice> Devices { get; } = [];

    public ObservableCollection<SavedWirelessDevice> SavedWirelessDevices { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsErrorStatus { get; set; }

    [ObservableProperty]
    public partial string WirelessHost { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WirelessPort { get; set; } = "5555";

    [ObservableProperty]
    public partial string PairPort { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PairCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EnvironmentWarning { get; set; } = string.Empty;

    public bool HasDevices => Devices.Count > 0;

    public bool HasSavedWirelessDevices => SavedWirelessDevices.Count > 0;

    public event EventHandler<AndroidDevice>? MirrorRequested;

    public void ApplyDeviceList(IReadOnlyList<AndroidDevice> list)
    {
        Devices.Clear();
        foreach (var device in list)
        {
            Devices.Add(device);
        }

        ReloadSavedWirelessDevices();
        OnPropertyChanged(nameof(HasDevices));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            UpdateEnvironmentCheck();
            var list = await _deviceManager.RefreshDevicesAsync();
            Devices.Clear();
            foreach (var device in list)
            {
                Devices.Add(device);
            }

            ReloadSavedWirelessDevices();
            OnPropertyChanged(nameof(HasDevices));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StartMirror(AndroidDevice? device)
    {
        if (device is null)
        {
            return;
        }

        if (!device.IsReady)
        {
            ShowStatus("裝置尚未授權或離線，請先在手機上確認允許 USB 偵錯。", isError: true);
            return;
        }

        MirrorRequested?.Invoke(this, device);
    }

    [RelayCommand]
    private async Task SwitchToWirelessAsync(AndroidDevice? device)
    {
        if (device is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (success, message) = await _deviceManager.SwitchUsbDeviceToWirelessAsync(device);
            ShowStatus(message, isError: !success);
            if (success)
            {
                await RefreshInternalAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectWirelessAsync()
    {
        if (string.IsNullOrWhiteSpace(WirelessHost) || !int.TryParse(WirelessPort, out var port) || port <= 0)
        {
            ShowStatus("請輸入有效的手機 IP 位址與連線連接埠。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var (success, message) = await _deviceManager.ConnectWirelessDeviceAsync(WirelessHost, port);
            ShowStatus(message, isError: !success);
            if (success)
            {
                await RefreshInternalAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PairWirelessAsync()
    {
        if (string.IsNullOrWhiteSpace(WirelessHost)
            || !int.TryParse(PairPort, out var port)
            || port <= 0
            || string.IsNullOrWhiteSpace(PairCode))
        {
            ShowStatus("請輸入手機 IP、配對連接埠與 6 位數配對碼。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var (success, message) = await _deviceManager.PairWirelessDeviceAsync(WirelessHost, port, PairCode);
            ShowStatus(message, isError: !success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReconnectSavedAsync(SavedWirelessDevice? saved)
    {
        if (saved is null)
        {
            return;
        }

        WirelessHost = saved.Host;
        WirelessPort = saved.Port.ToString();
        await ConnectWirelessAsync();
    }

    private async Task RefreshInternalAsync()
    {
        var list = await _deviceManager.RefreshDevicesAsync();
        Devices.Clear();
        foreach (var device in list)
        {
            Devices.Add(device);
        }

        ReloadSavedWirelessDevices();
        OnPropertyChanged(nameof(HasDevices));
    }

    private void ReloadSavedWirelessDevices()
    {
        SavedWirelessDevices.Clear();
        foreach (var item in _settingsService.Current.SavedWirelessDevices.OrderByDescending(x => x.LastConnectedUtc))
        {
            SavedWirelessDevices.Add(item);
        }

        OnPropertyChanged(nameof(HasSavedWirelessDevices));
    }

    private void UpdateEnvironmentCheck()
    {
        var missing = new List<string>();
        if (_adbService.ResolveAdbPath() is null)
        {
            missing.Add("adb.exe");
        }

        if (_adbService.ResolveScrcpyServerPath() is null)
        {
            missing.Add("scrcpy-server");
        }

        EnvironmentWarning = missing.Count > 0
            ? $"尚未偵測到 {string.Join("、", missing)}，請放置於 Tools 資料夾或前往「設定」指定路徑。"
            : string.Empty;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsErrorStatus = isError;
    }
}
