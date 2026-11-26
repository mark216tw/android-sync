namespace Com.AndroidSync.App.Models;

public sealed class WindowPlacementState
{
    public int X { get; set; } = 100;

    public int Y { get; set; } = 100;

    public int Width { get; set; } = 1120;

    public int Height { get; set; } = 760;

    public bool IsMaximized { get; set; }
}
