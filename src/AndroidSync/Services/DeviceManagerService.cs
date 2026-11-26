using Com.AndroidSync.App.Models;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Services;

public sealed class DeviceManagerService : IDeviceManagerService
{
    private readonly IAdbService _adbService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<DeviceManagerService> _logger;

    public DeviceManagerService(
        IAdbService adbService,
        ISettingsService settingsService,
        ILogger<DeviceManagerService> logger)
    {
        _adbService = adbService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public Task<IReadOnlyList<AndroidDevice>> RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        return _adbService.GetDevicesAsync(cancellationToken);
    }

    public Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onDevicesChanged, CancellationToken cancellationToken)
    {
        return _adbService.TrackDevicesAsync(onDevicesChanged, cancellationToken);
    }

    public async Task<(bool Success, string Message)> SwitchUsbDeviceToWirelessAsync(
        AndroidDevice device,
        int port = 5555,
        CancellationToken cancellationToken = default)
    {
        if (device.ConnectionType != DeviceConnectionType.Usb)
        {
            return (false, "此裝置已經是無線連線。");
        }

        var ip = await _adbService.GetDeviceWifiIpAsync(device.Serial, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ip))
        {
            return (false, "無法取得手機 Wi-Fi IP，請確認手機與電腦在相同區域網路。");
        }

        var tcpResult = await _adbService.EnableTcpIpAsync(device.Serial, port, cancellationToken).ConfigureAwait(false);
        if (!tcpResult.IsSuccess)
        {
            return (false, $"啟用無線偵錯連接埠失敗：{tcpResult.StandardError}");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
        return await ConnectWirelessDeviceAsync(ip, port, device.Model, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool Success, string Message)> PairWirelessDeviceAsync(
        string host,
        int port,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        var result = await _adbService.PairWirelessAsync(host.Trim(), port, pairingCode.Trim(), cancellationToken)
            .ConfigureAwait(false);

        var output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        var ok = result.IsSuccess && output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase);

        if (!ok)
        {
            _logger.LogWarning("無線配對失敗 ({Host}:{Port}): {Output}", host, port, output);
            return (false, string.IsNullOrWhiteSpace(output) ? "無線配對失敗。" : output);
        }

        return (true, output);
    }

    public async Task<(bool Success, string Message)> ConnectWirelessDeviceAsync(
        string host,
        int port,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedHost = host.Trim();
        var result = await _adbService.ConnectWirelessAsync(trimmedHost, port, cancellationToken).ConfigureAwait(false);
        var output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;

        var ok = result.IsSuccess
            && (output.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                || output.Contains("already connected", StringComparison.OrdinalIgnoreCase));

        if (!ok)
        {
            _logger.LogWarning("無線連線失敗 ({Host}:{Port}): {Output}", trimmedHost, port, output);
            return (false, string.IsNullOrWhiteSpace(output) ? "無線連線失敗。" : output);
        }

        await SaveWirelessDeviceAsync(trimmedHost, port, label, cancellationToken).ConfigureAwait(false);
        return (true, output);
    }

    public async Task<(bool Success, string Message)> DisconnectWirelessDeviceAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        var result = await _adbService.DisconnectWirelessAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        return (result.IsSuccess, string.IsNullOrWhiteSpace(output) ? "已中斷無線連線。" : output);
    }

    private async Task SaveWirelessDeviceAsync(string host, int port, string? label, CancellationToken cancellationToken)
    {
        var list = _settingsService.Current.SavedWirelessDevices;
        var existing = list.FirstOrDefault(d =>
            string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase) && d.Port == port);

        if (existing is not null)
        {
            existing.LastConnectedUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(label))
            {
                existing.Label = label;
            }
        }
        else
        {
            list.Add(new SavedWirelessDevice
            {
                Host = host,
                Port = port,
                Label = label ?? host,
                LastConnectedUtc = DateTimeOffset.UtcNow
            });
        }

        await _settingsService.SaveAsync(cancellationToken).ConfigureAwait(false);
    }
}
