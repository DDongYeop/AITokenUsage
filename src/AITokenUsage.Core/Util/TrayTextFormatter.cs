using System.Globalization;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Util;

/// <summary>Text for the tray icon: the tooltip ("Claude 5h 14% · 주 50%") and the number drawn inside the icon.</summary>
public static class TrayTextFormatter
{
    /// <summary>NotifyIcon.Text limit on .NET (longer strings throw).</summary>
    public const int MaxTooltipLength = 127;
    public const string AppTitle = "AI 사용량";
    public const string NoValue = MeterSelector.MissingValue;
    public const string StaleSuffix = " (이전 값)";
    private const string Separator = " · ";

    /// <summary>Line 1 exactly as the widget shows it (MeterSelector, the shared rule).</summary>
    public static UsageMeter? SelectLine1(ProviderView view, ProviderSettings? lines)
    {
        ArgumentNullException.ThrowIfNull(view);
        return MeterSelector.Select(view.DisplayMeters, lines?.Line1Key, lines?.Line2Key).First;
    }

    /// <summary>Line 2 exactly as the widget shows it (MeterSelector, the shared rule). Null when there is no other meter.</summary>
    public static UsageMeter? SelectLine2(ProviderView view, ProviderSettings? lines)
    {
        ArgumentNullException.ThrowIfNull(view);
        return MeterSelector.Select(view.DisplayMeters, lines?.Line1Key, lines?.Line2Key).Second;
    }

    /// <summary>Clamped used percent, or 100 − used in Remaining mode. Null when the meter has no percent.</summary>
    public static double? DisplayPercent(UsageMeter meter, ValueMode mode)
    {
        double? used = SeverityRules.Clamp(meter.UsedPercent);
        if (used is null)
        {
            return null;
        }

        return mode == ValueMode.Remaining ? 100 - used.Value : used.Value;
    }

    public static string FormatValue(UsageMeter meter, ValueMode mode)
    {
        if (!string.IsNullOrEmpty(meter.TextOverride))
        {
            return meter.TextOverride;
        }

        double? percent = DisplayPercent(meter, mode);
        return percent is null ? NoValue : Round(percent.Value) + "%";
    }

    public static string IconText(UsageMeter? meter, ValueMode mode)
    {
        if (meter is null)
        {
            return NoValue;
        }

        double? percent = DisplayPercent(meter, mode);
        return percent is null ? NoValue : Round(percent.Value);
    }

    public static string StatusText(ProviderStatus status) => status switch
    {
        ProviderStatus.NotInstalled => "설치 안 됨",
        ProviderStatus.SignedOut => "로그인 필요",
        ProviderStatus.WaitingForCliRefresh => "갱신 대기",
        ProviderStatus.Unsupported => "지원 안 됨",
        ProviderStatus.RateLimited => "요청 제한",
        ProviderStatus.Error => "오류",
        _ => "불러오는 중…",
    };

    public static string Tooltip(ProviderView? view, ProviderSettings? lines, ValueMode mode)
    {
        if (view is null)
        {
            return AppTitle;
        }

        string text;
        if (!view.HasAnyData)
        {
            string status = string.IsNullOrWhiteSpace(view.Current.StatusMessage)
                ? StatusText(view.Current.Status)
                : view.Current.StatusMessage;
            text = view.DisplayName + Separator + status;
        }
        else
        {
            (UsageMeter? line1, UsageMeter? line2) = MeterSelector.Select(view.DisplayMeters, lines?.Line1Key, lines?.Line2Key);
            var parts = new List<string>(2);
            if (line1 is not null)
            {
                parts.Add(Describe(line1, mode));
            }

            // Both keys may name the same meter (the widget then repeats it); the tooltip states it once.
            if (line2 is not null && !ReferenceEquals(line1, line2))
            {
                parts.Add(Describe(line2, mode));
            }

            text = view.DisplayName + " " + string.Join(Separator, parts);
            if (view.IsStale)
            {
                text += StaleSuffix;
            }
        }

        return Truncate(text);
    }

    private static string Describe(UsageMeter meter, ValueMode mode) =>
        string.IsNullOrEmpty(meter.TextOverride)
            ? $"{meter.ShortLabel} {FormatValue(meter, mode)}".Trim()
            : meter.TextOverride;

    private static string Round(double value) =>
        ((int)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static string Truncate(string text) =>
        text.Length <= MaxTooltipLength ? text : string.Concat(text.AsSpan(0, MaxTooltipLength - 1), "…");
}
