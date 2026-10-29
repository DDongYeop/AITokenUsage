namespace AITokenUsage.Core.Taskbar;

/// <summary>Widget size in DIP (v1.1 spec §2.1).</summary>
public static class WidgetMetrics
{
    public const double HeightDip = 40;
    public const double NarrowWidthDip = 176;
    public const double ResetColumnDip = 30;

    /// <summary>Gap between the value column and the reset column.</summary>
    public const double ResetGapDip = 4;

    public static double WidthDip(bool showResetTime) =>
        showResetTime ? NarrowWidthDip + ResetGapDip + ResetColumnDip : NarrowWidthDip;
}
