using System.Text.Json.Nodes;

namespace AITokenUsage.Core.Tests.Providers.Claude;

internal static class ClaudeTestData
{
    /// <summary>Fixed test clock start: 2026-10-28T03:00:00Z (epoch ms 1793156400000).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    public const string TokenA = "test-access-token";
    public const string TokenB = "test-access-token-2";

    public static readonly string[] DefaultScopes =
    [
        "user:file_upload",
        "user:inference",
        "user:mcp_servers",
        "user:plugins",
        "user:profile",
        "user:sessions:claude_code",
    ];

    /// <summary>Builds a fake .credentials.json. expiresAtMs defaults to Now + 8 h.</summary>
    public static string Credentials(
        string accessToken = TokenA,
        long? expiresAtMs = null,
        string[]? scopes = null,
        bool includeScopes = true,
        string? subscriptionType = "team",
        string? rateLimitTier = "default_claude_max_5x")
    {
        var oauth = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = "test-refresh-token",
            ["expiresAt"] = expiresAtMs ?? Now.AddHours(8).ToUnixTimeMilliseconds(),
            ["subscriptionType"] = subscriptionType,
            ["rateLimitTier"] = rateLimitTier,
        };

        if (includeScopes)
        {
            oauth["scopes"] = new JsonArray((scopes ?? DefaultScopes).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        var root = new JsonObject
        {
            ["claudeAiOauth"] = oauth,
            ["organizationUuid"] = "00000000-0000-4000-8000-000000000001",
        };
        return root.ToJsonString();
    }
}
