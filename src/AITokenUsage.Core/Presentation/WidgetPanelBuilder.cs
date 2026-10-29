using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Presentation;

/// <summary>Builds the widget's two-line model from a provider view (spec §2.1 state table).</summary>
public static class WidgetPanelBuilder
{
    public static string EmptyTitle => Strings.AppTitle;
    public static string EmptyMessage => Strings.WidgetEmptyMessage;
    public static string NoDataMessage => Strings.StatusNoData;

    /// <summary>Shown when no provider is enabled: "AI 사용량 · 우클릭하여 설정".</summary>
    public static WidgetPanelModel Empty() =>
        new("", EmptyTitle, false, WidgetRow.Message(EmptyMessage), null, 0, -1);

    public static WidgetPanelModel Build(
        ProviderView view,
        string? line1Key,
        string? line2Key,
        ValueMode mode,
        int dotCount,
        int dotIndex,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);

        (UsageMeter? first, UsageMeter? second) = MeterSelector.Select(view.DisplayMeters, line1Key, line2Key);
        if (first is null)
        {
            return new WidgetPanelModel(
                view.ProviderId, view.DisplayName, false, WidgetRow.Message(ShortStatus(view)), null, dotCount, dotIndex);
        }

        bool stale = view.IsStale;
        WidgetRow row1 = ToRow(first, mode, stale, now);
        WidgetRow? row2 = second is null ? null : ToRow(second, mode, stale, now);

        if (stale && ReplacesSecondLine(view.Current.Status))
        {
            row2 = WidgetRow.Message(ShortStatus(view));
        }

        return new WidgetPanelModel(view.ProviderId, view.DisplayName, stale, row1, row2, dotCount, dotIndex);
    }

    /// <summary>Two-to-five character status for the widget (spec §2.1 "상태별 위젯 표시").</summary>
    public static string ShortStatus(ProviderView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        UsageSnapshot current = view.Current;
        return current.Status switch
        {
            ProviderStatus.NotInstalled => Strings.StatusNotInstalled,
            ProviderStatus.SignedOut => Strings.StatusSignedOut,
            ProviderStatus.WaitingForCliRefresh => Strings.StatusWaitingForRefresh,
            ProviderStatus.Unsupported =>
                current.StatusMessage?.Contains(Strings.StatusDiscontinuedMarker, StringComparison.OrdinalIgnoreCase) == true
                    ? Strings.StatusDiscontinued
                    : Strings.StatusNotSupported,
            ProviderStatus.RateLimited => Strings.StatusRateLimited,
            ProviderStatus.Error => Strings.StatusError,
            _ => string.IsNullOrWhiteSpace(current.StatusMessage) ? NoDataMessage : current.StatusMessage.Trim(),
        };
    }

    public static WidgetRow ToRow(UsageMeter meter, ValueMode mode, bool dimmed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(meter);

        Severity severity = SeverityRules.Effective(meter);
        if (meter.TextOverride is { } text)
        {
            bool labelInText = meter.ShortLabel.Length > 0 && text.StartsWith(meter.ShortLabel, StringComparison.Ordinal);
            return new WidgetRow(WidgetRowKind.Text, labelInText ? "" : meter.ShortLabel, null, text, severity, dimmed);
        }

        return new WidgetRow(
            WidgetRowKind.Meter,
            meter.ShortLabel,
            MeterFormatter.BarFraction(meter, mode),
            MeterFormatter.ValueText(meter, mode),
            severity,
            dimmed,
            CompactDurationFormatter.Format(meter.ResetsAt, now));
    }

    private static bool ReplacesSecondLine(ProviderStatus status) => status is
        ProviderStatus.WaitingForCliRefresh or
        ProviderStatus.SignedOut or
        ProviderStatus.NotInstalled or
        ProviderStatus.Unsupported;
}
