using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class TerminalCommandLineTests
{
    [Fact]
    public void Build_WithWindowsTerminal_OpensNewTab()
    {
        (string fileName, IReadOnlyList<string> args) = TerminalCommandLine.Build(true, "codex login");

        Assert.Equal("wt.exe", fileName);
        Assert.Equal(new[] { "new-tab", "cmd", "/k", "codex login" }, args);
    }

    [Fact]
    public void Build_WithoutWindowsTerminal_UsesCmd()
    {
        (string fileName, IReadOnlyList<string> args) = TerminalCommandLine.Build(false, "codex login");

        Assert.Equal("cmd.exe", fileName);
        Assert.Equal(new[] { "/k", "codex login" }, args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_EmptyCommand_Throws(string? command)
    {
        Assert.ThrowsAny<ArgumentException>(() => TerminalCommandLine.Build(true, command!));
    }

    [Theory]
    [InlineData(true, "codex login", "new-tab cmd /k codex login")]
    [InlineData(false, "codex login", "/k codex login")]
    [InlineData(true, "claude", "new-tab cmd /k claude")]
    [InlineData(false, "gemini", "/k gemini")]
    public void JoinArguments_PassesCommandVerbatimAfterK(bool hasWindowsTerminal, string command, string expected)
    {
        (_, IReadOnlyList<string> args) = TerminalCommandLine.Build(hasWindowsTerminal, command);

        Assert.Equal(expected, TerminalCommandLine.JoinArguments(args));
    }

    [Fact]
    public void JoinArguments_QuotesLeadingArgumentsWithSpaces()
    {
        Assert.Equal(@"""new tab"" /k codex login", TerminalCommandLine.JoinArguments(new[] { "new tab", "/k", "codex login" }));
    }

    [Fact]
    public void JoinArguments_Empty_ReturnsEmpty()
    {
        Assert.Equal("", TerminalCommandLine.JoinArguments(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("cmd", "cmd")]
    [InlineData("/k", "/k")]
    [InlineData("", @"""""")]
    [InlineData("new tab", @"""new tab""")]
    [InlineData(@"C:\Program Files\", @"""C:\Program Files\\""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData(@"a\""b", @"""a\\\""b""")]
    public void QuoteArgument_QuotesOnlyWhenNeeded(string argument, string expected)
    {
        Assert.Equal(expected, TerminalCommandLine.QuoteArgument(argument));
    }
}
