using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Core.Presentation;

/// <summary>Where the hover card goes, in physical screen pixels.</summary>
public static class FlyoutPlacement
{
    /// <summary>
    /// Top-left of a window of <paramref name="width"/>×<paramref name="height"/> whose visible card is inset by
    /// <paramref name="margin"/> (transparent shadow border). The visible card is centred over <paramref name="anchor"/>
    /// and ends <paramref name="gap"/> above it, then clamped so the visible card stays inside <paramref name="workArea"/>.
    /// </summary>
    public static (int X, int Y) Compute(PixelRect anchor, PixelRect workArea, int width, int height, int margin, int gap)
    {
        int x = anchor.Left + (anchor.Width - width) / 2;
        int y = anchor.Top - gap - height + margin;

        int minX = workArea.Left - margin;
        int maxX = Math.Max(minX, workArea.Right + margin - width);
        int minY = workArea.Top - margin;
        int maxY = Math.Max(minY, workArea.Bottom + margin - height);

        return (Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }
}
