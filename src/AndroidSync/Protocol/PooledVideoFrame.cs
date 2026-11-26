using System.Buffers;

namespace Com.AndroidSync.App.Protocol;

public readonly record struct PooledVideoFrame(
    long PtsMicroseconds,
    bool IsConfig,
    bool IsKeyFrame,
    byte[] Buffer,
    int Length)
{
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public void Return()
    {
        if (Buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(Buffer);
        }
    }
}
