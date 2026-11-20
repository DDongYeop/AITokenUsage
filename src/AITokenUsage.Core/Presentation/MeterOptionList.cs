using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Presentation;

/// <summary>Items of the settings window's "1줄"/"2줄" drop-downs.</summary>
public static class MeterOptionList
{
    public static string MissingSuffix => Strings.MeterMissingSuffix;

    public static IReadOnlyList<MeterOption> ForView(
        IReadOnlyList<MeterOption> staticOptions, ProviderView? view, IEnumerable<string?> selectedKeys)
    {
        if (view is not null && ProviderIds.KindOf(view.ProviderId) is ProviderIds.Claude or ProviderIds.Codex)
        {
            string label = ResetPassPresentation.Current(view) is null
                ? Strings.ResetPassTitle + MissingSuffix : Strings.ResetPassTitle;
            staticOptions = [.. staticOptions, new(ResetPassPresentation.Key, label)];
        }

        return Build(staticOptions, view?.DisplayMeters ?? [], selectedKeys);
    }

    /// <summary>
    /// Static provider options first, then meters seen in the current view (by LongLabel),
    /// then any currently selected key that neither list has (so the selection is never lost).
    /// Blank and duplicate keys are skipped; the first occurrence wins.
    /// </summary>
    public static IReadOnlyList<MeterOption> Build(
        IReadOnlyList<MeterOption> staticOptions,
        IReadOnlyList<UsageMeter> meters,
        IEnumerable<string?> selectedKeys)
    {
        ArgumentNullException.ThrowIfNull(staticOptions);
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(selectedKeys);

        var result = new List<MeterOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (MeterOption option in staticOptions)
        {
            if (!string.IsNullOrEmpty(option.Key) && seen.Add(option.Key))
            {
                result.Add(option);
            }
        }

        foreach (UsageMeter meter in meters)
        {
            if (!string.IsNullOrEmpty(meter.Key) && seen.Add(meter.Key))
            {
                result.Add(new MeterOption(meter.Key, string.IsNullOrWhiteSpace(meter.LongLabel) ? meter.Key : meter.LongLabel));
            }
        }

        foreach (string? key in selectedKeys)
        {
            if (!string.IsNullOrEmpty(key) && seen.Add(key))
            {
                string label = key == ResetPassPresentation.Key ? Strings.ResetPassTitle : key;
                result.Add(new MeterOption(key, label + MissingSuffix));
            }
        }

        return result;
    }

    public static bool SameOptions(IReadOnlyList<MeterOption> a, IReadOnlyList<MeterOption> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Count == b.Count && a.SequenceEqual(b);
    }
}
