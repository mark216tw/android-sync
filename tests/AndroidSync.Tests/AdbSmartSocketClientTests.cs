using System.Net;
using System.Net.Sockets;
using System.Text;
using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Com.AndroidSync.Tests;

public sealed class AdbSmartSocketClientTests
{
    [Fact]
    public void FormatRequest_EncodesFourCharHexLengthPrefix()
    {
        var bytes = AdbSmartSocketClient.FormatRequest("host:devices-l");
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Equal("000Ehost:devices-l", text);
    }

    [Theory]
    [InlineData("0000", true, 0)]
    [InlineData("001A", true, 26)]
    [InlineData("00ff", true, 255)]
    [InlineData("ZZZZ", false, 0)]
    public void TryParseHexLength_ParsesValidAndInvalidHeaders(string header, bool expectedOk, int expectedLen)
    {
        var ok = AdbSmartSocketClient.TryParseHexLength(Encoding.ASCII.GetBytes(header), out var len);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedLen, len);
    }

    [Fact]
    public async Task TryQueryDevicesAsync_ParsesDevicesFromSmartSocketServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var req = new byte[18];
            var read = 0;
            while (read < req.Length)
            {
                read += await stream.ReadAsync(req.AsMemory(read, req.Length - read));
            }

            Assert.Equal("000Ehost:devices-l", Encoding.ASCII.GetString(req));

            var body = "emulator-5554          device product:sdk_gphone model:Pixel_8\n";
            var payload = Encoding.UTF8.GetBytes(body);
            var response = Encoding.ASCII.GetBytes($"OKAY{payload.Length:X4}");
            await stream.WriteAsync(response);
            await stream.WriteAsync(payload);
        });

        var client = new AdbSmartSocketClient(NullLogger<AdbSmartSocketClient>.Instance);
        var devices = await client.TryQueryDevicesAsync(CancellationToken.None, "127.0.0.1", port);
        await serverTask;

        Assert.NotNull(devices);
        var device = Assert.Single(devices);
        Assert.Equal("emulator-5554", device.Serial);
        Assert.Equal("Pixel 8", device.Model);
        Assert.Equal(DeviceStatus.Online, device.Status);
    }
}
