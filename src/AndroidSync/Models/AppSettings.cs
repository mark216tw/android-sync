namespace Com.AndroidSync.App.Models;

public sealed class AppSettings
{
    public string AdbPath { get; set; } = string.Empty;

    public string ScrcpyServerPath { get; set; } = string.Empty;

    public MirrorOptions MirrorOptions { get; set; } = new();

    public List<SavedWirelessDevice> SavedWirelessDevices { get; set; } = [];

    public WindowPlacementState? WindowState { get; set; }

    public AppDisplayMode DisplayMode { get; set; } = AppDisplayMode.System;

    public AppColorTheme ColorTheme { get; set; } = AppColorTheme.ClassicBlue;
}
