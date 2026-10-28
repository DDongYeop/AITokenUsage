using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiCredentialReaderTests
{
    private static readonly DateTimeOffset FixtureExpiry = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeCredentialStore _credentials = new();

    [Fact]
    public void Paths_default_to_user_profile()
    {
        var paths = new GeminiPaths(_env);

        Assert.Equal(Path.Combine(_env.UserProfile, ".gemini"), paths.GeminiDir);
        Assert.Equal(Path.Combine(_env.UserProfile, ".gemini", "oauth_creds.json"), paths.OAuthCredsPath);
        Assert.Equal(Path.Combine(_env.UserProfile, ".gemini", "settings.json"), paths.SettingsPath);
    }

    [Fact]
    public void Paths_treat_GEMINI_CLI_HOME_as_the_home_directory()
    {
        _env.Set("GEMINI_CLI_HOME", @"D:\alt-home");

        var paths = new GeminiPaths(_env);

        Assert.Equal(@"D:\alt-home\.gemini\oauth_creds.json", paths.OAuthCredsPath);
        Assert.Equal(@"D:\alt-home\.gemini\settings.json", paths.SettingsPath);
        Assert.Equal(@"D:\alt-home\.gemini\.env", paths.GeminiDotEnvPath);
        Assert.Equal(@"D:\alt-home\.env", paths.HomeDotEnvPath);
    }

    [Fact]
    public void Paths_ignore_empty_GEMINI_CLI_HOME()
    {
        _env.Set("GEMINI_CLI_HOME", "");

        var paths = new GeminiPaths(_env);

        Assert.Equal(_env.UserProfile, paths.Home);
    }

    [Fact]
    public void AuthType_reads_jsonc_settings_with_comments_and_trailing_commas()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.SettingsPath, Fixture.Read("gemini/settings-oauth-jsonc.json"));

        var authType = GeminiAuthType.Read(_files, paths.SettingsPath);

        Assert.Equal("oauth-personal", authType);
        Assert.False(GeminiAuthType.IsUnsupported(authType));
    }

    [Theory]
    [InlineData("gemini/settings-api-key.json", "gemini-api-key")]
    [InlineData("gemini/settings-legacy-vertex.json", "vertex-ai")]
    public void AuthType_detects_api_key_and_vertex_modes(string fixture, string expected)
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.SettingsPath, Fixture.Read(fixture));

        var authType = GeminiAuthType.Read(_files, paths.SettingsPath);

        Assert.Equal(expected, authType);
        Assert.True(GeminiAuthType.IsUnsupported(authType));
    }

    [Theory]
    [InlineData("gemini-api-key", true)]
    [InlineData("api-key", true)]
    [InlineData("vertex-ai", true)]
    [InlineData("gateway", true)]
    [InlineData("oauth-personal", false)]
    [InlineData("cloud-shell", false)]
    [InlineData(null, false)]
    public void IsUnsupported_matches_the_spec_list(string? authType, bool expected)
    {
        Assert.Equal(expected, GeminiAuthType.IsUnsupported(authType));
    }

    [Fact]
    public void AuthType_is_null_when_settings_are_missing_or_broken()
    {
        var paths = new GeminiPaths(_env);
        Assert.Null(GeminiAuthType.Read(_files, paths.SettingsPath));

        _files.AddFile(paths.SettingsPath, "{ \"security\": ");
        Assert.Null(GeminiAuthType.Read(_files, paths.SettingsPath));
    }

    [Fact]
    public async Task Reads_oauth_creds_file()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.OAuthCredsPath, Fixture.Read("gemini/oauth_creds.json"));

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Found, result.Status);
        var creds = Assert.IsType<GeminiCredentials>(result.Credentials);
        Assert.Equal("test-access-token", creds.AccessToken);
        Assert.Equal("test-refresh-token", creds.RefreshToken);
        Assert.Equal(FixtureExpiry, creds.Expiry);
        Assert.NotNull(creds.IdToken);
        Assert.Equal(GeminiCredentialSource.OAuthFile, creds.Source);
    }

    [Fact]
    public async Task Falls_back_to_credential_manager_json()
    {
        var paths = new GeminiPaths(_env);
        _credentials.Add(GeminiCredentialReader.CredentialTarget, "main-account", Fixture.Read("gemini/credman-main-account.json"));

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Found, result.Status);
        var creds = Assert.IsType<GeminiCredentials>(result.Credentials);
        Assert.Equal("test-credman-access-token", creds.AccessToken);
        Assert.Equal("test-credman-refresh-token", creds.RefreshToken);
        Assert.Equal(FixtureExpiry, creds.Expiry);
        Assert.Null(creds.IdToken);
        Assert.Equal(GeminiCredentialSource.CredentialManager, creds.Source);
    }

    [Fact]
    public async Task Returns_NotFound_when_no_source_exists()
    {
        var result = await CreateReader(new GeminiPaths(_env)).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.NotFound, result.Status);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public async Task Corrupt_file_is_Unreadable_not_NotFound()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.OAuthCredsPath, "{ \"access_token\": \"test-acc");

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Unreadable, result.Status);
    }

    [Fact]
    public async Task Corrupt_file_still_falls_back_to_credential_manager()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.OAuthCredsPath, "not json");
        _credentials.Add(GeminiCredentialReader.CredentialTarget, "main-account", Fixture.Read("gemini/credman-main-account.json"));

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Found, result.Status);
        Assert.Equal(GeminiCredentialSource.CredentialManager, result.Credentials!.Source);
    }

    [Fact]
    public void ToString_never_contains_tokens()
    {
        var creds = GeminiCredentialReader.ParseOAuthFile(Fixture.Read("gemini/oauth_creds.json"))!;

        var text = creds.ToString();

        Assert.DoesNotContain("test-access-token", text);
        Assert.DoesNotContain("test-refresh-token", text);
    }

    private GeminiCredentialReader CreateReader(GeminiPaths paths) => new(_files, _credentials, paths);
}
