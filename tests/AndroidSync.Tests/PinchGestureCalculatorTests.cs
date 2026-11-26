using Com.AndroidSync.App.Helpers;
using Xunit;

namespace Com.AndroidSync.Tests;

public sealed class PinchGestureCalculatorTests
{
    [Fact]
    public void ComputeSymmetricPoint_MirrorsAroundDeviceCenter()
    {
        var (mx, my) = PinchGestureCalculator.ComputeSymmetricPoint(300, 400, 1080, 2400);

        Assert.Equal(779, mx);
        Assert.Equal(1999, my);
    }

    [Fact]
    public void ComputeSymmetricPoint_ClampsOutOfRangeCoordinates()
    {
        var (mx, my) = PinchGestureCalculator.ComputeSymmetricPoint(-50, 5000, 1080, 2400);

        Assert.Equal(1079, mx);
        Assert.Equal(0, my);
    }

    [Fact]
    public void ComputeSymmetricPoint_InvalidDimensions_ReturnsOrigin()
    {
        var (mx, my) = PinchGestureCalculator.ComputeSymmetricPoint(100, 100, 0, 2400);

        Assert.Equal(0, mx);
        Assert.Equal(0, my);
    }
}
