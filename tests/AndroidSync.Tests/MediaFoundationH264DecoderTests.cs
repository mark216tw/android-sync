using Com.AndroidSync.App.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class MediaFoundationH264DecoderTests
{
    [Theory]
    [InlineData(1080, 1920)]
    [InlineData(1080, 2400)]
    [InlineData(480, 1080)]
    [InlineData(486, 1080)]
    [InlineData(491, 1080)]
    [InlineData(0, 0)]
    public void Initialize_SucceedsForValidAndFallbackDimensions(int width, int height)
    {
        using var decoder = new MediaFoundationH264Decoder(NullLogger<MediaFoundationH264Decoder>.Instance);
        decoder.Initialize(width, height);
    }

    [Fact]
    public void Initialize_WithDxgiDeviceManager_SucceedsAndReleasesCleanly()
    {
        using var renderer = new D3D11SwapChainRenderer(NullLogger<D3D11SwapChainRenderer>.Instance);
        var manager = renderer.GetDxgiDeviceManager();

        using var decoder = new MediaFoundationH264Decoder(NullLogger<MediaFoundationH264Decoder>.Instance);
        decoder.Initialize(1080, 1920, manager);
        decoder.ReleaseOutputFrame();
    }

    [Fact]
    public void TryValidateAperture_AcceptsValidCroppedRegion()
    {
        var valid = MediaFoundationH264Decoder.TryValidateAperture(
            offsetX: 0,
            offsetY: 0,
            areaWidth: 1080,
            areaHeight: 2340,
            codedWidth: 1088,
            codedHeight: 2352,
            out var result);

        Assert.True(valid);
        Assert.Equal((0, 0, 1080, 2340), result);
    }

    [Theory]
    [InlineData(-1, 0, 1080, 2340, 1088, 2352)]
    [InlineData(0, -1, 1080, 2340, 1088, 2352)]
    [InlineData(0, 0, 0, 2340, 1088, 2352)]
    [InlineData(0, 0, 1080, 0, 1088, 2352)]
    [InlineData(16, 0, 1080, 2340, 1088, 2352)]
    [InlineData(0, 16, 1080, 2340, 1088, 2352)]
    public void TryValidateAperture_RejectsInvalidBounds(
        int offsetX,
        int offsetY,
        int areaWidth,
        int areaHeight,
        int codedWidth,
        int codedHeight)
    {
        var valid = MediaFoundationH264Decoder.TryValidateAperture(
            offsetX,
            offsetY,
            areaWidth,
            areaHeight,
            codedWidth,
            codedHeight,
            out _);

        Assert.False(valid);
    }
}
