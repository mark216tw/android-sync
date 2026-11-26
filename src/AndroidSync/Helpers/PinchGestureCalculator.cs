namespace Com.AndroidSync.App.Helpers;

public static class PinchGestureCalculator
{
    public const ulong PrimaryPinchPointerId = 1001UL;
    public const ulong SecondaryPinchPointerId = 1002UL;

    public static (int X, int Y) ComputeSymmetricPoint(int x, int y, int deviceWidth, int deviceHeight)
    {
        if (deviceWidth <= 0 || deviceHeight <= 0)
        {
            return (0, 0);
        }

        var mirrorX = Math.Clamp(deviceWidth - 1 - x, 0, deviceWidth - 1);
        var mirrorY = Math.Clamp(deviceHeight - 1 - y, 0, deviceHeight - 1);
        return (mirrorX, mirrorY);
    }
}
