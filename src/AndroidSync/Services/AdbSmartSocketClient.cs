using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Com.AndroidSync.App.Models;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Services;

public sealed class AdbSmartSocketClient
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 5037;

    private readonly ILogger<AdbSmartSocketClient> _logger;

    public AdbSmartSocketClient(ILogger<AdbSmartSocketClient> logger)
    {
        _logger = logger;
    }

    public static byte[] FormatRequest(string command)
    {
        var payload = Encoding.ASCII.GetBytes(command);
        var header = payload.Length.ToString("X4", CultureInfo.InvariantCulture);
        var result = new byte[4 + payload.Length];
        Encoding.ASCII.GetBytes(header, result.AsSpan(0, 4));
        payload.CopyTo(result, 4);
        return result;
    }

    public static bool TryParseHexLength(ReadOnlySpan<byte> fourBytes, out int length)
    {
        length = 0;
        if (fourBytes.Length < 4)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(fourBytes[..4]);
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out length) && length >= 0;
    }

    public async Task<IReadOnlyList<AndroidDevice>?> TryQueryDevicesAsync(
        CancellationToken cancellationToken = default,
        string host = DefaultHost,
        int port = DefaultPort)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            var req = FormatRequest("host:devices-l");
            await stream.WriteAsync(req, cancellationToken).ConfigureAwait(false);

            var status = new byte[4];
            if (!await TryReadExactAsync(stream, status, cancellationToken).ConfigureAwait(false)
                || Encoding.ASCII.GetString(status) != "OKAY")
            {
                return null;
            }

            var payload = await ReadLengthPrefixedStringAsync(stream, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return null;
            }

            return AdbService.ParseDevicesOutput(payload);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "ADB Smart Socket 查詢未就緒，將退回 CLI 模式");
            return null;
        }
    }

    public Task<AdbCommandResult?> TryForwardPortAsync(
        string serial,
        int localPort,
        string remoteAbstractSocket,
        CancellationToken cancellationToken = default,
        string host = DefaultHost,
        int port = DefaultPort)
    {
        var command = $"host-serial:{serial}:forward:tcp:{localPort};localabstract:{remoteAbstractSocket}";
        return TryExecuteHostStatusCommandAsync(command, expectSecondStatus: true, cancellationToken, host, port);
    }

    public Task<AdbCommandResult?> TryRemoveForwardAsync(
        string serial,
        int localPort,
        CancellationToken cancellationToken = default,
        string host = DefaultHost,
        int port = DefaultPort)
    {
        var command = $"host-serial:{serial}:killforward:tcp:{localPort}";
        return TryExecuteHostStatusCommandAsync(command, expectSecondStatus: true, cancellationToken, host, port);
    }

    private async Task<AdbCommandResult?> TryExecuteHostStatusCommandAsync(
        string command,
        bool expectSecondStatus,
        CancellationToken cancellationToken,
        string host,
        int port)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            await stream.WriteAsync(FormatRequest(command), cancellationToken).ConfigureAwait(false);

            var status = new byte[4];
            if (!await TryReadExactAsync(stream, status, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (Encoding.ASCII.GetString(status) != "OKAY")
            {
                var err = await ReadLengthPrefixedStringAsync(stream, cancellationToken).ConfigureAwait(false);
                return new AdbCommandResult(1, string.Empty, err ?? "ADB daemon 回傳錯誤");
            }

            if (expectSecondStatus)
            {
                if (!await TryReadExactAsync(stream, status, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                if (Encoding.ASCII.GetString(status) != "OKAY")
                {
                    var err = await ReadLengthPrefixedStringAsync(stream, cancellationToken).ConfigureAwait(false);
                    return new AdbCommandResult(1, string.Empty, err ?? "ADB daemon 轉發命令失敗");
                }
            }

            return new AdbCommandResult(0, string.Empty, string.Empty);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "ADB Smart Socket 命令未就緒，將退回 CLI 模式: {Command}", command);
            return null;
        }
    }

    public async Task TrackDevicesAsync(
        Action<IReadOnlyList<AndroidDevice>> onDevicesChanged,
        CancellationToken cancellationToken,
        string host = DefaultHost,
        int port = DefaultPort)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();

                // Try extended format first ("host:track-devices-l"), fall back to "host:track-devices"
                var command = "host:track-devices-l";
                await stream.WriteAsync(FormatRequest(command), cancellationToken).ConfigureAwait(false);

                var status = new byte[4];
                if (!await TryReadExactAsync(stream, status, cancellationToken).ConfigureAwait(false))
                {
                    await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var statusStr = Encoding.ASCII.GetString(status);
                if (statusStr != "OKAY")
                {
                    // Reconnect using legacy host:track-devices
                    client.Dispose();
                    await TrackLegacyDevicesLoopAsync(onDevicesChanged, cancellationToken, host, port).ConfigureAwait(false);
                    continue;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var payload = await ReadLengthPrefixedStringAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (payload is null)
                    {
                        break;
                    }

                    var devices = AdbService.ParseDevicesOutput(payload);
                    onDevicesChanged(devices);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                _logger.LogDebug(ex, "ADB track-devices 連線中斷，將於稍後重試");
                try
                {
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task TrackLegacyDevicesLoopAsync(
        Action<IReadOnlyList<AndroidDevice>> onDevicesChanged,
        CancellationToken cancellationToken,
        string host,
        int port)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();

        await stream.WriteAsync(FormatRequest("host:track-devices"), cancellationToken).ConfigureAwait(false);
        var status = new byte[4];
        if (!await TryReadExactAsync(stream, status, cancellationToken).ConfigureAwait(false)
            || Encoding.ASCII.GetString(status) != "OKAY")
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await ReadLengthPrefixedStringAsync(stream, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                break;
            }

            // Enrich with full model info via host:devices-l if available
            var detailed = await TryQueryDevicesAsync(cancellationToken, host, port).ConfigureAwait(false);
            onDevicesChanged(detailed ?? AdbService.ParseDevicesOutput(payload));
        }
    }

    private static async Task<string?> ReadLengthPrefixedStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lenBuf = new byte[4];
        if (!await TryReadExactAsync(stream, lenBuf, cancellationToken).ConfigureAwait(false)
            || !TryParseHexLength(lenBuf, out var length))
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var payloadBuf = new byte[length];
        if (!await TryReadExactAsync(stream, payloadBuf, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Encoding.UTF8.GetString(payloadBuf);
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
