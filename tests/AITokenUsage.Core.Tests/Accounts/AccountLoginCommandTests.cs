using AITokenUsage.Core.Accounts;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountLoginCommandTests
{
    [Fact]
    public void Build_Claude_ClearsSecureStorageDirAndPointsConfigDirAtTheFolder()
    {
        string? command = AccountLoginCommand.Build("claude", @"C:\Users\kim\AppData\Local\AITokenUsage\login\claude-k3x9q2");

        Assert.Equal(
            "set \"CLAUDE_SECURESTORAGE_CONFIG_DIR=\" && set \"CLAUDE_CONFIG_DIR=C:\\Users\\kim\\AppData\\Local\\AITokenUsage\\login\\claude-k3x9q2\" && claude auth login",
            command);
    }

    [Fact]
    public void Build_Codex_SetsCodexHome()
    {
        string? command = AccountLoginCommand.Build("codex", @"C:\Users\kim\AppData\Local\AITokenUsage\accounts\codex-a1b2c3");

        Assert.Equal(
            "set \"CODEX_HOME=C:\\Users\\kim\\AppData\\Local\\AITokenUsage\\accounts\\codex-a1b2c3\" && codex login",
            command);
    }

    [Fact]
    public void Build_PathWithSpaces_StaysInsideTheQuotes()
    {
        Assert.Equal(
            "set \"CODEX_HOME=C:\\Users\\Kim Min Su\\AppData\\Local\\AITokenUsage\\accounts\\codex-a1b2c3\" && codex login",
            AccountLoginCommand.Build("codex", @"C:\Users\Kim Min Su\AppData\Local\AITokenUsage\accounts\codex-a1b2c3"));
        Assert.Contains(
            "set \"CLAUDE_CONFIG_DIR=D:\\내 폴더\\login\\claude-k3x9q2\" &&",
            AccountLoginCommand.Build("claude", @"D:\내 폴더\login\claude-k3x9q2"));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("%")]
    [InlineData("^")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData(";")]
    public void Build_ForbiddenCharacterInPath_ReturnsNull(string bad)
    {
        Assert.Contains(bad, AccountLoginCommand.ForbiddenCharacters, StringComparison.Ordinal);
        Assert.Null(AccountLoginCommand.Build("claude", @"C:\Users\ki" + bad + @"m\login\claude-k3x9q2"));
        Assert.Null(AccountLoginCommand.Build("codex", @"C:\Users\ki" + bad + @"m\accounts\codex-a1b2c3"));
    }

    [Fact]
    public void ForbiddenCharacters_AreTheEightFromTheSpec()
    {
        Assert.Equal("\"%^&|<>;", AccountLoginCommand.ForbiddenCharacters);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("claude#k3x9q2")]
    [InlineData("")]
    public void Build_UnsupportedKind_ReturnsNull(string kind)
    {
        Assert.Null(AccountLoginCommand.Build(kind, @"C:\x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\a\nb")]
    public void Build_BlankOrMultilineDirectory_ReturnsNull(string directory)
    {
        Assert.Null(AccountLoginCommand.Build("claude", directory));
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => AccountLoginCommand.Build(null!, @"C:\x"));
        Assert.Throws<ArgumentNullException>(() => AccountLoginCommand.Build("claude", null!));
    }
}
