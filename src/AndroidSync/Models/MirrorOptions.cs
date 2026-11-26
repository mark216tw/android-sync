namespace Com.AndroidSync.App.Models;

public sealed class MirrorOptions
{
    public int MaxSize { get; set; } = 1080;

    public int BitRateBps { get; set; } = 8_000_000;

    public int MaxFps { get; set; } = 60;

    public bool StayAwake { get; set; } = true;

    public bool TurnScreenOffOnStart { get; set; }

    public bool SyncClipboard { get; set; } = true;

    public bool EnableAudio { get; set; } = true;

    public bool UseCompressedAudio { get; set; } = true;

    public VideoCodecPreference VideoCodec { get; set; } = VideoCodecPreference.H264;

    public bool AutoReconnect { get; set; } = true;

    public bool ShowPerformanceHud { get; set; }

    public string ScrcpyServerVersion { get; set; } = "5.0.1";
}
