using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Com.AndroidSync.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IAdbService _adbService;
    private readonly IThemeService _themeService;
    private bool _isLoading;

    public SettingsViewModel(
        ISettingsService settingsService,
        IAdbService adbService,
        IThemeService themeService)
    {
        _settingsService = settingsService;
        _adbService = adbService;
        _themeService = themeService;
        LoadFromCurrent();
    }

    [ObservableProperty]
    public partial int SelectedDisplayModeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedColorThemeIndex { get; set; }

    [ObservableProperty]
    public partial string AdbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScrcpyServerPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScrcpyServerVersion { get; set; } = "5.0.1";

    [ObservableProperty]
    public partial double MaxSize { get; set; } = 1080;

    [ObservableProperty]
    public partial double BitRateMbps { get; set; } = 8;

    [ObservableProperty]
    public partial double MaxFps { get; set; } = 60;

    [ObservableProperty]
    public partial bool StayAwake { get; set; } = true;

    [ObservableProperty]
    public partial bool TurnScreenOffOnStart { get; set; }

    [ObservableProperty]
    public partial bool SyncClipboard { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableAudio { get; set; } = true;

    [ObservableProperty]
    public partial bool UseCompressedAudio { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedVideoCodecIndex { get; set; }

    [ObservableProperty]
    public partial bool AutoReconnect { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowPerformanceHud { get; set; }

    [ObservableProperty]
    public partial string DetectedToolsSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SaveFeedback { get; set; } = string.Empty;

    public void LoadFromCurrent()
    {
        _isLoading = true;
        try
        {
            var current = _settingsService.Current;
            SelectedDisplayModeIndex = (int)current.DisplayMode;
            SelectedColorThemeIndex = (int)current.ColorTheme;
            AdbPath = current.AdbPath;
            ScrcpyServerPath = current.ScrcpyServerPath;
            ScrcpyServerVersion = current.MirrorOptions.ScrcpyServerVersion;
            MaxSize = current.MirrorOptions.MaxSize;
            BitRateMbps = Math.Max(1, current.MirrorOptions.BitRateBps / 1_000_000.0);
            MaxFps = current.MirrorOptions.MaxFps;
            StayAwake = current.MirrorOptions.StayAwake;
            TurnScreenOffOnStart = current.MirrorOptions.TurnScreenOffOnStart;
            SyncClipboard = current.MirrorOptions.SyncClipboard;
            EnableAudio = current.MirrorOptions.EnableAudio;
            UseCompressedAudio = current.MirrorOptions.UseCompressedAudio;
            SelectedVideoCodecIndex = (int)current.MirrorOptions.VideoCodec;
            AutoReconnect = current.MirrorOptions.AutoReconnect;
            ShowPerformanceHud = current.MirrorOptions.ShowPerformanceHud;
            RefreshToolDetection();
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnSelectedDisplayModeIndexChanged(int value)
    {
        if (_isLoading || !Enum.IsDefined(typeof(AppDisplayMode), value))
        {
            return;
        }

        var mode = (AppDisplayMode)value;
        _settingsService.Current.DisplayMode = mode;
        _themeService.ApplyDisplayMode(mode);
        _ = PersistAppearanceAsync();
    }

    partial void OnSelectedColorThemeIndexChanged(int value)
    {
        if (_isLoading || !Enum.IsDefined(typeof(AppColorTheme), value))
        {
            return;
        }

        var theme = (AppColorTheme)value;
        _settingsService.Current.ColorTheme = theme;
        _themeService.ApplyColorTheme(theme);
        _ = PersistAppearanceAsync();
    }

    private async Task PersistAppearanceAsync()
    {
        await _settingsService.SaveAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var current = _settingsService.Current;
        if (Enum.IsDefined(typeof(AppDisplayMode), SelectedDisplayModeIndex))
        {
            current.DisplayMode = (AppDisplayMode)SelectedDisplayModeIndex;
        }

        if (Enum.IsDefined(typeof(AppColorTheme), SelectedColorThemeIndex))
        {
            current.ColorTheme = (AppColorTheme)SelectedColorThemeIndex;
        }

        current.AdbPath = AdbPath.Trim();
        current.ScrcpyServerPath = ScrcpyServerPath.Trim();
        current.MirrorOptions.ScrcpyServerVersion = string.IsNullOrWhiteSpace(ScrcpyServerVersion)
            ? "5.0.1"
            : ScrcpyServerVersion.Trim();
        current.MirrorOptions.MaxSize = Math.Max(0, (int)Math.Round(MaxSize));
        current.MirrorOptions.BitRateBps = Math.Max(1, (int)Math.Round(BitRateMbps)) * 1_000_000;
        current.MirrorOptions.MaxFps = Math.Clamp((int)Math.Round(MaxFps), 15, 120);
        current.MirrorOptions.StayAwake = StayAwake;
        current.MirrorOptions.TurnScreenOffOnStart = TurnScreenOffOnStart;
        current.MirrorOptions.SyncClipboard = SyncClipboard;
        current.MirrorOptions.EnableAudio = EnableAudio;
        current.MirrorOptions.UseCompressedAudio = UseCompressedAudio;
        if (Enum.IsDefined(typeof(VideoCodecPreference), SelectedVideoCodecIndex))
        {
            current.MirrorOptions.VideoCodec = (VideoCodecPreference)SelectedVideoCodecIndex;
        }

        current.MirrorOptions.AutoReconnect = AutoReconnect;
        current.MirrorOptions.ShowPerformanceHud = ShowPerformanceHud;

        _themeService.Apply(current.DisplayMode, current.ColorTheme);
        await _settingsService.SaveAsync();
        RefreshToolDetection();
        SaveFeedback = "設定已儲存。";
    }

    private void RefreshToolDetection()
    {
        var adb = _adbService.ResolveAdbPath() ?? "未偵測到";
        var server = _adbService.ResolveScrcpyServerPath() ?? "未偵測到";
        DetectedToolsSummary = $"ADB：{adb}\nscrcpy-server：{server}";
    }
}
