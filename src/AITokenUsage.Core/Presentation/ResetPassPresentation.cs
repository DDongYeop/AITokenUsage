using System.Globalization;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Presentation;

internal static class ResetPassPresentation
{
    public const string Key = "reset-passes";

    public static ResetPassBalance? Current(ProviderView? view) =>
        view?.Current.Status == ProviderStatus.Ok &&
        ProviderIds.KindOf(view.ProviderId) is ProviderIds.Claude or ProviderIds.Codex
            ? view.Current.ResetPasses
            : null;

    public static bool Selected(string? first, string? second) => first == Key || second == Key;

    public static UsageMeter CountMeter(ResetPassBalance balance) =>
        new(Key, "", Strings.ResetPassTitle, null, null,
            TextOverride: string.Format(CultureInfo.CurrentCulture, Strings.ResetPassCompact, balance.AvailableCount));

    public static CardRow Card(ResetPassBalance balance, TimeZoneInfo zone)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        var details = new List<string>();
        foreach (ResetPass pass in balance.Passes)
        {
            string label = string.IsNullOrWhiteSpace(pass.Label)
                ? string.Format(culture, Strings.ResetPassNumber, details.Count + 1)
                : pass.Label.Trim();
            string expiry = pass.ExpiresAt is { } at
                ? TimeZoneInfo.ConvertTime(at, zone).ToString("g", culture)
                : pass.NoExpiry ? Strings.ResetPassNoExpiry : Strings.ResetPassUnknownExpiry;
            details.Add($"{label} · {expiry}");
        }

        int missing = balance.AvailableCount - details.Count;
        if (missing > 0)
        {
            details.Add(string.Format(culture, Strings.ResetPassMissingDetails, missing));
        }

        return new CardRow(Strings.ResetPassTitle, null, balance.AvailableCount.ToString(culture),
            string.Join(Environment.NewLine, details), Severity.Normal, true, false);
    }
}
