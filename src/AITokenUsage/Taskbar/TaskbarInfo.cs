using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Taskbar;

public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>An explorer-owned taskbar window. Rectangles are physical screen pixels.</summary>
public sealed record TaskbarInfo(
    nint Handle,
    bool IsPrimary,
    PixelRect Bounds,
    uint Dpi,
    string? MonitorDeviceName,
    PixelRect MonitorBounds,
    bool IsWin11,
    TaskbarEdge Edge)
{
    public bool IsHorizontal => Edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
}
