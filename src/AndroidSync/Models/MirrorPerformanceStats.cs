namespace Com.AndroidSync.App.Models;

public readonly record struct MirrorPerformanceStats(
    double Fps,
    double AverageDecodeMs,
    double BitrateMbps,
    string CodecName,
    bool IsGpuZeroCopy,
    string AudioMode);
