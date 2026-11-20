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

    public static (UsageMeter? First, UsageMeter? Second) SelectForView(ProviderView view, string? line1Key, string? line2Key)
    {
        IReadOnlyList<UsageMeter> meters = view.DisplayMeters;
        // Only an explicitly requested pass can enter selection. That line claims it before quota fallback runs.
        if (ResetPassPresentation.Selected(line1Key, line2Key) && ResetPassPresentation.Current(view) is { } balance)
        {
            meters = [.. meters, ResetPassPresentation.CountMeter(balance)];
        }

        return Select(meters, line1Key, line2Key);
    }

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
        // A single available meter can match line 2. Render it in the widget's primary row.
        if (first is null) return (second, null);
        second ??= meters.FirstOrDefault(m => !ReferenceEquals(m, first));

        return (first, second);
    }

    private static UsageMeter? Find(IReadOnlyList<UsageMeter> meters, string? key) =>
        string.IsNullOrEmpty(key)
            ? null
            : meters.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
}
