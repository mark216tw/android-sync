namespace Com.AndroidSync.App.Rendering;

public readonly record struct DecodedVideoFrame(
    int Width,
    int Height,
    int Stride,
    byte[]? BgraData,
    nint NativeTexture = 0,
    uint SubresourceIndex = 0,
    int CodedWidth = 0,
    int CodedHeight = 0,
    int CropX = 0,
    int CropY = 0)
{
    public bool IsGpuTexture => NativeTexture != 0;
}
