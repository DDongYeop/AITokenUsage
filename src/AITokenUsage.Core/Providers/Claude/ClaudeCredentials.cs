namespace AITokenUsage.Core.Providers.Claude;

public enum ClaudeCredentialState
{
    Ok,
    NotInstalled,
    SignedOut,
    MissingProfileScope,
    Unreadable,

    /// <summary>The widget-owned login is missing, broken or refused by the token endpoint: sign in again.</summary>
    ReauthRequired,

    /// <summary>Renewing the widget-owned login failed for now (network, 429, 5xx, bad body). The tokens are kept.</summary>
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
