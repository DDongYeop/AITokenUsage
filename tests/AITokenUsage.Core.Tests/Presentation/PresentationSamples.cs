using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Tests.Presentation;

/// <summary>Shared builders for presentation tests. Now = 2026-10-28 12:00 in the fixed +09:00 test zone.</summary>
internal static class PresentationSamples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    public static readonly TimeZoneInfo Kst =
        TimeZoneInfo.CreateCustomTimeZone("Test KST", TimeSpan.FromHours(9), "Test KST", "Test KST");

    public static UsageMeter Meter(
        string key,
        string shortLabel,
        double? used,
        DateTimeOffset? resetsAt = null,
        Severity? severity = null,
        string? text = null,
        string? longLabel = null) =>
        new(key, shortLabel, longLabel ?? "긴 " + key, used, resetsAt, severity, text);

    /// <summary>Ok snapshot with plan "Pro", fetched 3 minutes before <see cref="Now"/>.</summary>
    public static UsageSnapshot Ok(string providerId, params UsageMeter[] meters) =>
        new(providerId, ProviderStatus.Ok, "Pro", meters, Now.AddMinutes(-3));

    /// <summary>Failure/status snapshot fetched 1 minute before <see cref="Now"/> (11:59 local).</summary>
    public static UsageSnapshot Status(string providerId, ProviderStatus status, string? message = null, TimeSpan? retryAfter = null) =>
        UsageSnapshot.WithStatus(providerId, status, Now.AddMinutes(-1), message, retryAfter);

    public static ProviderView View(UsageSnapshot current, UsageSnapshot? lastGood = null, string displayName = "Claude") =>
        new(current.ProviderId, displayName, current, lastGood);
}
