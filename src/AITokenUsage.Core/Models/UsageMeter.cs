namespace AITokenUsage.Core.Models;

/// <summary>One usage limit, for example the Claude 5-hour session window.</summary>
/// <param name="Key">Stable key referenced by settings: "session", "weekly", "weekly:fable", "pro", "monthly", ...</param>
/// <param name="ShortLabel">2-3 character widget label: "5h", "주", "Pro", "월".</param>
/// <param name="LongLabel">Detail card label: "현재 세션 (5시간)".</param>
/// <param name="UsedPercent">Used percentage (0-100). Clamped only when displayed.</param>
/// <param name="ResetsAt">When the window resets, if known.</param>
/// <param name="ServerSeverity">Severity reported by the provider, if any.</param>
/// <param name="TextOverride">Text-only line such as "↻ 11/1" or "12 크레딧 사용".</param>
public sealed record UsageMeter(
    string Key,
    string ShortLabel,
    string LongLabel,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    Severity? ServerSeverity = null,
    string? TextOverride = null);
