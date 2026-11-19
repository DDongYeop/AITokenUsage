namespace AITokenUsage.Core.Models;

/// <summary>Result of one provider fetch. StatusMessage is a Korean, user-facing message.</summary>
public sealed record UsageSnapshot(
    string ProviderId,
    ProviderStatus Status,
    string? PlanLabel,
    IReadOnlyList<UsageMeter> Meters,
    DateTimeOffset FetchedAt,
    string? StatusMessage = null,
    TimeSpan? RetryAfter = null,
    string? ExtraLine = null)
{
    /// <summary>Read-only balance reported by the current fetch. Null when the source omits it.</summary>
    public ResetPassBalance? ResetPasses { get; init; }

    public static UsageSnapshot WithStatus(string providerId, ProviderStatus status, DateTimeOffset now,
        string? message = null, TimeSpan? retryAfter = null)
        => new(providerId, status, null, Array.Empty<UsageMeter>(), now, message, retryAfter);
}
