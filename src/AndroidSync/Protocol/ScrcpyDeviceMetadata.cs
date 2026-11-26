namespace Com.AndroidSync.App.Protocol;

public sealed record ScrcpyDeviceMetadata(
    string DeviceName,
    uint CodecId,
    int Width,
    int Height);
