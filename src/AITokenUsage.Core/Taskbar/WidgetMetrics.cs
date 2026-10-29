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

    /// <summary>Gap before the PC column, divider included.</summary>
    public const double SystemGapDip = 8;

    /// <summary>The PC column: label (24) + value (32).</summary>
    public const double SystemColumnDip = 56;

    /// <summary>Width of the widget; systemBeside adds the always-visible PC column (default: not shown).</summary>
    public static double WidthDip(bool showResetTime, bool systemBeside = false) =>
        NarrowWidthDip
        + (showResetTime ? ResetGapDip + ResetColumnDip : 0)
        + (systemBeside ? SystemGapDip + SystemColumnDip : 0);
}
