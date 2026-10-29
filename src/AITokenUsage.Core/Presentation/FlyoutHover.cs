using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Core.Presentation;

/// <summary>Where the cursor really is relative to the widget and its hover card.</summary>
/// <param name="OverAnchor">The cursor is inside the widget window.</param>
/// <param name="OverCard">The cursor is inside the card window (including its transparent shadow border).</param>
public readonly record struct FlyoutHoverState(bool OverAnchor, bool OverCard)
{
    public bool IsOver => OverAnchor || OverCard;
}

/// <summary>
/// Hover-card close decision from the real cursor position instead of WPF MouseEnter/MouseLeave, which report a leave
/// whenever another window (or the card itself, while it resizes) ends up under a cursor that did not move.
/// </summary>
public static class FlyoutHover
{
    /// <summary>
    /// Hit-tests <paramref name="cursor"/> against the window rectangles, all in physical screen pixels. A missing or
    /// empty rectangle (window not created or hidden) never contains the cursor.
    /// </summary>
    public static FlyoutHoverState Locate(PixelPoint cursor, PixelRect? anchor, PixelRect? card) =>
        new(Hits(anchor, cursor), Hits(card, cursor));

    private static bool Hits(PixelRect? rect, PixelPoint cursor) =>
        rect is PixelRect r && !r.IsEmpty && r.Contains(cursor);
}
