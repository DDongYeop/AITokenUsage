namespace AITokenUsage.Core.Taskbar;

/// <summary>
/// Snapshot of one taskbar's layout. Every rectangle is in screen coordinates (physical px, PerMonitorV2).
/// A null element means "not found / not valid" (for example, secondary taskbars have no TrayNotifyWnd).
/// </summary>
public sealed record TaskbarGeometry(
    PixelRect Taskbar, uint Dpi, bool IsHorizontal, bool IsCentered,
    PixelRect? Start, PixelRect? Widgets, PixelRect? AppButtons, PixelRect? Tray);
