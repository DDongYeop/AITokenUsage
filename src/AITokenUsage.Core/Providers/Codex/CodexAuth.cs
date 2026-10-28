namespace AITokenUsage.Core.Providers.Codex;

/// <summary>How the Codex CLI is logged in, reduced to what the widget cares about.</summary>
public enum CodexAuthMode
{
    /// <summary>ChatGPT plan login (chatgpt, chatgptAuthTokens). Has plan limits.</summary>
    ChatGpt,
    /// <summary>API key style login (apikey, bedrockApiKey, bedrockAccessKeys). No plan limits.</summary>
    ApiKey,
    /// <summary>personalAccessToken, agentIdentity, headers or an unknown mode. Only the app-server can read limits.</summary>
    Other,
}

/// <summary>Parsed auth.json. ToString never prints tokens.</summary>
public sealed record CodexAuth(
    CodexAuthMode Mode,
    string? AccessToken,
    string? AccountId,
    bool IsFedramp,
    string? PlanTypeClaim,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? LastRefresh)
{
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LastRefreshMaxAge = TimeSpan.FromDays(8);

    public bool HasAccessToken => !string.IsNullOrEmpty(AccessToken);

    /// <summary>exp > now + 5 min; when exp is unknown, last_refresh younger than 8 days.</summary>
    public bool IsFresh(DateTimeOffset now)
    {
        if (!HasAccessToken) return false;
        if (AccessTokenExpiresAt is { } expiresAt) return expiresAt > now + ExpiryMargin;
        if (LastRefresh is { } refreshedAt) return now - refreshedAt < LastRefreshMaxAge;
        return false;
    }

    public override string ToString()
        => $"CodexAuth {{ Mode = {Mode}, HasAccessToken = {HasAccessToken}, HasAccountId = {AccountId is not null} }}";
}

public enum CodexAuthReadStatus { Ok, Missing, Unreadable }

public sealed record CodexAuthReadResult(CodexAuthReadStatus Status, CodexAuth? Auth)
{
    public static readonly CodexAuthReadResult Missing = new(CodexAuthReadStatus.Missing, null);
    public static readonly CodexAuthReadResult Unreadable = new(CodexAuthReadStatus.Unreadable, null);
}
