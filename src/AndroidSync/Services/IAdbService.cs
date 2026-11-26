using System.Diagnostics;
using Com.AndroidSync.App.Models;

namespace Com.AndroidSync.App.Services;

public interface IAdbService
{
    string? ResolveAdbPath();

    string? ResolveScrcpyServerPath();

    Task<IReadOnlyList<AndroidDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<AdbCommandResult> PairWirelessAsync(string host, int port, string pairingCode, CancellationToken cancellationToken = default);

    Task<AdbCommandResult> ConnectWirelessAsync(string host, int port, CancellationToken cancellationToken = default);

    Task<AdbCommandResult> DisconnectWirelessAsync(string endpoint, CancellationToken cancellationToken = default);

    Task<AdbCommandResult> EnableTcpIpAsync(string serial, int port = 5555, CancellationToken cancellationToken = default);

    Task<string?> GetDeviceWifiIpAsync(string serial, CancellationToken cancellationToken = default);

    Task<AdbCommandResult> PushFileAsync(string serial, string localPath, string remotePath, CancellationToken cancellationToken = default);

    Task<AdbCommandResult> InstallApkAsync(string serial, string apkPath, CancellationToken cancellationToken = default);

    Task<AdbCommandResult> ScanMediaFileAsync(string serial, string remotePath, CancellationToken cancellationToken = default);

    Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onDevicesChanged, CancellationToken cancellationToken);

    Task<AdbCommandResult> ForwardPortAsync(string serial, int localPort, string remoteAbstractSocket, CancellationToken cancellationToken = default);

    Task<AdbCommandResult> RemoveForwardAsync(string serial, int localPort, CancellationToken cancellationToken = default);

    Process StartDeviceShellProcess(string serial, string shellCommand);
}
