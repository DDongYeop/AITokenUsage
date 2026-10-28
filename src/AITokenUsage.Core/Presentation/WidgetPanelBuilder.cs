using AITokenUsage.Core.Models;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Presentation;

/// <summary>Builds the widget's two-line model from a provider view (spec §2.1 state table).</summary>
public static class WidgetPanelBuilder
{
    public const string EmptyTitle = "AI 사용량";
    public const string EmptyMessage = "우클릭하여 설정";
    public const string NoDataMessage = "데이터 없음";

    /// <summary>Shown when no provider is enabled: "AI 사용량 · 우클릭하여 설정".</summary>
    public static WidgetPanelModel Empty() =>
        new("", EmptyTitle, false, WidgetRow.Message(EmptyMessage), null, 0, -1);

    public static WidgetPanelModel Build(
        ProviderView view,
        string? line1Key,
        string? line2Key,
        ValueMode mode,
        int dotCount,
        int dotIndex)
    {
        ArgumentNullException.ThrowIfNull(view);

        (UsageMeter? first, UsageMeter? second) = MeterSelector.Select(view.DisplayMeters, line1Key, line2Key);
        if (first is null)
        {
            return new WidgetPanelModel(
                view.ProviderId, view.DisplayName, false, WidgetRow.Message(ShortStatus(view)), null, dotCount, dotIndex);
        }

        bool stale = view.IsStale;
        WidgetRow row1 = ToRow(first, mode, stale);
        WidgetRow? row2 = second is null ? null : ToRow(second, mode, stale);

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
            ProviderStatus.NotInstalled => "설치 안 됨",
            ProviderStatus.SignedOut => "로그인 필요",
            ProviderStatus.WaitingForCliRefresh => "갱신 대기",
            ProviderStatus.Unsupported =>
                current.StatusMessage?.Contains("지원 종료", StringComparison.Ordinal) == true ? "지원 종료" : "지원 안 됨",
            ProviderStatus.RateLimited => "요청 제한",
            ProviderStatus.Error => "오류",
            _ => string.IsNullOrWhiteSpace(current.StatusMessage) ? NoDataMessage : current.StatusMessage.Trim(),
        };
    }

    public static WidgetRow ToRow(UsageMeter meter, ValueMode mode, bool dimmed)
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
            dimmed);
    }

    private static bool ReplacesSecondLine(ProviderStatus status) => status is
        ProviderStatus.WaitingForCliRefresh or
        ProviderStatus.SignedOut or
        ProviderStatus.NotInstalled or
        ProviderStatus.Unsupported;
}
