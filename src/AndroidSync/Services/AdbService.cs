using System.Diagnostics;
using System.Text.RegularExpressions;
using Com.AndroidSync.App.Models;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Services;

public sealed partial class AdbService : IAdbService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AdbService> _logger;
    private readonly AdbSmartSocketClient _smartSocket;
    private string? _cachedAdbPath;
    private string? _cachedAdbConfigured;
    private string? _cachedScrcpyPath;
    private string? _cachedScrcpyConfigured;

    public AdbService(
        ISettingsService settingsService,
        ILogger<AdbService> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _settingsService = settingsService;
        _logger = logger;
        var socketLogger = loggerFactory?.CreateLogger<AdbSmartSocketClient>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AdbSmartSocketClient>.Instance;
        _smartSocket = new AdbSmartSocketClient(socketLogger);
    }

    public string? ResolveAdbPath()
    {
        var configured = _settingsService.Current.AdbPath;
        if (string.Equals(configured, _cachedAdbConfigured, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(_cachedAdbPath)
            && File.Exists(_cachedAdbPath))
        {
            return _cachedAdbPath;
        }

        _cachedAdbConfigured = configured;
        _cachedAdbPath = ResolveAdbPathUncached(configured);
        return _cachedAdbPath;
    }

    private static string? ResolveAdbPathUncached(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var localBundled = Path.Combine(AppContext.BaseDirectory, "Tools", "adb.exe");
        if (File.Exists(localBundled))
        {
            return localBundled;
        }

        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(androidHome))
        {
            var sdkAdb = Path.Combine(androidHome, "platform-tools", "adb.exe");
            if (File.Exists(sdkAdb))
            {
                return sdkAdb;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultSdkAdb = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(defaultSdkAdb))
        {
            return defaultSdkAdb;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public string? ResolveScrcpyServerPath()
    {
        var configured = _settingsService.Current.ScrcpyServerPath;
        if (string.Equals(configured, _cachedScrcpyConfigured, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(_cachedScrcpyPath)
            && File.Exists(_cachedScrcpyPath))
        {
            return _cachedScrcpyPath;
        }

        _cachedScrcpyConfigured = configured;
        _cachedScrcpyPath = ResolveScrcpyServerPathUncached(configured);
        return _cachedScrcpyPath;
    }

    private static string? ResolveScrcpyServerPathUncached(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "scrcpy-server");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var bundledJar = Path.Combine(AppContext.BaseDirectory, "Tools", "scrcpy-server.jar");
        if (File.Exists(bundledJar))
        {
            return bundledJar;
        }

        return null;
    }

    public async Task<IReadOnlyList<AndroidDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var viaSocket = await _smartSocket.TryQueryDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (viaSocket is not null)
        {
            return viaSocket;
        }

        var result = await RunAdbAsync("devices -l", cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("執行 adb devices 失敗: {Error}", result.StandardError);
            return [];
        }

        return ParseDevicesOutput(result.StandardOutput);
    }

    public Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onDevicesChanged, CancellationToken cancellationToken)
    {
        return _smartSocket.TrackDevicesAsync(onDevicesChanged, cancellationToken);
    }

    public Task<AdbCommandResult> PairWirelessAsync(string host, int port, string pairingCode, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync($"pair {host}:{port} {pairingCode}", cancellationToken);
    }

    public Task<AdbCommandResult> ConnectWirelessAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync($"connect {host}:{port}", cancellationToken);
    }

    public Task<AdbCommandResult> DisconnectWirelessAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync($"disconnect {endpoint}", cancellationToken);
    }

    public Task<AdbCommandResult> EnableTcpIpAsync(string serial, int port = 5555, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync($"-s \"{serial}\" tcpip {port}", cancellationToken);
    }

    public async Task<string?> GetDeviceWifiIpAsync(string serial, CancellationToken cancellationToken = default)
    {
        var routeResult = await RunAdbAsync($"-s \"{serial}\" shell ip route", cancellationToken).ConfigureAwait(false);
        if (routeResult.IsSuccess)
        {
            var match = IpRouteSrcRegex().Match(routeResult.StandardOutput);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        var addrResult = await RunAdbAsync($"-s \"{serial}\" shell ip -f inet addr show wlan0", cancellationToken).ConfigureAwait(false);
        if (addrResult.IsSuccess)
        {
            var match = InetAddrRegex().Match(addrResult.StandardOutput);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    public Task<AdbCommandResult> PushFileAsync(string serial, string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync($"-s \"{serial}\" push \"{localPath}\" \"{remotePath}\"", cancellationToken);
    }

    public Task<AdbCommandResult> InstallApkAsync(string serial, string apkPath, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync($"-s \"{serial}\" install -r \"{apkPath}\"", cancellationToken);
    }

    public Task<AdbCommandResult> ScanMediaFileAsync(string serial, string remotePath, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync(
            $"-s \"{serial}\" shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d \"file://{remotePath}\"",
            cancellationToken);
    }

    public async Task<AdbCommandResult> ForwardPortAsync(string serial, int localPort, string remoteAbstractSocket, CancellationToken cancellationToken = default)
    {
        var viaSocket = await _smartSocket.TryForwardPortAsync(serial, localPort, remoteAbstractSocket, cancellationToken)
            .ConfigureAwait(false);
        if (viaSocket is not null)
        {
            return viaSocket;
        }

        return await RunAdbAsync($"-s \"{serial}\" forward tcp:{localPort} localabstract:{remoteAbstractSocket}", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AdbCommandResult> RemoveForwardAsync(string serial, int localPort, CancellationToken cancellationToken = default)
    {
        var viaSocket = await _smartSocket.TryRemoveForwardAsync(serial, localPort, cancellationToken)
            .ConfigureAwait(false);
        if (viaSocket is not null)
        {
            return viaSocket;
        }

        return await RunAdbAsync($"-s \"{serial}\" forward --remove tcp:{localPort}", cancellationToken)
            .ConfigureAwait(false);
    }

    public Process StartDeviceShellProcess(string serial, string shellCommand)
    {
        var adbPath = ResolveAdbPath()
            ?? throw new FileNotFoundException("找不到 adb.exe，請確認已安裝 Android Platform Tools 或於設定指定路徑。");

        var startInfo = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = $"-s \"{serial}\" shell {shellCommand}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    public static IReadOnlyList<AndroidDevice> ParseDevicesOutput(string output)
    {
        var devices = new List<AndroidDevice>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)
                || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith('*'))
            {
                continue;
            }

            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var serial = tokens[0];
            var status = tokens[1] switch
            {
                "device" => DeviceStatus.Online,
                "unauthorized" => DeviceStatus.Unauthorized,
                "offline" => DeviceStatus.Offline,
                _ => DeviceStatus.Unknown
            };

            var model = string.Empty;
            var product = string.Empty;

            for (var i = 2; i < tokens.Length; i++)
            {
                if (tokens[i].StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                {
                    model = tokens[i]["model:".Length..].Replace('_', ' ');
                }
                else if (tokens[i].StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                {
                    product = tokens[i]["product:".Length..];
                }
            }

            var connectionType = serial.Contains(':')
                ? DeviceConnectionType.Wireless
                : DeviceConnectionType.Usb;

            devices.Add(new AndroidDevice(serial, model, product, status, connectionType));
        }

        return devices;
    }

    private async Task<AdbCommandResult> RunAdbAsync(string arguments, CancellationToken cancellationToken)
    {
        var adbPath = ResolveAdbPath();
        if (string.IsNullOrEmpty(adbPath))
        {
            return new AdbCommandResult(-1, string.Empty, "找不到 adb.exe，請先安裝 Android Platform Tools 或於設定指定路徑。");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new AdbCommandResult(process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "終止已取消的 ADB 行程時發生錯誤");
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "執行 ADB 指令發生例外: {Arguments}", arguments);
            return new AdbCommandResult(-1, string.Empty, ex.Message);
        }
    }

    [GeneratedRegex(@"src\s+(\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.Compiled)]
    private static partial Regex IpRouteSrcRegex();

    [GeneratedRegex(@"inet\s+(\d{1,3}(?:\.\d{1,3}){3})/", RegexOptions.Compiled)]
    private static partial Regex InetAddrRegex();
}
