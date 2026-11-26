using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Com.AndroidSync.App.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class ScrcpyProtocolClientTests
{
    [Fact]
    public async Task ConnectAsync_RetriesWhenForwardPortClosesBeforeDummyByte()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            // Simulate adb forward accepting and immediately closing twice before scrcpy-server is ready
            for (var i = 0; i < 2; i++)
            {
                using var rejected = await listener.AcceptTcpClientAsync();
            }

            using var videoSocket = await listener.AcceptTcpClientAsync();
            await using var videoStream = videoSocket.GetStream();
            await videoStream.WriteAsync(new byte[] { 0x00 });

            using var controlSocket = await listener.AcceptTcpClientAsync();

            var deviceName = new byte[64];
            Encoding.UTF8.GetBytes("Pixel 9").CopyTo(deviceName, 0);
            await videoStream.WriteAsync(deviceName);

            var codecMeta = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(codecMeta.AsSpan(0, 4), 0x68323634); // h264
            BinaryPrimitives.WriteInt32BigEndian(codecMeta.AsSpan(4, 4), 1080);
            BinaryPrimitives.WriteInt32BigEndian(codecMeta.AsSpan(8, 4), 2400);
            await videoStream.WriteAsync(codecMeta);
        });

        await using var client = new ScrcpyProtocolClient(NullLogger<ScrcpyProtocolClient>.Instance);
        var metadata = await client.ConnectAsync(port, CancellationToken.None);

        await serverTask;

        Assert.Equal("Pixel 9", metadata.DeviceName);
        Assert.Equal(0x68323634u, metadata.CodecId);
        Assert.Equal(1080, metadata.Width);
        Assert.Equal(2400, metadata.Height);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWithServerErrorWhenServerExitsEarly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var client = new ScrcpyProtocolClient(NullLogger<ScrcpyProtocolClient>.Instance);
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            client.ConnectAsync(port, CancellationToken.None, () => "Unknown server option: send_codec_meta"));

        Assert.Contains("Unknown server option: send_codec_meta", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_ParsesScrcpyV5StreamHeaderSessionMetaAndFlags()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var videoSocket = await listener.AcceptTcpClientAsync();
            await using var videoStream = videoSocket.GetStream();
            await videoStream.WriteAsync(new byte[] { 0x00 });

            using var controlSocket = await listener.AcceptTcpClientAsync();

            var deviceName = new byte[64];
            Encoding.UTF8.GetBytes("Pixel 10").CopyTo(deviceName, 0);
            await videoStream.WriteAsync(deviceName);

            // v5.0.1 writeVideoHeader: 4-byte codecId
            var codecId = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(codecId, 0x68323634);
            await videoStream.WriteAsync(codecId);

            // v5.0.1 writeSessionMeta: [0x80000000, width, height]
            var sessionMeta = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(sessionMeta.AsSpan(0, 4), 0x8000_0000u);
            BinaryPrimitives.WriteInt32BigEndian(sessionMeta.AsSpan(4, 4), 480);
            BinaryPrimitives.WriteInt32BigEndian(sessionMeta.AsSpan(8, 4), 1080);
            await videoStream.WriteAsync(sessionMeta);

            // v5.0.1 config packet (bit 62)
            var configHeader = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(configHeader.AsSpan(0, 8), 1UL << 62);
            BinaryPrimitives.WriteInt32BigEndian(configHeader.AsSpan(8, 4), 4);
            await videoStream.WriteAsync(configHeader);
            await videoStream.WriteAsync(new byte[] { 0x00, 0x00, 0x00, 0x01 });

            // v5.0.1 mid-stream session meta (rotation to 1080x480)
            BinaryPrimitives.WriteUInt32BigEndian(sessionMeta.AsSpan(0, 4), 0x8000_0001u);
            BinaryPrimitives.WriteInt32BigEndian(sessionMeta.AsSpan(4, 4), 1080);
            BinaryPrimitives.WriteInt32BigEndian(sessionMeta.AsSpan(8, 4), 480);
            await videoStream.WriteAsync(sessionMeta);

            // v5.0.1 keyframe packet (bit 61) with PTS 12345
            var keyHeader = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(keyHeader.AsSpan(0, 8), (1UL << 61) | 12345UL);
            BinaryPrimitives.WriteInt32BigEndian(keyHeader.AsSpan(8, 4), 3);
            await videoStream.WriteAsync(keyHeader);
            await videoStream.WriteAsync(new byte[] { 0xAA, 0xBB, 0xCC });
        });

        await using var client = new ScrcpyProtocolClient(NullLogger<ScrcpyProtocolClient>.Instance);
        var metadata = await client.ConnectAsync(port, CancellationToken.None);

        Assert.Equal("Pixel 10", metadata.DeviceName);
        Assert.Equal(0x68323634u, metadata.CodecId);
        Assert.Equal(480, metadata.Width);
        Assert.Equal(1080, metadata.Height);

        var configFrame = await client.ReadNextVideoFrameAsync(CancellationToken.None);
        Assert.NotNull(configFrame);
        Assert.True(configFrame.Value.IsConfig);
        Assert.False(configFrame.Value.IsKeyFrame);

        var keyFrame = await client.ReadNextVideoFrameAsync(CancellationToken.None);
        Assert.NotNull(keyFrame);
        Assert.False(keyFrame.Value.IsConfig);
        Assert.True(keyFrame.Value.IsKeyFrame);
        Assert.Equal(12345L, keyFrame.Value.PtsMicroseconds);
        Assert.Equal(1080, client.Metadata!.Width);
        Assert.Equal(480, client.Metadata!.Height);

        await serverTask;
    }

    [Fact]
    public async Task ControlReadLoop_RaisesDeviceClipboardAndAckEvents()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clipboardReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackReceived = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var videoSocket = await listener.AcceptTcpClientAsync();
            await using var videoStream = videoSocket.GetStream();
            await videoStream.WriteAsync(new byte[] { 0x00 });

            using var controlSocket = await listener.AcceptTcpClientAsync();
            await using var controlStream = controlSocket.GetStream();

            var deviceName = new byte[64];
            Encoding.UTF8.GetBytes("Pixel 10").CopyTo(deviceName, 0);
            await videoStream.WriteAsync(deviceName);

            var codecMeta = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(codecMeta.AsSpan(0, 4), 0x68323634);
            BinaryPrimitives.WriteInt32BigEndian(codecMeta.AsSpan(4, 4), 1080);
            BinaryPrimitives.WriteInt32BigEndian(codecMeta.AsSpan(8, 4), 2400);
            await videoStream.WriteAsync(codecMeta);

            // Send TYPE_CLIPBOARD (0)
            var clipBytes = Encoding.UTF8.GetBytes("來自手機的文字");
            var clipMsg = new byte[5 + clipBytes.Length];
            clipMsg[0] = 0;
            BinaryPrimitives.WriteInt32BigEndian(clipMsg.AsSpan(1, 4), clipBytes.Length);
            clipBytes.CopyTo(clipMsg.AsSpan(5));
            await controlStream.WriteAsync(clipMsg);

            // Send TYPE_ACK_CLIPBOARD (1)
            var ackMsg = new byte[9];
            ackMsg[0] = 1;
            BinaryPrimitives.WriteUInt64BigEndian(ackMsg.AsSpan(1, 8), 99UL);
            await controlStream.WriteAsync(ackMsg);

            await Task.WhenAll(clipboardReceived.Task, ackReceived.Task);
        });

        await using var client = new ScrcpyProtocolClient(NullLogger<ScrcpyProtocolClient>.Instance);
        client.DeviceClipboardReceived += (_, text) => clipboardReceived.TrySetResult(text);
        client.ClipboardAckReceived += (_, seq) => ackReceived.TrySetResult(seq);

        await client.ConnectAsync(port, CancellationToken.None);

        Assert.Equal("來自手機的文字", await clipboardReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(99UL, await ackReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await serverTask;
    }

    [Fact]
    public async Task ConnectAsync_WithAudioEnabled_ReadsAudioCodecAndRawPcmPackets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var videoSocket = await listener.AcceptTcpClientAsync();
            await using var videoStream = videoSocket.GetStream();
            await videoStream.WriteAsync(new byte[] { 0x00 });

            using var audioSocket = await listener.AcceptTcpClientAsync();
            await using var audioStream = audioSocket.GetStream();

            using var controlSocket = await listener.AcceptTcpClientAsync();

            var deviceName = new byte[64];
            Encoding.UTF8.GetBytes("Pixel 10").CopyTo(deviceName, 0);
            await videoStream.WriteAsync(deviceName);

            // v5.0.1 video header + session meta
            var videoHeader = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(videoHeader.AsSpan(0, 4), 0x68323634);
            BinaryPrimitives.WriteUInt32BigEndian(videoHeader.AsSpan(4, 4), 0x8000_0000u);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(8, 4), 1080);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(12, 4), 2400);
            await videoStream.WriteAsync(videoHeader);

            // audio header: 4-byte raw codec id
            var audioCodec = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(audioCodec, ScrcpyProtocolClient.AudioCodecRaw);
            await audioStream.WriteAsync(audioCodec);

            // audio packet: 12-byte header (PTS=5000, size=8) + 8-byte PCM payload
            var audioPacketHeader = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(audioPacketHeader.AsSpan(0, 8), 5000UL);
            BinaryPrimitives.WriteInt32BigEndian(audioPacketHeader.AsSpan(8, 4), 8);
            await audioStream.WriteAsync(audioPacketHeader);
            await audioStream.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        });

        await using var client = new ScrcpyProtocolClient(NullLogger<ScrcpyProtocolClient>.Instance);
        var metadata = await client.ConnectAsync(port, CancellationToken.None, enableAudio: true);

        Assert.Equal("Pixel 10", metadata.DeviceName);
        Assert.True(client.IsAudioConnected);
        Assert.Equal(ScrcpyProtocolClient.AudioCodecRaw, client.AudioCodecId);

        var packet = await client.ReadNextAudioPacketAsync(CancellationToken.None);
        Assert.NotNull(packet);
        Assert.Equal(5000L, packet.Value.PtsMicroseconds);
        Assert.False(packet.Value.IsConfig);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, packet.Value.Data);

        await serverTask;
    }

    [Fact]
    public async Task ConnectAsync_WithAudioDisabledByDevice_DegradesGracefullyToVideoOnly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var videoSocket = await listener.AcceptTcpClientAsync();
            await using var videoStream = videoSocket.GetStream();
            await videoStream.WriteAsync(new byte[] { 0x00 });

            using var audioSocket = await listener.AcceptTcpClientAsync();
            await using var audioStream = audioSocket.GetStream();

            using var controlSocket = await listener.AcceptTcpClientAsync();

            var deviceName = new byte[64];
            Encoding.UTF8.GetBytes("Old Android").CopyTo(deviceName, 0);
            await videoStream.WriteAsync(deviceName);

            var codecMeta = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(codecMeta.AsSpan(0, 4), 0x68323634);
            BinaryPrimitives.WriteInt32BigEndian(codecMeta.AsSpan(4, 4), 720);
            BinaryPrimitives.WriteInt32BigEndian(codecMeta.AsSpan(8, 4), 1280);
            await videoStream.WriteAsync(codecMeta);

            // Device signals audio disabled (0x00000000)
            await audioStream.WriteAsync(new byte[] { 0, 0, 0, 0 });
        });

        await using var client = new ScrcpyProtocolClient(NullLogger<ScrcpyProtocolClient>.Instance);
        var metadata = await client.ConnectAsync(port, CancellationToken.None, enableAudio: true);

        Assert.Equal("Old Android", metadata.DeviceName);
        Assert.True(client.IsConnected);
        Assert.False(client.IsAudioConnected);
        Assert.Equal(ScrcpyProtocolClient.AudioCodecDisabled, client.AudioCodecId);
        Assert.Null(await client.ReadNextAudioPacketAsync(CancellationToken.None));

        await serverTask;
    }
}
