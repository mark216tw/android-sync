using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Protocol;

public sealed class ScrcpyProtocolClient : IAsyncDisposable
{
    private const ulong PacketFlagConfigV2 = 1UL << 63;
    private const ulong PacketFlagKeyFrameV2 = 1UL << 62;
    private const ulong PacketPtsMaskV2 = ~(PacketFlagConfigV2 | PacketFlagKeyFrameV2);

    private const ulong PacketFlagSessionV5 = 1UL << 63;
    private const ulong PacketFlagConfigV5 = 1UL << 62;
    private const ulong PacketFlagKeyFrameV5 = 1UL << 61;
    private const ulong PacketPtsMaskV5 = ~(PacketFlagSessionV5 | PacketFlagConfigV5 | PacketFlagKeyFrameV5);

    private const uint SessionMetaFlagMask = 0x8000_0000u;

    private const byte DeviceMessageTypeClipboard = 0;
    private const byte DeviceMessageTypeAckClipboard = 1;
    private const byte DeviceMessageTypeUhidOutput = 2;
    private const int MaxDeviceClipboardBytes = 1024 * 1024;
    private const int MaxAudioPacketBytes = 4 * 1024 * 1024;

    public const uint AudioCodecDisabled = 0u;
    public const uint AudioCodecError = 1u;
    public const uint AudioCodecRaw = 0x0072_6177u; // "\0raw"
    public const uint AudioCodecAac = 0x0061_6163u; // "\0aac"
    public const uint AudioCodecOpus = 0x6F70_7573u; // "opus"

    public const uint VideoCodecH264 = 0x6832_3634u; // "h264"
    public const uint VideoCodecH265 = 0x6832_3635u; // "h265"

    private readonly ILogger<ScrcpyProtocolClient> _logger;
    private readonly byte[] _videoHeaderBuffer = new byte[12];
    private readonly byte[] _audioHeaderBuffer = new byte[12];
    private byte[] _audioPayloadBuffer = [];
    private readonly Channel<byte[]> _controlChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private TcpClient? _videoClient;
    private TcpClient? _audioClient;
    private TcpClient? _controlClient;
    private NetworkStream? _videoStream;
    private NetworkStream? _audioStream;
    private NetworkStream? _controlStream;
    private CancellationTokenSource? _cts;
    private Task? _controlWriterTask;
    private Task? _controlReaderTask;
    private bool _isV5Protocol;

    public ScrcpyProtocolClient(ILogger<ScrcpyProtocolClient> logger)
    {
        _logger = logger;
    }

    public ScrcpyDeviceMetadata? Metadata { get; private set; }

    public uint AudioCodecId { get; private set; }

    public bool IsAudioConnected => _audioClient?.Connected == true && _audioStream is not null;

    public bool IsConnected => _videoClient?.Connected == true && _controlClient?.Connected == true;

    public event EventHandler<string>? DeviceClipboardReceived;

    public event EventHandler<ulong>? ClipboardAckReceived;

    public async Task<ScrcpyDeviceMetadata> ConnectAsync(
        int localPort,
        CancellationToken cancellationToken,
        Func<string?>? getServerExitError = null,
        bool enableAudio = false)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        (_videoClient, _videoStream) = await ConnectAndReadDummyByteAsync(localPort, getServerExitError, _cts.Token)
            .ConfigureAwait(false);

        if (enableAudio)
        {
            _audioClient = new TcpClient { NoDelay = true };
            await _audioClient.ConnectAsync("127.0.0.1", localPort, _cts.Token).ConfigureAwait(false);
            _audioStream = _audioClient.GetStream();
        }

        _controlClient = new TcpClient { NoDelay = true };
        await _controlClient.ConnectAsync("127.0.0.1", localPort, _cts.Token).ConfigureAwait(false);
        _controlStream = _controlClient.GetStream();

        var deviceNameBytes = new byte[64];
        await ReadExactAsync(_videoStream, deviceNameBytes, getServerExitError, _cts.Token).ConfigureAwait(false);
        var nullIndex = Array.IndexOf(deviceNameBytes, (byte)0);
        var nameLength = nullIndex >= 0 ? nullIndex : deviceNameBytes.Length;
        var deviceName = Encoding.UTF8.GetString(deviceNameBytes, 0, nameLength);

