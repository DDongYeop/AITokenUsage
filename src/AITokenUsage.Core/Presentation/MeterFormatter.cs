using System.Globalization;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Presentation;

/// <summary>Turns a meter into the number and bar length the widget and the detail card draw.</summary>
public static class MeterFormatter
{
    /// <summary>"—" — the same constant the tray uses (TrayTextFormatter.NoValue).</summary>
    public const string MissingValue = MeterSelector.MissingValue;

    /// <summary>
    /// Used % (or 100 − used in Remaining mode), clamped to 0..100.
    /// Null for text lines (TextOverride) and for unknown or NaN percentages.
    /// </summary>
    public static double? DisplayPercent(UsageMeter meter, ValueMode mode)
    {
        ArgumentNullException.ThrowIfNull(meter);

        if (meter.TextOverride is not null || SeverityRules.Clamp(meter.UsedPercent) is not double used)
        {
            return null;
        }

        return mode == ValueMode.Remaining ? 100 - used : used;
    }

    /// <summary>0..1 bar length, or null when no bar should be drawn.</summary>
    public static double? BarFraction(UsageMeter meter, ValueMode mode) =>
        DisplayPercent(meter, mode) is double percent ? percent / 100 : null;

    /// <summary>"14%" (rounded half away from zero), the TextOverride text, or "—".</summary>
    public static string ValueText(UsageMeter meter, ValueMode mode)
    {
        ArgumentNullException.ThrowIfNull(meter);

        if (meter.TextOverride is { } text)
        {
            return text;
        }

        return DisplayPercent(meter, mode) is double percent
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(percent, MidpointRounding.AwayFromZero):0}%")
            : MissingValue;
    }
}
