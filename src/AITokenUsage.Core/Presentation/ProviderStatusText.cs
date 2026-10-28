using System.Globalization;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Presentation;

/// <summary>Korean status sentences for the settings window rows (spec §2.5) and the detail card (spec §2.3).</summary>
public static class ProviderStatusText
{
    public const string Connected = "연결됨";
    public const string NotInstalled = "설치 안 됨";
    public const string SignInRequired = "로그인 필요";
    public const string NotSupported = "지원 안 됨";
    public const string NoData = "데이터 없음";
    public const string RetrySoon = "잠시 후 다시 시도";

    private const string Separator = " · ";
    private const string Dash = " — ";

    /// <summary>The tool whose login the app waits for: "Claude Code", "Codex", "Gemini CLI", "Copilot".</summary>
    public static string CliName(string providerId) => providerId switch
    {
        ProviderIds.Claude => "Claude Code",
        ProviderIds.Codex => "Codex",
        ProviderIds.Gemini => "Gemini CLI",
        ProviderIds.Copilot => "Copilot",
        _ => providerId,
    };

    /// <summary>"요청 제한 — 14:20 이후 재시도" (FetchedAt + RetryAfter in <paramref name="zone"/>) or "요청 제한 — 잠시 후 다시 시도".</summary>
    public static string RateLimited(UsageSnapshot snapshot, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(zone);

        string? clock = RetryClock(snapshot, zone);
        return clock is null ? $"요청 제한{Dash}{RetrySoon}" : $"요청 제한{Dash}{clock} 이후 재시도";
    }

    /// <summary>One line for the settings window provider row.</summary>
    public static string ForSettings(ProviderView view, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(zone);

        UsageSnapshot current = view.Current;
        string? message = Clean(current.StatusMessage);

        switch (current.Status)
        {
            case ProviderStatus.Ok when current.Meters.Count > 0:
                string age = new ResetTimeFormatter(zone).FormatAgo(current.FetchedAt, now);
                return Join(Connected, view.DisplayPlan, message ?? age);
            case ProviderStatus.Ok:
                return message ?? NoData;
            case ProviderStatus.NotInstalled:
                return message ?? NotInstalled;
            case ProviderStatus.SignedOut:
                return Prefixed(SignInRequired, message);
            case ProviderStatus.WaitingForCliRefresh:
                return $"{CliName(view.ProviderId)}의 로그인 갱신 대기 중";
            case ProviderStatus.Unsupported:
                return message ?? NotSupported;
            case ProviderStatus.RateLimited:
                return RateLimited(current, zone);
            default:
                return ErrorText(current, message, zone);
        }
    }

    /// <summary>Status lines under the detail card: the reason, then "마지막 정상 값 · 3분 전" when older values are shown.</summary>
    public static IReadOnlyList<string> ForCard(ProviderView view, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(zone);

        UsageSnapshot current = view.Current;
        string? message = Clean(current.StatusMessage);
        var formatter = new ResetTimeFormatter(zone);

        if (current.Status == ProviderStatus.Ok)
        {
            if (current.Meters.Count == 0)
            {
                return new[] { message ?? NoData };
            }

            string updated = $"{formatter.FormatAgo(current.FetchedAt, now)} 갱신";
            return new[] { message is null ? updated : updated + Separator + message };
        }

        string reason = current.Status == ProviderStatus.WaitingForCliRefresh && message is not null
            ? message
            : ForSettings(view, now, zone);

        if (view.LastGood is { } lastGood)
        {
            return new[] { reason, $"마지막 정상 값{Separator}{formatter.FormatAgo(lastGood.FetchedAt, now)}" };
        }

        return new[] { reason };
    }

    private static string ErrorText(UsageSnapshot snapshot, string? message, TimeZoneInfo zone)
    {
        string reason = message ?? "오류";
        string? clock = RetryClock(snapshot, zone);
        if (clock is null)
        {
            return reason;
        }

        string retrySoonSuffix = Dash + RetrySoon;
        if (reason.EndsWith(retrySoonSuffix, StringComparison.Ordinal))
        {
            reason = reason[..^retrySoonSuffix.Length];
        }

        return $"{reason}{Dash}{clock} 이후 재시도";
    }

    private static string? RetryClock(UsageSnapshot snapshot, TimeZoneInfo zone)
    {
        if (snapshot.RetryAfter is not TimeSpan retry || retry <= TimeSpan.Zero)
        {
            return null;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(snapshot.FetchedAt + retry, zone);
        return local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Prefixed(string prefix, string? message)
    {
        if (message is null)
        {
            return prefix;
        }

        return message.StartsWith(prefix, StringComparison.Ordinal) ? message : prefix + Dash + message;
    }

    private static string Join(params string?[] parts) =>
        string.Join(Separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
