namespace AITokenUsage.Core.Taskbar;

/// <summary>
/// Picks the app-button group out of every Taskbar.TaskListButtonAutomationPeer rectangle. A plain union would also
/// swallow buttons that sit apart from the group (e.g. an overflow button far right) and push BeforeTray's left
/// boundary towards the tray.
/// </summary>
public static class TaskbarButtonGrouping
{
    /// <summary>Largest gap between neighbouring buttons of one group at 96 DPI. Callers scale it by dpi / 96.</summary>
    public const int MaxGapAt96Dpi = 16;

    /// <summary>
    /// Sorts by Left, starts at the first button whose Left is at or after <paramref name="startRight"/> (the leftmost
    /// button when null) and unions neighbours while the gap to the next one is at most <paramref name="maxGap"/> px.
    /// Null when there is no starting button.
    /// </summary>
    public static PixelRect? GroupContiguous(IReadOnlyList<PixelRect> buttons, int? startRight, int maxGap)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        ArgumentOutOfRangeException.ThrowIfNegative(maxGap);

        PixelRect[] sorted = buttons.OrderBy(b => b.Left).ThenBy(b => b.Right).ToArray();
        int first = startRight is int start
            ? Array.FindIndex(sorted, b => b.Left >= start)
            : (sorted.Length > 0 ? 0 : -1);
        if (first < 0)
        {
            return null;
        }

        PixelRect group = sorted[first];
        for (int i = first + 1; i < sorted.Length; i++)
        {
            PixelRect next = sorted[i];
            if (next.Left - group.Right > maxGap)
            {
                break;
            }

            group = new PixelRect(
                Math.Min(group.Left, next.Left),
                Math.Min(group.Top, next.Top),
                Math.Max(group.Right, next.Right),
                Math.Max(group.Bottom, next.Bottom));
        }

        return group;
    }
}
