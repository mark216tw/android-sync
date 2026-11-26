using Com.AndroidSync.App.Helpers;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class CoordinateMapperTests
{
    [Fact]
    public void ComputeViewportRect_PortraitPhoneInLandscapeContainer_CentersHorizontally()
    {
        var rect = CoordinateMapper.ComputeViewportRect(1000, 800, 1080, 2160);

        Assert.Equal(400, rect.Width, 3);
        Assert.Equal(800, rect.Height, 3);
        Assert.Equal(300, rect.X, 3);
        Assert.Equal(0, rect.Y, 3);
    }

    [Fact]
    public void TryMapToDevice_MapsAndClampsCorrectly()
    {
        var viewport = new ViewportRect(0, 0, 500, 1000);

        var okCenter = CoordinateMapper.TryMapToDevice(250, 500, viewport, 1080, 2160, out var cx, out var cy);
        Assert.True(okCenter);
        Assert.InRange(cx, 539, 540);
        Assert.InRange(cy, 1079, 1080);

        var okClamp = CoordinateMapper.TryMapToDevice(-50, 1200, viewport, 1080, 2160, out var minX, out var maxY);
        Assert.True(okClamp);
        Assert.Equal(0, minX);
        Assert.Equal(2159, maxY);
    }
}
