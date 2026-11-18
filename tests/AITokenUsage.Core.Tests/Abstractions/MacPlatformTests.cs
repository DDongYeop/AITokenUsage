using System.Runtime.Versioning;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Abstractions;

/// <summary>A test of macOS/Linux behaviour: runs everywhere except Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "macOS/Linux only";
    }
}

public class MacPlatformTests
{
    private const string Login =
        """{"claudeAiOauth":{"accessToken":"sk-ant-oat-keychain","expiresAt":4102444800000,"scopes":["user:profile"],"subscriptionType":"max"}}""";

    [Fact]
    public void ClaudeKeychainService_IsPlainName_WithoutConfigDir()
    {
        var env = new FakeEnvironment("/Users/test");

        Assert.Equal("Claude Code-credentials", ClaudeKeychainCredentialSource.ResolveServiceName(env));
    }

    [Fact]
    public void ClaudeKeychainService_GetsHashSuffix_WithConfigDir()
    {
        var env = new FakeEnvironment("/Users/test").Set("CLAUDE_CONFIG_DIR", "/Users/test/.claude-work");

        string service = ClaudeKeychainCredentialSource.ResolveServiceName(env);

        Assert.Matches("^Claude Code-credentials-[0-9a-f]{8}$", service);
    }

    [Fact]
    public async Task ClaudeKeychainSource_ReadsTheKeychainItem()
    {
        var keychain = new FakeCredentialStore().Add("Claude Code-credentials", "test", Login);
        var source = new ClaudeKeychainCredentialSource(keychain, new FakeFileSystem(), new FakeEnvironment("/Users/test"));

        ClaudeCredentialReadResult result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal("sk-ant-oat-keychain", result.Credentials!.AccessToken);
        Assert.False(source.RenewsItself);
    }

    [Fact]
    public async Task ClaudeKeychainSource_FallsBackToTheCredentialsFile()
    {
        var env = new FakeEnvironment("/Users/test");
        var files = new FakeFileSystem().AddFile(ClaudePaths.ResolveCredentialsPath(env), Login);
        var source = new ClaudeKeychainCredentialSource(new FakeCredentialStore(), files, env);

        ClaudeCredentialReadResult result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
    }

    [Fact]
    public async Task ClaudeKeychainSource_NothingAnywhere_IsNotInstalled()
    {
        var source = new ClaudeKeychainCredentialSource(new FakeCredentialStore(), new FakeFileSystem(), new FakeEnvironment("/Users/test"));

        ClaudeCredentialReadResult result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.NotInstalled, result.State);
    }

    [UnixFact]
    public void CodexSpec_RunsTheShimDirectly()
    {
        ProcessSpec spec = CodexAppServerClient.BuildSpec("/opt/homebrew/bin/codex", "/tmp/codex-home");

        Assert.Equal("/opt/homebrew/bin/codex", spec.FileName);
        Assert.Equal(["-s", "read-only", "-a", "never", "app-server"], spec.Arguments);
        Assert.Equal("/tmp/codex-home", spec.EnvironmentOverrides!["CODEX_HOME"]);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void FindOnUnixPath_FindsExecutables_AndSkipsPlainFiles()
    {
        using var dir = new TempDirectory();
        string bin = dir.Combine("bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "tool"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(bin, "tool"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(bin, "data"), "not a program");

        Assert.Equal(Path.Combine(bin, "tool"), RealProcessRunner.FindOnUnixPath("tool", bin, dir.Path));
        Assert.Null(RealProcessRunner.FindOnUnixPath("data", bin, dir.Path));
    }

    [Fact]
    public void UnixSearchDirectories_AddsWellKnownFolders_AfterPath()
    {
        string[] dirs = RealProcessRunner.UnixSearchDirectories("/a:/b:/a", "/Users/test").ToArray();

        Assert.Equal("/a", dirs[0]);
        Assert.Equal("/b", dirs[1]);
        Assert.Contains("/opt/homebrew/bin", dirs);
        Assert.Contains(Path.Combine("/Users/test", ".local", "bin"), dirs);
        Assert.Single(dirs, d => d == "/a");
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void UnixFileSecretStore_RoundTrips_WithOwnerOnlyPermissions()
    {
        using var dir = new TempDirectory();
        var store = new UnixFileSecretStore(dir.Combine("secrets"));

        store.Save("copilot-token", "gho_secret");

        Assert.Equal("gho_secret", store.Load("copilot-token"));
        string file = Directory.GetFiles(dir.Combine("secrets")).Single();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        store.Delete("copilot-token");
        Assert.Null(store.Load("copilot-token"));
        store.Delete("copilot-token");
    }

    [Fact]
    public void UnixFileSecretStore_RejectsPathNames()
    {
        // Runs on Windows too: the name is rejected before any file call, so nothing Unix-only is reached.
#pragma warning disable CA1416
        var store = new UnixFileSecretStore("/tmp/x");

        Assert.Throws<ArgumentException>(() => store.Load("../escape"));
#pragma warning restore CA1416
    }
}
