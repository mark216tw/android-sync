namespace Com.AndroidSync.App.Helpers;

public static class CoordinateMapper
{
    public static ViewportRect ComputeViewportRect(
        double containerWidth,
        double containerHeight,
        int deviceWidth,
        int deviceHeight)
    {
        if (containerWidth <= 0 || containerHeight <= 0 || deviceWidth <= 0 || deviceHeight <= 0)
        {
            return new ViewportRect(0, 0, Math.Max(containerWidth, 0), Math.Max(containerHeight, 0));
        }

        var containerRatio = containerWidth / containerHeight;
        var deviceRatio = (double)deviceWidth / deviceHeight;

        if (deviceRatio > containerRatio)
        {
            var width = containerWidth;
            var height = containerWidth / deviceRatio;
            var y = (containerHeight - height) / 2.0;
            return new ViewportRect(0, y, width, height);
        }
        else
        {
            var height = containerHeight;
            var width = containerHeight * deviceRatio;
            var x = (containerWidth - width) / 2.0;
            return new ViewportRect(x, 0, width, height);
        }
    }

    public static bool TryMapToDevice(
        double pointerX,
        double pointerY,
        ViewportRect viewport,
        int deviceWidth,
        int deviceHeight,
        out int deviceX,
        out int deviceY)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || deviceWidth <= 0 || deviceHeight <= 0)
        {
            deviceX = 0;
            deviceY = 0;
            return false;
        }

        var normalizedX = (pointerX - viewport.X) / viewport.Width;
        var normalizedY = (pointerY - viewport.Y) / viewport.Height;

        var clampedX = Math.Clamp(normalizedX, 0.0, 1.0);
        var clampedY = Math.Clamp(normalizedY, 0.0, 1.0);

        deviceX = (int)Math.Round(clampedX * (deviceWidth - 1));
        deviceY = (int)Math.Round(clampedY * (deviceHeight - 1));
        return true;
    }
}
