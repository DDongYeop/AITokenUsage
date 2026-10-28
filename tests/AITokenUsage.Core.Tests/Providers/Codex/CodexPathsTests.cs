using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexPathsTests
{
    [Fact]
    public void Resolve_UsesCodexHome_WhenDefined()
    {
        var env = new FakeEnvironment();
        env.Set("CODEX_HOME", @"D:\codex-home");

        var paths = CodexPaths.Resolve(env);

        Assert.Equal(@"D:\codex-home", paths.Home);
        Assert.Equal(@"D:\codex-home\auth.json", paths.AuthFile);
        Assert.Equal(@"D:\codex-home\config.toml", paths.ConfigFile);
        Assert.Equal(@"D:\codex-home\sessions", paths.SessionsDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToUserProfile_WhenCodexHomeMissingOrBlank(string? codexHome)
    {
        var env = new FakeEnvironment();
        env.Set("CODEX_HOME", codexHome);

        var paths = CodexPaths.Resolve(env);

        Assert.Equal(CodexTestData.Home, paths.Home);
        Assert.Equal(CodexTestData.AuthPath, paths.AuthFile);
    }

    [Theory]
    [InlineData("chatgpt_base_url = \"https://proxy.example.com/backend-api/\"", "https://proxy.example.com/backend-api/")]
    [InlineData("  chatgpt_base_url='http://localhost:8080'  # local", "http://localhost:8080")]
    [InlineData("model = \"gpt-5-codex\"\nchatgpt_base_url = \"https://chatgpt.com\"\n[profiles.work]\nmodel = \"o3\"", "https://chatgpt.com")]
    [InlineData("# chatgpt_base_url = \"https://commented.example.com\"", null)]
    [InlineData("[profiles.work]\nchatgpt_base_url = \"https://profile.example.com\"", null)]
    [InlineData("chatgpt_base_url = \"\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadConfiguredBaseUrl_ReadsOnlyTopLevelKey(string? toml, string? expected)
    {
        Assert.Equal(expected, CodexPaths.ReadConfiguredBaseUrl(toml?.Replace("\n", "\r\n")));
    }

    [Theory]
    [InlineData(null, "https://chatgpt.com/backend-api")]
    [InlineData("   ", "https://chatgpt.com/backend-api")]
    [InlineData("https://chatgpt.com", "https://chatgpt.com/backend-api")]
    [InlineData("https://chatgpt.com/", "https://chatgpt.com/backend-api")]
    [InlineData("https://chat.openai.com", "https://chat.openai.com/backend-api")]
    [InlineData("https://chatgpt.com/backend-api/", "https://chatgpt.com/backend-api")]
    [InlineData("https://proxy.example.com/backend-api/", "https://proxy.example.com/backend-api")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    public void NormalizeBaseUrl_FollowsCodexRules(string? configured, string expected)
    {
        Assert.Equal(expected, CodexPaths.NormalizeBaseUrl(configured));
    }

    [Theory]
    [InlineData("https://chatgpt.com/backend-api", "https://chatgpt.com/backend-api/wham/usage")]
    [InlineData("https://proxy.example.com/backend-api", "https://proxy.example.com/backend-api/wham/usage")]
    [InlineData("http://localhost:8080", "http://localhost:8080/api/codex/usage")]
    public void BuildUsageUrl_PicksWhamOnlyForBackendApi(string normalizedBase, string expected)
    {
        Assert.Equal(expected, CodexPaths.BuildUsageUrl(normalizedBase));
    }

    [Fact]
    public void ResolveUsageUrl_UsesDefault_WhenConfigMissing()
    {
        var paths = CodexPaths.Resolve(new FakeEnvironment());

        Assert.Equal(CodexTestData.DefaultUsageUrl, paths.ResolveUsageUrl(new FakeFileSystem()));
    }

    [Fact]
    public void ResolveUsageUrl_ReadsConfigToml()
    {
        var files = new FakeFileSystem();
        files.AddFile(CodexTestData.ConfigPath, "chatgpt_base_url = \"http://localhost:8080/\"\n", CodexTestData.FileTime);
        var paths = CodexPaths.Resolve(new FakeEnvironment());

        Assert.Equal("http://localhost:8080/api/codex/usage", paths.ResolveUsageUrl(files));
    }
}
