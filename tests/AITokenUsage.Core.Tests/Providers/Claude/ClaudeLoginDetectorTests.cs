using AITokenUsage.Core.Providers.Claude;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeLoginDetectorTests
{
    private static string Credentials(
        string accessToken = "login-access",
        string refreshToken = "login-refresh",
        string expiresAt = "1793185200000",
        string scopes = """["user:profile","user:inference"]""") =>
        $$"""{"claudeAiOauth":{"accessToken":"{{accessToken}}","refreshToken":"{{refreshToken}}","expiresAt":{{expiresAt}},"scopes":{{scopes}},"subscriptionType":"team"},"organizationUuid":"x"}""";

    [Fact]
    public void Complete_login_is_signed_in()
    {
        Assert.True(ClaudeLoginDetector.IsSignedIn(Credentials()));
    }

    [Theory]
    [InlineData("", "login-refresh", "1793185200000", """["user:profile"]""")]
    [InlineData("login-access", "", "1793185200000", """["user:profile"]""")]
    [InlineData("login-access", "login-refresh", "0", """["user:profile"]""")]
    [InlineData("login-access", "login-refresh", "-5", """["user:profile"]""")]
    [InlineData("login-access", "login-refresh", "null", """["user:profile"]""")]
    [InlineData("login-access", "login-refresh", "1793185200000", """["user:inference"]""")]
    [InlineData("login-access", "login-refresh", "1793185200000", "null")]
    public void Incomplete_login_is_not_signed_in(string accessToken, string refreshToken, string expiresAt, string scopes)
    {
        Assert.False(ClaudeLoginDetector.IsSignedIn(Credentials(accessToken, refreshToken, expiresAt, scopes)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"claudeAiOauth\":{\"accessTo")]
    [InlineData("""{"mcpOAuth":{}}""")]
    [InlineData("[]")]
    public void Missing_or_partial_credentials_file_is_not_signed_in(string? credentialsJson)
    {
        Assert.False(ClaudeLoginDetector.IsSignedIn(credentialsJson));
    }
}
