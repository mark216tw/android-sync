using Com.AndroidSync.App.Rendering;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class Nv12ColorConverterTests
{
    [Fact]
    public void ConvertNv12ToBgra_NeutralWhite_ProducesWhitePixels()
    {
        const int width = 2;
        const int height = 2;
        var nv12 = new byte[]
        {
            235, 235,
            235, 235,
            128, 128
        };
        var bgra = new byte[width * height * 4];

        Nv12ColorConverter.ConvertNv12ToBgra(nv12, width, height, width, bgra);

        for (var i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(255, bgra[i]);
            Assert.Equal(255, bgra[i + 1]);
            Assert.Equal(255, bgra[i + 2]);
            Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Fact]
    public void ConvertNv12ToBgra_NeutralBlack_ProducesBlackPixelsWithFullAlpha()
    {
        const int width = 2;
        const int height = 2;
        var nv12 = new byte[]
        {
            16, 16,
            16, 16,
            128, 128
        };
        var bgra = new byte[width * height * 4];

        Nv12ColorConverter.ConvertNv12ToBgra(nv12, width, height, width, bgra);

        for (var i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(0, bgra[i]);
            Assert.Equal(0, bgra[i + 1]);
            Assert.Equal(0, bgra[i + 2]);
            Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Fact]
    public void ConvertNv12ToBgra_WithRightAndBottomZeroPadding_CropsWithoutGreenBorder()
    {
        const int codedWidth = 4;
        const int codedHeight = 4;
        const int visibleWidth = 2;
        const int visibleHeight = 2;

        // NV12 緩衝區：4x4 編碼尺寸（Y 16 bytes + UV 8 bytes），
        // 僅左上 2x2 為白色 (Y=235, UV=128)，右側與下方補齊區皆為 0（未裁切會呈綠色）。
        var nv12 = new byte[codedWidth * codedHeight + codedWidth * (codedHeight / 2)];
        nv12[0] = 235;
        nv12[1] = 235;
        nv12[4] = 235;
        nv12[5] = 235;

        var uvOffset = codedWidth * codedHeight;
        nv12[uvOffset + 0] = 128;
        nv12[uvOffset + 1] = 128;

        var bgra = new byte[visibleWidth * visibleHeight * 4];
        Nv12ColorConverter.ConvertNv12ToBgra(
            nv12,
            visibleWidth,
            visibleHeight,
            codedWidth,
            codedHeight,
            cropX: 0,
            cropY: 0,
            bgra);

        for (var i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(255, bgra[i]);
            Assert.Equal(255, bgra[i + 1]);
            Assert.Equal(255, bgra[i + 2]);
            Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Fact]
    public void ConvertNv12ToBgra_WithNonZeroCropOffset_SamplesOffsetRegion()
    {
        const int codedWidth = 4;
        const int codedHeight = 4;
        const int visibleWidth = 2;
        const int visibleHeight = 2;

        var nv12 = new byte[codedWidth * codedHeight + codedWidth * (codedHeight / 2)];
        nv12[(2 * codedWidth) + 2] = 235;
        nv12[(2 * codedWidth) + 3] = 235;
        nv12[(3 * codedWidth) + 2] = 235;
        nv12[(3 * codedWidth) + 3] = 235;

        var uvOffset = codedWidth * codedHeight;
        nv12[uvOffset + codedWidth + 2] = 128;
        nv12[uvOffset + codedWidth + 3] = 128;

        var bgra = new byte[visibleWidth * visibleHeight * 4];
        Nv12ColorConverter.ConvertNv12ToBgra(
            nv12,
            visibleWidth,
            visibleHeight,
            codedWidth,
            codedHeight,
            cropX: 2,
            cropY: 2,
            bgra);

        for (var i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(255, bgra[i]);
            Assert.Equal(255, bgra[i + 1]);
            Assert.Equal(255, bgra[i + 2]);
            Assert.Equal(255, bgra[i + 3]);
        }
    }
}
