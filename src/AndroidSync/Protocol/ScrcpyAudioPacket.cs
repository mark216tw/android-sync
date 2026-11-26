namespace Com.AndroidSync.App.Protocol;

public readonly record struct ScrcpyAudioPacket(
    long PtsMicroseconds,
    bool IsConfig,
    byte[] Data);
