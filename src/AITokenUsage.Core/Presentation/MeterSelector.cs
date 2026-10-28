using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Presentation;

/// <summary>
/// Picks the two meters a provider shows from the configured line keys.
/// The single selection rule shared by the tray (TrayTextFormatter) and the widget (WidgetPanelBuilder).
/// </summary>
public static class MeterSelector
{
    /// <summary>Placeholder for a line that has no value (em dash), used by the tray and the widget alike.</summary>
    public const string MissingValue = "—";

    /// <summary>
    /// Each line takes the meter with its configured key. A line whose key is blank or missing
    /// takes the first meter (in provider order) that the other line does not already show.
    /// Both lines may show the same meter only when both keys name it.
    /// </summary>
    public static (UsageMeter? First, UsageMeter? Second) Select(IReadOnlyList<UsageMeter> meters, string? line1Key, string? line2Key)
    {
        ArgumentNullException.ThrowIfNull(meters);

        UsageMeter? first = Find(meters, line1Key);
        UsageMeter? second = Find(meters, line2Key);

        first ??= meters.FirstOrDefault(m => !ReferenceEquals(m, second));
        second ??= meters.FirstOrDefault(m => !ReferenceEquals(m, first));

        return (first, second);
    }

    private static UsageMeter? Find(IReadOnlyList<UsageMeter> meters, string? key) =>
        string.IsNullOrEmpty(key)
            ? null
            : meters.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
}
