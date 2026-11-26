using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Services;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class AdbServiceTests
{
    [Fact]
    public void ParseDevicesOutput_ParsesUsbAndWirelessDevices()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            R5CR123456A            device product:o1sxxx model:SM_G9910 device:o1q transport_id:1
            192.168.1.55:5555      device product:husky model:Pixel_8_Pro device:husky transport_id:2
            FA79A123456            unauthorized transport_id:3
            192.168.1.88:5555      offline transport_id:4
            """;

        var devices = AdbService.ParseDevicesOutput(output);

        Assert.Equal(4, devices.Count);

        Assert.Equal("R5CR123456A", devices[0].Serial);
        Assert.Equal("SM G9910", devices[0].Model);
        Assert.Equal(DeviceStatus.Online, devices[0].Status);
        Assert.Equal(DeviceConnectionType.Usb, devices[0].ConnectionType);
        Assert.True(devices[0].IsReady);

        Assert.Equal("192.168.1.55:5555", devices[1].Serial);
        Assert.Equal("Pixel 8 Pro", devices[1].Model);
        Assert.Equal(DeviceStatus.Online, devices[1].Status);
        Assert.Equal(DeviceConnectionType.Wireless, devices[1].ConnectionType);

        Assert.Equal(DeviceStatus.Unauthorized, devices[2].Status);
        Assert.False(devices[2].IsReady);

        Assert.Equal(DeviceStatus.Offline, devices[3].Status);
        Assert.Equal(DeviceConnectionType.Wireless, devices[3].ConnectionType);
    }
}
