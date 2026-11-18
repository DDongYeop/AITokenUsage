namespace AITokenUsage.Core.Providers.Claude;

public enum ClaudeCredentialState
{
    Ok,
    NotInstalled,
    SignedOut,
    MissingProfileScope,
    Unreadable,

    /// <summary>An extra account's login is missing, or Claude Code failed to renew it several times in a row: sign in again.</summary>
    ReauthRequired,

    /// <summary>Claude Code did not renew an extra account's expired login this time; a later read tries again.</summary>
    RenewFailed,
}

/// <summary>The values one usage request needs. Claude Code's own file is read-only: never written back.</summary>
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