        var codecMeta = new byte[12];
        await ReadExactAsync(_videoStream, codecMeta, getServerExitError, _cts.Token).ConfigureAwait(false);
        var codecId = BinaryPrimitives.ReadUInt32BigEndian(codecMeta.AsSpan(0, 4));
        var word1 = BinaryPrimitives.ReadUInt32BigEndian(codecMeta.AsSpan(4, 4));
        var word2 = BinaryPrimitives.ReadInt32BigEndian(codecMeta.AsSpan(8, 4));

        int width;
        int height;
        if ((word1 & SessionMetaFlagMask) != 0)
        {
            // scrcpy v5.0+: writeVideoHeader sends 4-byte codecId, followed by 12-byte writeSessionMeta
            // [flags (0x80000000/0x80000001), width, height].
            _isV5Protocol = true;
            width = word2;
            var heightBytes = new byte[4];
            await ReadExactAsync(_videoStream, heightBytes, getServerExitError, _cts.Token).ConfigureAwait(false);
            height = BinaryPrimitives.ReadInt32BigEndian(heightBytes);
        }
        else
        {
            // scrcpy v2/v3: writeVideoHeader sends 12 bytes [codecId, width, height].
            _isV5Protocol = false;
            width = (int)word1;
            height = word2;
        }

        if (_audioStream is not null)
        {
            var audioCodecBytes = new byte[4];
            var audioOk = await TryReadExactAsync(_audioStream, audioCodecBytes, _cts.Token).ConfigureAwait(false);
            if (!audioOk)
            {
                _logger.LogWarning("音訊串流於讀取編碼標頭前關閉，將以純畫面模式繼續。");
                CloseAudioStream();
            }
            else
            {
                AudioCodecId = BinaryPrimitives.ReadUInt32BigEndian(audioCodecBytes);
                if (AudioCodecId is AudioCodecDisabled or AudioCodecError)
                {
                    _logger.LogWarning(
                        "裝置端停用或無法擷取音訊 (AudioCodecId=0x{CodecId:X8})，將以純畫面模式繼續。",
                        AudioCodecId);
                    CloseAudioStream();
                }
                else
                {
                    _logger.LogInformation("已連線音訊串流 (CodecId=0x{CodecId:X8})", AudioCodecId);
                }
            }
        }

        Metadata = new ScrcpyDeviceMetadata(deviceName, codecId, width, height);
        _controlWriterTask = Task.Run(() => ControlWriteLoopAsync(_cts.Token), _cts.Token);
        _controlReaderTask = Task.Run(() => ControlReadLoopAsync(_cts.Token), _cts.Token);

