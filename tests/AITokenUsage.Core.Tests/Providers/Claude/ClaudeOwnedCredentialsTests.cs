using System.Text.Json;
using AITokenUsage.Core.Providers.Claude;
using Xunit;
using static AITokenUsage.Core.Tests.Providers.Claude.ClaudeOwnedTestData;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeOwnedCredentialsTests
{
    [Fact]
    public void ToJson_writes_design_keys_with_millisecond_times()
    {
        ClaudeOwnedCredentials login = Login(refreshTokenExpiresAt: ClaudeTestData.Now.AddDays(12));

        using JsonDocument doc = JsonDocument.Parse(login.ToJson());
        JsonElement root = doc.RootElement;

        Assert.Equal(
            new[] { "accessToken", "refreshToken", "expiresAt", "refreshTokenExpiresAt", "scopes", "subscriptionType", "rateLimitTier", "email", "organizationName" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(AccessToken1, root.GetProperty("accessToken").GetString());
        Assert.Equal(RefreshToken1, root.GetProperty("refreshToken").GetString());
        Assert.Equal(ClaudeTestData.Now.AddHours(8).ToUnixTimeMilliseconds(), root.GetProperty("expiresAt").GetInt64());
        Assert.Equal(ClaudeTestData.Now.AddDays(12).ToUnixTimeMilliseconds(), root.GetProperty("refreshTokenExpiresAt").GetInt64());
        Assert.Equal(Scopes, root.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("max", root.GetProperty("subscriptionType").GetString());
        Assert.Equal("default_claude_max_20x", root.GetProperty("rateLimitTier").GetString());
        Assert.Equal("me@example.com", root.GetProperty("email").GetString());
        Assert.Equal("Example Org", root.GetProperty("organizationName").GetString());
    }

    [Fact]
    public void ToJson_writes_null_for_missing_optional_values()
    {
        ClaudeOwnedCredentials login = Login() with { SubscriptionType = null, RateLimitTier = null, Email = null, OrganizationName = null };

        using JsonDocument doc = JsonDocument.Parse(login.ToJson());
        JsonElement root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("refreshTokenExpiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("subscriptionType").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("email").ValueKind);
    }

    [Fact]
    public void Json_round_trip_keeps_every_value()
    {
        ClaudeOwnedCredentials login = Login(refreshTokenExpiresAt: ClaudeTestData.Now.AddDays(12));

        ClaudeOwnedCredentials? back = ClaudeOwnedCredentials.FromJson(login.ToJson());

        Assert.NotNull(back);
        Assert.Equal(login.AccessToken, back.AccessToken);
        Assert.Equal(login.RefreshToken, back.RefreshToken);
        Assert.Equal(login.ExpiresAt, back.ExpiresAt);
        Assert.Equal(login.RefreshTokenExpiresAt, back.RefreshTokenExpiresAt);
        Assert.Equal(login.Scopes, back.Scopes);
        Assert.Equal(login.SubscriptionType, back.SubscriptionType);
        Assert.Equal(login.RateLimitTier, back.RateLimitTier);
        Assert.Equal(login.Email, back.Email);
        Assert.Equal(login.OrganizationName, back.OrganizationName);
    }

    [Fact]
    public void Json_round_trip_keeps_null_refresh_expiry()
    {
        ClaudeOwnedCredentials? back = ClaudeOwnedCredentials.FromJson(Login().ToJson());

        Assert.NotNull(back);
        Assert.Null(back.RefreshTokenExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"accessToken\":\"a\"")]
    [InlineData("""{"refreshToken":"r","expiresAt":1793185200000,"scopes":["user:profile"]}""")]
    [InlineData("""{"accessToken":"","refreshToken":"r","expiresAt":1793185200000,"scopes":["user:profile"]}""")]
    [InlineData("""{"accessToken":"a","expiresAt":1793185200000,"scopes":["user:profile"]}""")]
    [InlineData("""{"accessToken":"a","refreshToken":" ","expiresAt":1793185200000,"scopes":["user:profile"]}""")]
    [InlineData("""{"accessToken":"a","refreshToken":"r","scopes":["user:profile"]}""")]
    [InlineData("""{"accessToken":"a","refreshToken":"r","expiresAt":0,"scopes":["user:profile"]}""")]
    [InlineData("""{"accessToken":"a","refreshToken":"r","expiresAt":1793185200000,"scopes":["user:inference"]}""")]
    [InlineData("""{"accessToken":"a","refreshToken":"r","expiresAt":1793185200000}""")]
    public void FromJson_rejects_bad_or_incomplete_logins(string? json)
    {
        Assert.Null(ClaudeOwnedCredentials.FromJson(json));
    }

    [Fact]
    public void ToCredentials_copies_usage_values()
    {
        ClaudeOwnedCredentials login = Login();

        ClaudeCredentials credentials = login.ToCredentials();

        Assert.Equal(AccessToken1, credentials.AccessToken);
        Assert.Equal(login.ExpiresAt, credentials.ExpiresAt);
        Assert.Equal(Scopes, credentials.Scopes);
        Assert.Equal("max", credentials.SubscriptionType);
        Assert.Equal("default_claude_max_20x", credentials.RateLimitTier);
    }

    [Fact]
    public void ToString_omits_tokens()
    {
        string text = Login().ToString();

        Assert.DoesNotContain(AccessToken1, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken1, text, StringComparison.Ordinal);
        Assert.Contains("me@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Renew_result_ToString_omits_tokens()
    {
        string text = new ClaudeRenewResult(ClaudeRenewOutcome.Renewed, Login()).ToString();

        Assert.DoesNotContain(AccessToken1, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken1, text, StringComparison.Ordinal);
    }
}
