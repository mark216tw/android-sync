namespace Com.AndroidSync.App.Models;

public sealed class SavedWirelessDevice
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 5555;

    public string Label { get; set; } = string.Empty;

    public DateTimeOffset LastConnectedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Endpoint => $"{Host}:{Port}";
}
