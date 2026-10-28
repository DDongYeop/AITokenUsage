using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeVersionProbeTests
{
    // 2.1.99 sorts above 2.1.350 as text, so this list proves semantic comparison.
    private static readonly string[] ExtensionFolders =
    [
        @"C:\Users\test\.vscode\extensions\anthropic.claude-code-2.1.99-win32-x64",
        @"C:\Users\test\.vscode\extensions\anthropic.claude-code-2.1.350-win32-x64",
        "anthropic.claude-code-2.1.200",
        "ms-dotnettools.csharp-2.50.0-win32-x64",
        "anthropic.claude-code-latest",
    ];

    private static Func<IEnumerable<string?>> Processes(params string?[] versions) => () => versions;

    [Fact]
    public void Picks_highest_semantic_version_from_extension_folders()
    {
        var env = new FakeEnvironment();
        string? listedDirectory = null;
        var probe = new ClaudeVersionProbe(
            env,
            directory =>
            {
                listedDirectory = directory;
                return ExtensionFolders;
            },
            Processes());

        Assert.Equal("2.1.350", probe.GetVersion());
        Assert.Equal(Path.Combine(env.UserProfile, ".vscode", "extensions"), listedDirectory);
    }

    [Theory]
    [InlineData("Claude", "4.29.0", null)]
    [InlineData("Claude Desktop", "4.29.0.0", null)]
    [InlineData(null, "2.1.311.0", null)]
    [InlineData("Claude Code", "2.1.311.0", "2.1.311")]
    [InlineData("claude code", "2.1.311.0", "2.1.311")]
    [InlineData("Claude Code", "unknown", null)]
    [InlineData("Claude Code", null, null)]
    public void Only_claude_code_product_is_accepted_for_running_process(string? productName, string? fileVersion, string? expected)
    {
        Version? version = ClaudeVersionProbe.TryReadClaudeCodeVersion(productName, fileVersion);

        Assert.Equal(expected, version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}");
    }

    [Fact]
    public void Mixed_desktop_and_code_processes_yield_claude_code_version()
    {
        string?[] raw = new[] { ("Claude", "4.29.0.0"), ("Claude Code", "2.1.311.0") }
            .Select(p => ClaudeVersionProbe.TryReadClaudeCodeVersion(p.Item1, p.Item2))
            .Select(v => v is null ? null : $"{v.Major}.{v.Minor}.{v.Build}")
            .ToArray();
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes(raw));

        Assert.Equal("2.1.311", probe.GetVersion());
    }

    [Fact]
    public void Only_desktop_running_falls_through_to_extension_folders()
    {
        string?[] raw = new[] { ClaudeVersionProbe.TryReadClaudeCodeVersion("Claude", "4.29.0.0") }
            .Select(v => v is null ? null : $"{v.Major}.{v.Minor}.{v.Build}")
            .ToArray();
        var withFolders = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes(raw));
        var noFolders = new ClaudeVersionProbe(new FakeEnvironment(), _ => Array.Empty<string>(), Processes(raw));

        Assert.Equal("2.1.350", withFolders.GetVersion());
        Assert.Equal(ClaudeVersionProbe.FallbackVersion, noFolders.GetVersion());
    }

    [Fact]
    public void Running_claude_exe_file_version_comes_first()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes("2.1.360.0"));

        Assert.Equal("2.1.360", probe.GetVersion());
    }

    [Fact]
    public void Running_process_is_preferred_even_when_a_folder_is_newer()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes("2.1.200.0"));

        Assert.Equal("2.1.200", probe.GetVersion());
    }

    [Fact]
    public void Highest_running_process_version_is_used()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => Array.Empty<string>(), Processes("2.1.199.0", "2.1.311.0"));

        Assert.Equal("2.1.311", probe.GetVersion());
    }

    [Fact]
    public void Unparseable_process_versions_fall_back_to_folders()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes(null, "", "unknown"));

        Assert.Equal("2.1.350", probe.GetVersion());
    }

    [Fact]
    public void Process_probe_failure_falls_back_to_folders()
    {
        var probe = new ClaudeVersionProbe(
            new FakeEnvironment(),
            _ => ExtensionFolders,
            () => throw new InvalidOperationException("Access is denied."));

        Assert.Equal("2.1.350", probe.GetVersion());
    }

    [Fact]
    public void Folder_listing_failure_falls_back_to_fixed_version()
    {
        var probe = new ClaudeVersionProbe(
            new FakeEnvironment(),
            _ => throw new UnauthorizedAccessException("Access is denied."),
            Processes());

        Assert.Equal(ClaudeVersionProbe.FallbackVersion, probe.GetVersion());
        Assert.Equal("2.1.311", ClaudeVersionProbe.FallbackVersion);
    }

    [Fact]
    public void No_source_returns_fallback()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => Array.Empty<string>(), Processes());

        Assert.Equal("2.1.311", probe.GetVersion());
    }

    [Theory]
    [InlineData("2.1.311", "2.1.311")]
    [InlineData("2.1.311.0", "2.1.311")]
    [InlineData("2.1.200 (Claude Code)", "2.1.200")]
    [InlineData("v2.2.0", "2.2.0")]
    [InlineData("2.1", null)]
    [InlineData("latest", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_leading_three_part_version(string? text, string? expected)
    {
        bool parsed = ClaudeVersionProbe.TryParseVersion(text, out Version? version);

        Assert.Equal(expected is not null, parsed);
        Assert.Equal(expected, version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}");
    }
}
