namespace AITokenUsage.Core.Providers.Claude;

public enum ClaudeCredentialState
{
    Ok,
    NotInstalled,
    SignedOut,
    MissingProfileScope,
    Unreadable,
}

/// <summary>The subset of claudeAiOauth the widget needs. Read-only: never written back.</summary>
public sealed record ClaudeCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string>? Scopes,
    string? SubscriptionType,
    string? RateLimitTier)
{
    // Records print every member by default; never let the token reach a log line.
    public override string ToString() =>
        $"ClaudeCredentials {{ ExpiresAt = {ExpiresAt:O}, SubscriptionType = {SubscriptionType}, RateLimitTier = {RateLimitTier} }}";
}

public sealed record ClaudeCredentialReadResult(ClaudeCredentialState State, ClaudeCredentials? Credentials = null);
