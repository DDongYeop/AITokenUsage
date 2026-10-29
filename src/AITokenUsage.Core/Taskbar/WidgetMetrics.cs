namespace AITokenUsage.Core.Taskbar;

/// <summary>Widget size in DIP (v1.1 spec §2.1).</summary>
public static class WidgetMetrics
{
    public const double HeightDip = 40;
    public const double NarrowWidthDip = 176;
    /// <summary>Fits the widest compact text, "23h59m" (~36 DIP at 10 px Segoe UI).</summary>
    public const double ResetColumnDip = 38;

    /// <summary>Gap between the value column and the reset column.</summary>
    public const double ResetGapDip = 4;

    public static double WidthDip(bool showResetTime) =>
        showResetTime ? NarrowWidthDip + ResetGapDip + ResetColumnDip : NarrowWidthDip;
}
