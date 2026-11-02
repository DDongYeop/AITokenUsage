using AITokenUsage.Core.Providers.Claude;

namespace AITokenUsage.Core.Tests.Providers.Claude;

/// <summary>Widget-owned Claude login values. Every token is unique text so "not in the log" checks are meaningful.</summary>
internal static class ClaudeOwnedTestData
{
    public const string AccessToken1 = "owned-access-token-one";
    public const string RefreshToken1 = "owned-refresh-token-one";
    public const string AccessToken2 = "owned-access-token-two";
    public const string RefreshToken2 = "owned-refresh-token-two";
    public const string AccessToken3 = "owned-access-token-three";
    public const string RefreshToken3 = "owned-refresh-token-three";

    public const string AccountId = "claude#abc123";
    public const string SecretName = "claude-account-abc123";

    public static readonly string[] AllTokens =
    [
        AccessToken1, RefreshToken1, AccessToken2, RefreshToken2, AccessToken3, RefreshToken3,
    ];

    public static readonly string[] Scopes = ["user:profile", "user:inference"];

    public static ClaudeOwnedCredentials Login(
        string accessToken = AccessToken1,
        string refreshToken = RefreshToken1,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? refreshTokenExpiresAt = null,
        IReadOnlyList<string>? scopes = null) => new(
            accessToken,
            refreshToken,
            expiresAt ?? ClaudeTestData.Now.AddHours(8),
            refreshTokenExpiresAt,
            scopes ?? Scopes,
            "max",
            "default_claude_max_20x",
            "me@example.com",
            "Example Org");

    /// <summary>A token endpoint success body.</summary>
    public static string TokenResponse(
        string? accessToken = AccessToken2,
        string? refreshToken = RefreshToken2,
        double? expiresIn = 28800,
        double? refreshTokenExpiresIn = null,
        string? scope = null)
    {
        var parts = new List<string>();
        if (accessToken is not null)
        {
            parts.Add($"\"access_token\":\"{accessToken}\"");
        }

        if (refreshToken is not null)
        {
            parts.Add($"\"refresh_token\":\"{refreshToken}\"");
        }

        if (expiresIn is { } e)
        {
            parts.Add($"\"expires_in\":{e.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (refreshTokenExpiresIn is { } r)
        {
            parts.Add($"\"refresh_token_expires_in\":{r.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (scope is not null)
        {
            parts.Add($"\"scope\":\"{scope}\"");
        }

        parts.Add("\"token_type\":\"Bearer\"");
        return "{" + string.Join(",", parts) + "}";
    }
}
