using AITokenUsage.Core.Providers.Claude;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeLoginImporterTests
{
    private const long ExpiresAtMs = 1_793_185_200_000;

    private static string Credentials(
        string accessToken = "imported-access",
        string refreshToken = "imported-refresh",
        string expiresAt = "1793185200000",
        string scopes = """["user:profile","user:inference"]""",
        string extra = ""","refreshTokenExpiresAt":1794241627830""") =>
        $$"""{"claudeAiOauth":{"accessToken":"{{accessToken}}","refreshToken":"{{refreshToken}}","expiresAt":{{expiresAt}},"scopes":{{scopes}},"subscriptionType":"team","rateLimitTier":"default_claude_max_5x"{{extra}}},"organizationUuid":"x"}""";

    private const string ClaudeJson =
        """{"numStartups":3,"oauthAccount":{"accountUuid":"u","emailAddress":"me@example.com","organizationName":"Example Org","displayName":"Me"}}""";

    [Fact]
    public void Complete_login_is_imported_with_email_and_organization()
    {
        ClaudeOwnedCredentials? login = ClaudeLoginImporter.TryRead(Credentials(), ClaudeJson);

        Assert.NotNull(login);
        Assert.Equal("imported-access", login.AccessToken);
        Assert.Equal("imported-refresh", login.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMs), login.ExpiresAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_794_241_627_830), login.RefreshTokenExpiresAt);
        Assert.Equal(new[] { "user:profile", "user:inference" }, login.Scopes);
        Assert.Equal("team", login.SubscriptionType);
        Assert.Equal("default_claude_max_5x", login.RateLimitTier);
        Assert.Equal("me@example.com", login.Email);
        Assert.Equal("Example Org", login.OrganizationName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("""{"numStartups":3}""")]
    [InlineData("""{"oauthAccount":"nope"}""")]
    public void Missing_or_bad_claude_json_leaves_email_empty(string? claudeJson)
    {
        ClaudeOwnedCredentials? login = ClaudeLoginImporter.TryRead(Credentials(), claudeJson);

        Assert.NotNull(login);
        Assert.Null(login.Email);
        Assert.Null(login.OrganizationName);
    }

    [Fact]
    public void Missing_refresh_expiry_is_null()
    {
        ClaudeOwnedCredentials? login = ClaudeLoginImporter.TryRead(Credentials(extra: ""), null);

        Assert.NotNull(login);
        Assert.Null(login.RefreshTokenExpiresAt);
    }

    [Theory]
    [InlineData("", "imported-refresh", "1793185200000", """["user:profile"]""")]
    [InlineData("imported-access", "", "1793185200000", """["user:profile"]""")]
    [InlineData("imported-access", "imported-refresh", "0", """["user:profile"]""")]
    [InlineData("imported-access", "imported-refresh", "-5", """["user:profile"]""")]
    [InlineData("imported-access", "imported-refresh", "null", """["user:profile"]""")]
    [InlineData("imported-access", "imported-refresh", "1793185200000", """["user:inference"]""")]
    [InlineData("imported-access", "imported-refresh", "1793185200000", "null")]
    public void Incomplete_login_is_not_done(string accessToken, string refreshToken, string expiresAt, string scopes)
    {
        Assert.Null(ClaudeLoginImporter.TryRead(Credentials(accessToken, refreshToken, expiresAt, scopes), ClaudeJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"claudeAiOauth\":{\"accessTo")]
    [InlineData("""{"mcpOAuth":{}}""")]
    [InlineData("[]")]
    public void Missing_or_partial_credentials_file_is_not_done(string? credentialsJson)
    {
        Assert.Null(ClaudeLoginImporter.TryRead(credentialsJson, ClaudeJson));
    }
}