        _logger.LogInformation(
            "已連線至 scrcpy-server ({Protocol}): {DeviceName} ({Width}x{Height})",
            _isV5Protocol ? "v5" : "v2/v3",
            deviceName,
            width,
            height);
        return Metadata;
    }

    public async Task<ScrcpyVideoFrame?> ReadNextVideoFrameAsync(CancellationToken cancellationToken)
    {
        var pooled = await ReadNextVideoFramePooledAsync(cancellationToken).ConfigureAwait(false);
        if (pooled is null)
        {
            return null;
        }

        var item = pooled.Value;
        try
        {
            var copy = new byte[item.Length];
            Buffer.BlockCopy(item.Buffer, 0, copy, 0, item.Length);
            return new ScrcpyVideoFrame(item.PtsMicroseconds, item.IsConfig, item.IsKeyFrame, copy);
        }
        finally
        {
            item.Return();
        }
    }

    public async Task<PooledVideoFrame?> ReadNextVideoFramePooledAsync(CancellationToken cancellationToken)
    {
        if (_videoStream is null)
        {
            return null;
        }

        var header = _videoHeaderBuffer;
        while (true)
        {
            var ok = await TryReadExactAsync(_videoStream, header, header.Length, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return null;
            }

            var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));

            if (_isV5Protocol && (ptsAndFlags & PacketFlagSessionV5) != 0)
            {
                // scrcpy v5.0+ mid-stream session metadata packet (e.g. on rotation/resize), 12 bytes total, no payload
                var newWidth = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
                var newHeight = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
                if (Metadata is not null && newWidth > 0 && newHeight > 0)
                {
                    Metadata = Metadata with { Width = newWidth, Height = newHeight };
                }

                _logger.LogInformation("收到 scrcpy v5 串流尺寸更新: {Width}x{Height}", newWidth, newHeight);
                continue;
            }

            var packetSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
            if (packetSize <= 0 || packetSize > 16 * 1024 * 1024)
            {
                throw new InvalidDataException($"收到異常的視訊封包大小: {packetSize}");
            }

            var rented = ArrayPool<byte>.Shared.Rent(packetSize);
            ok = await TryReadExactAsync(_videoStream, rented, packetSize, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                ArrayPool<byte>.Shared.Return(rented);
                return null;
            }

            bool isConfig;
            bool isKeyFrame;
            long pts;
            if (_isV5Protocol)
            {
                isConfig = (ptsAndFlags & PacketFlagConfigV5) != 0;
                isKeyFrame = (ptsAndFlags & PacketFlagKeyFrameV5) != 0;
                pts = (long)(ptsAndFlags & PacketPtsMaskV5);
            }
            else
            {
                isConfig = (ptsAndFlags & PacketFlagConfigV2) != 0;
                isKeyFrame = (ptsAndFlags & PacketFlagKeyFrameV2) != 0;
                pts = (long)(ptsAndFlags & PacketPtsMaskV2);
            }

            return new PooledVideoFrame(pts, isConfig, isKeyFrame, rented, packetSize);
        }
    }

    public async Task<ScrcpyAudioPacket?> ReadNextAudioPacketAsync(CancellationToken cancellationToken)
    {
        var view = await ReadNextAudioPacketViewAsync(cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return null;
        }

        var item = view.Value;
        return new ScrcpyAudioPacket(item.PtsMicroseconds, item.IsConfig, item.Data.ToArray());
    }

    public async Task<ScrcpyAudioPacketView?> ReadNextAudioPacketViewAsync(CancellationToken cancellationToken)
    {
        if (_audioStream is null)
        {
            return null;
        }

        var header = _audioHeaderBuffer;
        while (true)
        {
            var ok = await TryReadExactAsync(_audioStream, header, header.Length, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return null;
            }

            var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
            if (_isV5Protocol && (ptsAndFlags & PacketFlagSessionV5) != 0)
            {
                // v5.0+ mid-stream session metadata packet has no payload
                continue;
            }

            var packetSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
            if (packetSize <= 0 || packetSize > MaxAudioPacketBytes)
            {
                throw new InvalidDataException($"收到異常的音訊封包大小: {packetSize}");
            }

            if (_audioPayloadBuffer.Length < packetSize)
            {
                _audioPayloadBuffer = new byte[packetSize];
            }

            ok = await TryReadExactAsync(_audioStream, _audioPayloadBuffer, packetSize, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return null;
            }

            bool isConfig;
            long pts;
            if (_isV5Protocol)
            {
                isConfig = (ptsAndFlags & PacketFlagConfigV5) != 0;
                pts = (long)(ptsAndFlags & PacketPtsMaskV5);
            }
            else
            {
                isConfig = (ptsAndFlags & PacketFlagConfigV2) != 0;
                pts = (long)(ptsAndFlags & PacketPtsMaskV2);
            }

            return new ScrcpyAudioPacketView(pts, isConfig, _audioPayloadBuffer.AsMemory(0, packetSize));
        }
    }

    private void CloseAudioStream()
    {
        _audioStream?.Dispose();
        _audioStream = null;
        _audioClient?.Dispose();
        _audioClient = null;
    }

    public void EnqueueControlMessage(byte[] message)
    {
        _controlChannel.Writer.TryWrite(message);
    }

    private async Task ControlWriteLoopAsync(CancellationToken cancellationToken)
    {
        if (_controlStream is null)
        {
            return;
        }

        var coalesceBuffer = new byte[4096];
        try
        {
            while (await _controlChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var offset = 0;
                while (_controlChannel.Reader.TryRead(out var message))
                {
                    if (message.Length > coalesceBuffer.Length)
                    {
                        if (offset > 0)
                        {
                            await _controlStream.WriteAsync(coalesceBuffer.AsMemory(0, offset), cancellationToken)
                                .ConfigureAwait(false);
                            offset = 0;
                        }

                        await _controlStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (offset + message.Length > coalesceBuffer.Length)
                    {
                        await _controlStream.WriteAsync(coalesceBuffer.AsMemory(0, offset), cancellationToken)
                            .ConfigureAwait(false);
                        offset = 0;
                    }

                    Buffer.BlockCopy(message, 0, coalesceBuffer, offset, message.Length);
                    offset += message.Length;
                }

                if (offset > 0)
                {
                    await _controlStream.WriteAsync(coalesceBuffer.AsMemory(0, offset), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "傳送控制封包時連線中斷");
        }
    }

    private async Task ControlReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_controlStream is null)
        {
            return;
        }

        var typeBuf = new byte[1];
        var lenBuf = new byte[4];
        var seqBuf = new byte[8];
        var uhidHeaderBuf = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await TryReadExactAsync(_controlStream, typeBuf, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                switch (typeBuf[0])
                {
                    case DeviceMessageTypeClipboard:
                    {
                        if (!await TryReadExactAsync(_controlStream, lenBuf, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        var length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                        if (length < 0 || length > MaxDeviceClipboardBytes)
                        {
                            _logger.LogWarning("收到異常的裝置剪貼簿長度: {Length}", length);
                            return;
                        }

                        var payload = length == 0 ? [] : new byte[length];
                        if (length > 0 && !await TryReadExactAsync(_controlStream, payload, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        var text = Encoding.UTF8.GetString(payload);
                        DeviceClipboardReceived?.Invoke(this, text);
                        break;
                    }
                    case DeviceMessageTypeAckClipboard:
                    {
                        if (!await TryReadExactAsync(_controlStream, seqBuf, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        var sequence = BinaryPrimitives.ReadUInt64BigEndian(seqBuf);
                        ClipboardAckReceived?.Invoke(this, sequence);
                        break;
                    }
                    case DeviceMessageTypeUhidOutput:
                    {
                        if (!await TryReadExactAsync(_controlStream, uhidHeaderBuf, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        var size = BinaryPrimitives.ReadUInt16BigEndian(uhidHeaderBuf.AsSpan(2, 2));
                        if (size > 0)
                        {
                            var discard = new byte[size];
                            if (!await TryReadExactAsync(_controlStream, discard, cancellationToken).ConfigureAwait(false))
                            {
                                return;
                            }
                        }

                        break;
                    }
                    default:
                        _logger.LogWarning("收到未知的裝置控制訊息類型: {MessageType}", typeBuf[0]);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "讀取裝置控制訊息時連線結束");
        }
    }

    private static async Task<(TcpClient Client, NetworkStream Stream)> ConnectAndReadDummyByteAsync(
        int localPort,
        Func<string?>? getServerExitError,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var dummy = new byte[1];

        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var serverError = getServerExitError?.Invoke();
            if (!string.IsNullOrEmpty(serverError))
            {
                throw new IOException($"scrcpy-server 已異常結束：{serverError}");
            }

            var client = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 512 * 1024
            };
            try
            {
                await client.ConnectAsync("127.0.0.1", localPort, cancellationToken).ConfigureAwait(false);
                var stream = client.GetStream();

                // In adb forward mode, adb accepts the TCP socket immediately even before scrcpy-server
                // starts listening on the device, then closes it (EOF). Retry until 1 dummy byte arrives.
                var ok = await TryReadExactAsync(stream, dummy, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    return (client, stream);
                }

                client.Dispose();
                lastError = new EndOfStreamException("scrcpy 串流尚未就緒。");
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                client.Dispose();
                lastError = ex;
            }

            serverError = getServerExitError?.Invoke();
            if (!string.IsNullOrEmpty(serverError))
            {
                throw new IOException($"scrcpy-server 已異常結束：{serverError}");
            }

            await Task.Delay(attempt < 10 ? 25 : 100, cancellationToken).ConfigureAwait(false);
        }

        var finalServerError = getServerExitError?.Invoke();
        if (!string.IsNullOrEmpty(finalServerError))
        {
            throw new IOException($"scrcpy-server 已異常結束：{finalServerError}");
        }

        throw new IOException($"無法連線至 scrcpy-server（本機轉發連接埠 {localPort}）。", lastError);
    }

    private static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        Func<string?>? getServerExitError,
        CancellationToken cancellationToken)
    {
        var ok = await TryReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            var serverError = getServerExitError?.Invoke();
            var message = string.IsNullOrEmpty(serverError)
                ? "scrcpy 串流已關閉。"
                : $"scrcpy 串流已關閉：{serverError}";
            throw new EndOfStreamException(message);
        }
    }

    private static Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        return TryReadExactAsync(stream, buffer, buffer.Length, cancellationToken);
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _controlChannel.Writer.TryComplete();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_controlWriterTask is not null)
        {
            try
            {
                await _controlWriterTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "結束控制寫入執行緒時忽略例外");
            }
        }

        if (_controlReaderTask is not null)
        {
            try
            {
                await _controlReaderTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "結束控制讀取執行緒時忽略例外");
            }
        }

        _videoStream?.Dispose();
        CloseAudioStream();
        _controlStream?.Dispose();
        _videoClient?.Dispose();
        _controlClient?.Dispose();
        _cts?.Dispose();
    }
}
