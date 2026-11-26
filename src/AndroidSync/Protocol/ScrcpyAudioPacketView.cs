namespace Com.AndroidSync.App.Protocol;

public readonly record struct ScrcpyAudioPacketView(
    long PtsMicroseconds,
    bool IsConfig,
    ReadOnlyMemory<byte> Data);
