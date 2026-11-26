namespace Com.AndroidSync.App.Rendering;

public static class Nv12ColorConverter
{
    public static void ConvertNv12ToBgra(
        ReadOnlySpan<byte> nv12,
        int width,
        int height,
        int yStride,
        Span<byte> bgraDestination)
    {
        ConvertNv12ToBgra(nv12, width, height, yStride, height, 0, 0, bgraDestination);
    }

    public static void ConvertNv12ToBgra(
        ReadOnlySpan<byte> nv12,
        int width,
        int height,
        int yStride,
        int codedHeight,
        int cropX,
        int cropY,
        Span<byte> bgraDestination)
    {
        if (width <= 0 || height <= 0 || yStride <= 0 || codedHeight <= 0)
        {
            return;
        }

        if (cropX < 0 || cropY < 0 || cropX + width > yStride || cropY + height > codedHeight)
        {
            return;
        }

        var requiredBgraLength = width * height * 4;
        if (bgraDestination.Length < requiredBgraLength)
        {
            throw new ArgumentException("BGRA 緩衝區空間不足。", nameof(bgraDestination));
        }

        var uvPlaneOffset = yStride * codedHeight;
        var requiredUvRows = ((cropY + height - 1) >> 1) + 1;
        if (nv12.Length < uvPlaneOffset + (yStride * requiredUvRows))
        {
            return;
        }

        unsafe
        {
            fixed (byte* srcBase = nv12)
            fixed (byte* dstBase = bgraDestination)
            {
                var srcPtr = (nint)srcBase;
                var dstPtr = (nint)dstBase;

                if (height >= 128 && width * height >= 65_536)
                {
                    Parallel.For(0, height, y =>
                    {
                        ConvertRow(
                            (byte*)srcPtr,
                            (byte*)dstPtr,
                            y,
                            width,
                            yStride,
                            uvPlaneOffset,
                            cropX,
                            cropY);
                    });
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        ConvertRow(
                            srcBase,
                            dstBase,
                            y,
                            width,
                            yStride,
                            uvPlaneOffset,
                            cropX,
                            cropY);
                    }
                }
            }
        }
    }

    private static unsafe void ConvertRow(
        byte* nv12,
        byte* bgraDestination,
        int y,
        int width,
        int yStride,
        int uvPlaneOffset,
        int cropX,
        int cropY)
    {
        var srcY = cropY + y;
        var yRowPtr = nv12 + (srcY * yStride) + cropX;
        var uvRowPtr = nv12 + uvPlaneOffset + ((srcY >> 1) * yStride);
        var dstPtr = bgraDestination + (y * width * 4);

        var x = 0;
        if ((cropX & 1) == 0)
        {
            var evenLimit = width & ~1;
            var uvPtr = uvRowPtr + cropX;
            for (; x < evenLimit; x += 2)
            {
                var d = uvPtr[0] - 128;
                var e = uvPtr[1] - 128;
                uvPtr += 2;

                var rDelta = (409 * e) + 128;
                var gDelta = -(100 * d) - (208 * e) + 128;
                var bDelta = (516 * d) + 128;

                var c0 = 298 * (yRowPtr[x] - 16);
                dstPtr[0] = ClampByte((c0 + bDelta) >> 8);
                dstPtr[1] = ClampByte((c0 + gDelta) >> 8);
                dstPtr[2] = ClampByte((c0 + rDelta) >> 8);
                dstPtr[3] = 255;

                var c1 = 298 * (yRowPtr[x + 1] - 16);
                dstPtr[4] = ClampByte((c1 + bDelta) >> 8);
                dstPtr[5] = ClampByte((c1 + gDelta) >> 8);
                dstPtr[6] = ClampByte((c1 + rDelta) >> 8);
                dstPtr[7] = 255;

                dstPtr += 8;
            }
        }

        for (; x < width; x++)
        {
            var srcX = cropX + x;
            var c = 298 * (yRowPtr[x] - 16);
            var uvIndex = srcX & ~1;
            var d = uvRowPtr[uvIndex] - 128;
            var e = uvRowPtr[uvIndex + 1] - 128;

            dstPtr[0] = ClampByte((c + (516 * d) + 128) >> 8);
            dstPtr[1] = ClampByte((c - (100 * d) - (208 * e) + 128) >> 8);
            dstPtr[2] = ClampByte((c + (409 * e) + 128) >> 8);
            dstPtr[3] = 255;
            dstPtr += 4;
        }
    }

    private static byte ClampByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 255)
        {
            return 255;
        }

        return (byte)value;
    }
}
