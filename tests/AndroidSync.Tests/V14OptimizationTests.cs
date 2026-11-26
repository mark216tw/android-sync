using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Com.AndroidSync.App.Audio;
using Com.AndroidSync.App.Protocol;
using Com.AndroidSync.App.Rendering;
using Com.AndroidSync.App.Services;
using Com.AndroidSync.App.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Com.AndroidSync.Tests;

public sealed class V14OptimizationTests
{
    [Fact]
    public void BuildHeAacUserData_DefaultsTo48kStereoWhenEmpty()
    {
        var data = MediaFoundationAacDecoder.BuildHeAacUserData([]);

        Assert.Equal(14, data.Length);
        Assert.Equal(0xFE, data[2]);
        Assert.Equal(0x11, data[12]);
        Assert.Equal(0x90, data[13]);
    }

    [Fact]
    public void BuildHeAacUserData_AppendsCustomAudioSpecificConfig()
    {
        var data = MediaFoundationAacDecoder.BuildHeAacUserData([0x12, 0x10]);

        Assert.Equal(14, data.Length);
        Assert.Equal(0x12, data[12]);
        Assert.Equal(0x10, data[13]);
    }

    [Fact]
    public void MediaFoundationAacDecoder_InitializesAndDisposesCleanly()
    {
        using var decoder = new MediaFoundationAacDecoder(NullLogger<MediaFoundationAacDecoder>.Instance);
        decoder.Initialize([0x11, 0x90], sampleRate: 48_000, channels: 2);

        Assert.True(decoder.IsInitialized);
    }

    [Fact]
    public void WasapiAudioPlayer_MuteAndDisposeBeforeInitialize_AreSafe()
    {
        var player = new WasapiAudioPlayer(NullLogger<WasapiAudioPlayer>.Instance);
        Assert.False(player.IsInitialized);

        player.IsMuted = true;
        Assert.True(player.IsMuted);

        player.WritePcm([1, 2, 3, 4]);
        player.Dispose();
        player.Dispose();
    }

    [Fact]
    public void D3D11SwapChainRenderer_IsDeviceLostHResult_DetectsRemovedAndReset()
    {
        Assert.True(D3D11SwapChainRenderer.IsDeviceLostHResult(unchecked((int)0x887A0005)));
        Assert.True(D3D11SwapChainRenderer.IsDeviceLostHResult(unchecked((int)0x887A0007)));
        Assert.False(D3D11SwapChainRenderer.IsDeviceLostHResult(0));
    }

