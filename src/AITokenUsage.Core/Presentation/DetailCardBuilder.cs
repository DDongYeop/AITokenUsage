using AITokenUsage.Core.Models;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Presentation;

/// <summary>Builds the hover card content (spec §2.3).</summary>
public static class DetailCardBuilder
{
    public static DetailCardModel Build(ProviderView view, ValueMode mode, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(zone);

        var formatter = new ResetTimeFormatter(zone);
        bool dimmed = view.IsStale;
        CardRow[] rows = view.DisplayMeters.Select(m => ToRow(m, mode, now, formatter, dimmed)).ToArray();

        string? extra = view.Current.Status == ProviderStatus.Ok
            ? view.Current.ExtraLine
            : view.LastGood?.ExtraLine ?? view.Current.ExtraLine;

        return new DetailCardModel(
            view.ProviderId,
            view.DisplayName,
            view.DisplayPlan,
            rows,
            string.IsNullOrWhiteSpace(extra) ? null : extra.Trim(),
            ProviderStatusText.ForCard(view, now, zone),
            view.IsStale);
    }

    /// <summary>
    /// Percent rows: long label, bar, value and "3시간 12분 후 · 오늘 15:50".
    /// Text rows (TextOverride): the text and the reset date "11/1(일)".
    /// </summary>
    public static CardRow ToRow(UsageMeter meter, ValueMode mode, DateTimeOffset now, ResetTimeFormatter formatter, bool dimmed)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(formatter);

        Severity severity = SeverityRules.Effective(meter);

        if (meter.TextOverride is { } text)
        {
            string date = meter.ResetsAt is { } at ? formatter.FormatShortDateWithDay(at) : "";
            return new CardRow(meter.LongLabel, null, text, date, severity, true, dimmed);
        }

        string reset = meter.ResetsAt is { } resetsAt ? formatter.FormatRelative(resetsAt, now) : "";
        return new CardRow(
            meter.LongLabel,
            MeterFormatter.BarFraction(meter, mode),
            MeterFormatter.ValueText(meter, mode),
            reset,
            severity,
            false,
            dimmed);
    }
}
