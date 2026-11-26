namespace Com.AndroidSync.App.Protocol;

public readonly record struct ScrcpyVideoFrame(
    long PtsMicroseconds,
    bool IsConfig,
    bool IsKeyFrame,
    byte[] Data);
