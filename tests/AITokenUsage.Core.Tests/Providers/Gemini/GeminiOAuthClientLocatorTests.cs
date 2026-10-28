using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiOAuthClientLocatorTests
{
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();

    private string NpmPackageRoot => Path.Combine(_env.RoamingAppData, "npm", "node_modules", "@google", "gemini-cli");

    [Fact]
    public void Environment_variables_win()
    {
        _env.Set(GeminiOAuthClientLocator.EnvClientId, "env-id.apps.googleusercontent.com");
        _env.Set(GeminiOAuthClientLocator.EnvClientSecret, "env-secret");

        var client = CreateLocator().Locate();

        Assert.Equal(new GeminiOAuthClient("env-id.apps.googleusercontent.com", "env-secret"), client);
    }

    [Fact]
    public void Reads_double_quoted_constants_from_the_npm_bundle()
    {
        _files.AddFile(Path.Combine(NpmPackageRoot, "bundle", "chunk-AAAA.js"), "export const unrelated = 1;");
        _files.AddFile(Path.Combine(NpmPackageRoot, "bundle", "chunk-BBBB.js"),
            "var OAUTH_CLIENT_ID = \"bundle-id.apps.googleusercontent.com\";\nvar OAUTH_CLIENT_SECRET = \"bundle-secret\";");

        var client = CreateLocator().Locate();

        Assert.Equal(new GeminiOAuthClient("bundle-id.apps.googleusercontent.com", "bundle-secret"), client);
    }

    [Fact]
    public void Finds_the_package_next_to_gemini_on_PATH()
    {
        var npmDir = @"D:\tools\npm";
        _processes.SetPath("gemini", Path.Combine(npmDir, "gemini.cmd"));
        _files.AddFile(
            Path.Combine(npmDir, "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"),
            "const OAUTH_CLIENT_ID = 'core-id.apps.googleusercontent.com';\nconst OAUTH_CLIENT_SECRET = 'core-secret';");

        var client = CreateLocator().Locate();

        Assert.Equal(new GeminiOAuthClient("core-id.apps.googleusercontent.com", "core-secret"), client);
    }

    [Fact]
    public void Returns_null_when_gemini_cli_is_not_installed()
    {
        Assert.Null(CreateLocator().Locate());
    }

    [Fact]
    public void ParseScript_needs_both_values()
    {
        Assert.Null(GeminiOAuthClientLocator.ParseScript("const OAUTH_CLIENT_ID = \"only-id.apps.googleusercontent.com\";"));
    }

    [Fact]
    public void ToString_hides_the_secret()
    {
        var client = new GeminiOAuthClient("id.apps.googleusercontent.com", "hidden-secret");

        Assert.DoesNotContain("hidden-secret", client.ToString());
    }

    private GeminiOAuthClientLocator CreateLocator() => new(_env, _files, _processes);
}
