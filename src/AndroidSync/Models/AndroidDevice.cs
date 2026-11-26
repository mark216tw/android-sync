namespace Com.AndroidSync.App.Models;

public sealed record AndroidDevice(
    string Serial,
    string Model,
    string Product,
    DeviceStatus Status,
    DeviceConnectionType ConnectionType)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} ({Serial})";

    public bool IsReady => Status == DeviceStatus.Online;
}
