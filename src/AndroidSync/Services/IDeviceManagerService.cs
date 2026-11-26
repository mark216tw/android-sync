using Com.AndroidSync.App.Models;

namespace Com.AndroidSync.App.Services;

public interface IDeviceManagerService
{
    Task<IReadOnlyList<AndroidDevice>> RefreshDevicesAsync(CancellationToken cancellationToken = default);

    Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onDevicesChanged, CancellationToken cancellationToken);

    Task<(bool Success, string Message)> SwitchUsbDeviceToWirelessAsync(AndroidDevice device, int port = 5555, CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> PairWirelessDeviceAsync(string host, int port, string pairingCode, CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> ConnectWirelessDeviceAsync(string host, int port, string? label = null, CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> DisconnectWirelessDeviceAsync(string endpoint, CancellationToken cancellationToken = default);
}
