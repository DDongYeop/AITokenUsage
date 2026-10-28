using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudePathsTests
{
    [Fact]
    public void Uses_user_profile_dot_claude_when_no_variable_is_set()
    {
        var env = new FakeEnvironment();

        Assert.Equal(Path.Combine(env.UserProfile, ".claude", ".credentials.json"), ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Uses_claude_config_dir_when_set()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");

        Assert.Equal(@"D:\cfg\.credentials.json", ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Ignores_empty_claude_config_dir()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", "");

        Assert.Equal(Path.Combine(env.UserProfile, ".claude", ".credentials.json"), ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Secure_storage_dir_wins_over_claude_config_dir()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");
        env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", @"E:\secure");

        Assert.Equal(@"E:\secure\.credentials.json", ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Defined_but_empty_secure_storage_dir_pins_default_even_with_claude_config_dir()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");
        env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", "");

        Assert.Equal(Path.Combine(env.UserProfile, ".claude", ".credentials.json"), ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Secure_storage_dir_alone_is_used()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", @"E:\secure");

        Assert.Equal(@"E:\secure", ClaudePaths.ResolveStoreDirectory(env));
    }

    [Fact]
    public void VsCode_extensions_directory_is_under_user_profile()
    {
        var env = new FakeEnvironment();

        Assert.Equal(Path.Combine(env.UserProfile, ".vscode", "extensions"), ClaudePaths.VsCodeExtensionsDirectory(env));
    }
}
