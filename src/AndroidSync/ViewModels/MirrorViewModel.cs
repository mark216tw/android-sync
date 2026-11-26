using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Protocol;
using Com.AndroidSync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Com.AndroidSync.App.ViewModels;

public sealed partial class MirrorViewModel : ObservableObject
{
    private readonly IMirrorSessionService _sessionService;
    private readonly ISettingsService _settingsService;

    public MirrorViewModel(IMirrorSessionService sessionService, ISettingsService settingsService)
    {
        _sessionService = sessionService;
        _settingsService = settingsService;
        ShowPerformanceHud = settingsService.Current.MirrorOptions.ShowPerformanceHud;
    }

    [ObservableProperty]
    public partial MirrorSessionState SessionState { get; set; } = MirrorSessionState.Disconnected;

    [ObservableProperty]
    public partial string ActiveDeviceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResolutionLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusBannerMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStatusBannerError { get; set; }

    [ObservableProperty]
    public partial bool IsScreenOff { get; set; }

    [ObservableProperty]
    public partial bool IsAudioMuted { get; set; }

    [ObservableProperty]
    public partial bool ShowPerformanceHud { get; set; }

    [ObservableProperty]
    public partial string HudSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool IsCompactMode { get; set; }

    public string AudioMuteGlyph => IsAudioMuted ? "\uE74F" : "\uE767";

    public string PinGlyph => IsAlwaysOnTop ? "\uE77A" : "\uE718";

    public string CompactGlyph => IsCompactMode ? "\uE73F" : "\uE740";

    public bool IsDisconnected => SessionState == MirrorSessionState.Disconnected;

    public bool IsConnecting => SessionState == MirrorSessionState.Connecting;

    public bool IsReconnecting => SessionState == MirrorSessionState.Reconnecting;

    public bool IsConnected => SessionState == MirrorSessionState.Connected;

    public bool HasError => SessionState == MirrorSessionState.Error && !string.IsNullOrEmpty(ErrorMessage);

    public bool HasStatusBanner => !string.IsNullOrEmpty(StatusBannerMessage);

    public bool IsHudVisible => ShowPerformanceHud && IsConnected && !string.IsNullOrEmpty(HudSummary);

    partial void OnIsAudioMutedChanged(bool value) => OnPropertyChanged(nameof(AudioMuteGlyph));

    partial void OnIsAlwaysOnTopChanged(bool value) => OnPropertyChanged(nameof(PinGlyph));

    partial void OnIsCompactModeChanged(bool value) => OnPropertyChanged(nameof(CompactGlyph));

    partial void OnShowPerformanceHudChanged(bool value) => OnPropertyChanged(nameof(IsHudVisible));

    partial void OnHudSummaryChanged(string value) => OnPropertyChanged(nameof(IsHudVisible));

    partial void OnStatusBannerMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusBanner));

    partial void OnSessionStateChanged(MirrorSessionState value)
    {
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsHudVisible));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    public async Task StartSessionAsync(AndroidDevice device)
    {
        ErrorMessage = string.Empty;
        StatusBannerMessage = string.Empty;
        HudSummary = string.Empty;
        ShowPerformanceHud = _settingsService.Current.MirrorOptions.ShowPerformanceHud;
        ActiveDeviceName = device.DisplayName;
        await _sessionService.StartAsync(device);
    }

    public void UpdateStateFromSession(MirrorSessionState state)
    {
        SessionState = state;
        IsScreenOff = _sessionService.IsScreenOff;
        IsAudioMuted = _sessionService.IsAudioMuted;
        if (state == MirrorSessionState.Connected)
        {
            ErrorMessage = string.Empty;
        }
    }

    public void UpdateResolution(int width, int height)
    {
        ResolutionLabel = width > 0 && height > 0 ? $"{width} × {height}" : string.Empty;
    }

    public void UpdatePerformanceStats(MirrorPerformanceStats stats)
    {
        var accel = stats.IsGpuZeroCopy ? "DXVA GPU" : "CPU";
        HudSummary = $"{stats.Fps:0.0} FPS | {stats.AverageDecodeMs:0.0} ms | {stats.BitrateMbps:0.0} Mbps | {stats.CodecName} ({accel}) | 音訊: {stats.AudioMode}";
    }

    public void ShowStatusBanner(string message, bool isError = false)
    {
        IsStatusBannerError = isError;
        StatusBannerMessage = message;
    }

    public void ClearStatusBanner()
    {
        StatusBannerMessage = string.Empty;
    }

    public void SetError(string message)
    {
        ErrorMessage = message;
        SessionState = MirrorSessionState.Error;
    }

    [RelayCommand]
    private void SendBack() => _sessionService.SendKeyTap(AndroidKeyCode.Back);

    [RelayCommand]
    private void SendHome() => _sessionService.SendKeyTap(AndroidKeyCode.Home);

    [RelayCommand]
    private void SendRecents() => _sessionService.SendKeyTap(AndroidKeyCode.AppSwitch);

    [RelayCommand]
    private void SendVolumeUp() => _sessionService.SendKeyTap(AndroidKeyCode.VolumeUp);

    [RelayCommand]
    private void SendVolumeDown() => _sessionService.SendKeyTap(AndroidKeyCode.VolumeDown);

    [RelayCommand]
    private void SendPower() => _sessionService.SendKeyTap(AndroidKeyCode.Power);

    [RelayCommand]
    private void ToggleScreenPower()
    {
        _sessionService.ToggleScreenPower();
        IsScreenOff = _sessionService.IsScreenOff;
    }

    [RelayCommand]
    private void ToggleAudioMute()
    {
        _sessionService.ToggleAudioMute();
        IsAudioMuted = _sessionService.IsAudioMuted;
    }

    [RelayCommand]
    private void RotateDevice() => _sessionService.RequestRotate();

    [RelayCommand]
    private void ToggleHud()
    {
        ShowPerformanceHud = !ShowPerformanceHud;
        _settingsService.Current.MirrorOptions.ShowPerformanceHud = ShowPerformanceHud;
        _ = _settingsService.SaveAsync();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _sessionService.StopAsync();
        ActiveDeviceName = string.Empty;
        ResolutionLabel = string.Empty;
        HudSummary = string.Empty;
        StatusBannerMessage = string.Empty;
    }
}
