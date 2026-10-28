using System.Text;
using System.Text.Json.Nodes;

namespace AITokenUsage.Core.Tests.Providers.Codex;

/// <summary>Fake Codex credentials and paths for tests. Nothing here is a real secret.</summary>
internal static class CodexTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    public static readonly DateTime FileTime = new(2026, 10, 28, 2, 0, 0, DateTimeKind.Utc);

    public const string Home = @"C:\Users\test\.codex";
    public const string AuthPath = @"C:\Users\test\.codex\auth.json";
    public const string ConfigPath = @"C:\Users\test\.codex\config.toml";
    public const string SessionsDir = @"C:\Users\test\.codex\sessions";
    public const string DefaultUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public const string AccountId = "acct-test-0001";

    /// <summary>Unsigned fake JWT: base64url(header).base64url(payload).test-signature</summary>
    public static string Jwt(string payloadJson)
        => Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}") + "." + Base64Url(payloadJson) + ".test-signature";

    public static string AccessJwt(DateTimeOffset expiresAt)
        => Jwt($$"""{"exp":{{expiresAt.ToUnixTimeSeconds()}},"iat":{{expiresAt.AddHours(-1).ToUnixTimeSeconds()}},"scp":["openid"]}""");

    public static string IdJwt(string? accountId = AccountId, bool fedramp = false, string plan = "plus")
    {
        var auth = new JsonObject
        {
            ["chatgpt_plan_type"] = plan,
            ["chatgpt_user_id"] = "user-test-0001",
            ["chatgpt_account_is_fedramp"] = fedramp,
        };
        if (accountId is not null) auth["chatgpt_account_id"] = accountId;
        var payload = new JsonObject
        {
            ["email"] = "dev@example.com",
            ["https://api.openai.com/auth"] = auth,
            ["exp"] = Now.AddHours(1).ToUnixTimeSeconds(),
        };
        return Jwt(payload.ToJsonString());
    }

    /// <summary>auth.json in the shape Codex writes (research §1.3).</summary>
    public static string AuthJson(
        string? authMode = "chatgpt",
        string? accessToken = null,
        string? idToken = null,
        string? accountId = AccountId,
        string? lastRefresh = "2026-10-28T02:00:00.123456789Z",
        string? openAiApiKey = null,
        bool includeTokens = true)
    {
        var root = new JsonObject();
        if (authMode is not null) root["auth_mode"] = authMode;
        root["OPENAI_API_KEY"] = openAiApiKey;
        if (includeTokens)
        {
            root["tokens"] = new JsonObject
            {
                ["id_token"] = idToken ?? IdJwt(),
                ["access_token"] = accessToken ?? AccessJwt(Now.AddHours(1)),
                ["refresh_token"] = "test-refresh-token",
                ["account_id"] = accountId,
            };
        }
        if (lastRefresh is not null) root["last_refresh"] = lastRefresh;
        return root.ToJsonString();
    }

    private static string Base64Url(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
