using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Protocol;
using Com.AndroidSync.App.Rendering;

namespace Com.AndroidSync.App.Services;

public interface IMirrorSessionService : IAsyncDisposable
{
    MirrorSessionState State { get; }

    AndroidDevice? ActiveDevice { get; }

    int VideoWidth { get; }

    int VideoHeight { get; }

    bool IsScreenOff { get; }

    bool IsAudioActive { get; }

    bool IsAudioMuted { get; }

    bool IsGpuZeroCopyActive { get; }

    event EventHandler<MirrorSessionState>? StateChanged;

    event EventHandler<(int Width, int Height)>? VideoSizeChanged;

    event EventHandler<DecodedVideoFrame>? FrameDecoded;

    event EventHandler<string>? ErrorOccurred;

    event EventHandler<string>? DeviceClipboardChanged;

    event EventHandler<MirrorPerformanceStats>? PerformanceStatsUpdated;

    Task StartAsync(AndroidDevice device, CancellationToken cancellationToken = default);

    Task StopAsync();

    void SendTouch(AndroidMotionAction action, int x, int y, bool isPrimaryPressed, ulong pointerId = unchecked((ulong)-1L));

    void SendScroll(int x, int y, float horizontalScroll, float verticalScroll);

    void SendKeyTap(AndroidKeyCode keyCode);

    void SendKey(AndroidKeyCode keyCode, bool isDown, int repeat = 0, AndroidMetaState metaState = AndroidMetaState.None);

    void SendText(string text);

    void SyncHostClipboardToDevice(string text, bool paste = false);

    void RequestDeviceClipboard(ScrcpyClipboardCopyKey copyKey = ScrcpyClipboardCopyKey.None);

    void ToggleScreenPower();

    void ToggleAudioMute();

    void RequestRotate();

    Task<(bool Success, string Message)> PushFileOrInstallApkAsync(string localFilePath, CancellationToken cancellationToken = default);
}