    [Fact]
    public void CreateTouchEventMessage_SupportsCustomMultiTouchPointerId()
    {
        var packet = ScrcpyControlSerializer.CreateTouchEventMessage(
            AndroidMotionAction.Down,
            100,
            200,
            1080,
            2400,
            isPrimaryPressed: true,
            pointerId: 1002UL);

        Assert.Equal(32, packet.Length);
        var pointerId = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(2, 8));
        Assert.Equal(1002UL, pointerId);
    }

    [Fact]
    public void ComputeFittedWindowSize_ScalesChromeByRasterizationScale()
    {
        var (w100, h100) = MainWindow.ComputeFittedWindowSize(
            currentWidth: 800,
            currentHeight: 900,
            videoWidth: 1080,
            videoHeight: 2400,
            isCompactMode: true,
            rasterizationScale: 1.0,
            maxWorkWidth: 1920,
            maxWorkHeight: 1040);

        var (w150, h150) = MainWindow.ComputeFittedWindowSize(
            currentWidth: 1200,
            currentHeight: 1350,
            videoWidth: 1080,
            videoHeight: 2400,
            isCompactMode: true,
            rasterizationScale: 1.5,
            maxWorkWidth: 2880,
            maxWorkHeight: 1560);

        // At 1.5x DPI with 1.5x physical window height, fitted physical width should scale proportionally by 1.5x.
        Assert.InRange(w150, (int)Math.Round(w100 * 1.5) - 2, (int)Math.Round(w100 * 1.5) + 2);
        Assert.Equal(900, h100);
        Assert.Equal(1350, h150);
    }

    [Fact]
    public async Task ReadNextAudioPacketViewAsync_ReusesUnderlyingBufferAcrossPackets()
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

            var videoHeader = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(videoHeader.AsSpan(0, 4), ScrcpyProtocolClient.VideoCodecH264);
            BinaryPrimitives.WriteUInt32BigEndian(videoHeader.AsSpan(4, 4), 0x8000_0000u);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(8, 4), 1080);
            BinaryPrimitives.WriteInt32BigEndian(videoHeader.AsSpan(12, 4), 2400);
            await videoStream.WriteAsync(videoHeader);

            var audioCodec = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(audioCodec, ScrcpyProtocolClient.AudioCodecAac);
            await audioStream.WriteAsync(audioCodec);

            // Send two audio packets: first 8 bytes, second 4 bytes (should fit in reused buffer)
            var header1 = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(header1.AsSpan(0, 8), 1000UL);
            BinaryPrimitives.WriteInt32BigEndian(header1.AsSpan(8, 4), 8);
            await audioStream.WriteAsync(header1);
            await audioStream.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            var header2 = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(header2.AsSpan(0, 8), 2000UL);
            BinaryPrimitives.WriteInt32BigEndian(header2.AsSpan(8, 4), 4);
            await audioStream.WriteAsync(header2);
            await audioStream.WriteAsync(new byte[] { 9, 10, 11, 12 });
        });

        await using var client = new ScrcpyProtocolClient(NullLogger<ScrcpyProtocolClient>.Instance);
        await client.ConnectAsync(port, CancellationToken.None, enableAudio: true);

        var p1 = await client.ReadNextAudioPacketViewAsync(CancellationToken.None);
        Assert.NotNull(p1);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, p1.Value.Data.ToArray());

        var p2 = await client.ReadNextAudioPacketViewAsync(CancellationToken.None);
        Assert.NotNull(p2);
        Assert.Equal(2000L, p2.Value.PtsMicroseconds);
        Assert.Equal(new byte[] { 9, 10, 11, 12 }, p2.Value.Data.ToArray());

        await serverTask;
    }

    [Fact]
    public void PcmRingBuffer_HandlesWrapAroundAndLatencyCap()
    {
        var ring = new PcmRingBuffer(capacityBytes: 32, frameAlign: 4, maxBufferedBytes: 16);

        // Write 12 bytes, read 8 bytes (readPos=8, writePos=12, count=4)
        ring.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        var out1 = new byte[8];
        Assert.Equal(8, ring.Read(out1));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, out1);
        Assert.Equal(4, ring.BufferedBytes);

        // Write 16 bytes -> exceeds maxBufferedBytes (16), should drop oldest 4 bytes (9..12) and wrap around buffer
        ring.Write([13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28]);
        Assert.Equal(16, ring.BufferedBytes);

        var out2 = new byte[16];
        Assert.Equal(16, ring.Read(out2));
        Assert.Equal(
            new byte[] { 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 },
            out2);
        Assert.Equal(0, ring.BufferedBytes);
    }

    [Fact]
    public async Task AdbSmartSocketClient_TryForwardAndKillForward_SucceedsOverTcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receivedCommands = new List<string>();

        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var conn = await listener.AcceptTcpClientAsync();
                await using var stream = conn.GetStream();
                var lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf);
                Assert.True(AdbSmartSocketClient.TryParseHexLength(lenBuf, out var len));
                var cmdBuf = new byte[len];
                await stream.ReadExactlyAsync(cmdBuf);
                receivedCommands.Add(Encoding.ASCII.GetString(cmdBuf));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAYOKAY"));
            }
        });

        var client = new AdbSmartSocketClient(NullLogger<AdbSmartSocketClient>.Instance);
        var fwd = await client.TryForwardPortAsync("ABC123", 27183, "scrcpy_01", CancellationToken.None, "127.0.0.1", port);
        var kill = await client.TryRemoveForwardAsync("ABC123", 27183, CancellationToken.None, "127.0.0.1", port);

        await serverTask;

        Assert.NotNull(fwd);
        Assert.True(fwd.IsSuccess);
        Assert.NotNull(kill);
        Assert.True(kill.IsSuccess);
        Assert.Equal("host-serial:ABC123:forward:tcp:27183;localabstract:scrcpy_01", receivedCommands[0]);
        Assert.Equal("host-serial:ABC123:killforward:tcp:27183", receivedCommands[1]);
    }

    [Fact]
    public void Nv12ColorConverter_LargeParallelFrame_MatchesExpectedRgb()
    {
        const int width = 512;
        const int height = 160;
        var nv12 = new byte[width * height * 3 / 2];
        // Pure neutral white in BT.601 limited range: Y=235, U=128, V=128 -> RGB(255, 255, 255)
        Array.Fill(nv12, (byte)235, 0, width * height);
        Array.Fill(nv12, (byte)128, width * height, width * height / 2);

        var bgra = new byte[width * height * 4];
        Nv12ColorConverter.ConvertNv12ToBgra(nv12, width, height, width, bgra);

        // Verify first, middle, and last pixels
        foreach (var pixelIndex in new[] { 0, (width * height) / 2, (width * height) - 1 })
        {
            var offset = pixelIndex * 4;
            Assert.Equal(255, bgra[offset]);
            Assert.Equal(255, bgra[offset + 1]);
            Assert.Equal(255, bgra[offset + 2]);
            Assert.Equal(255, bgra[offset + 3]);
        }
    }
}
