using AITokenUsage.Core.Accounts;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountLoginCommandTests
{
    [WindowsFact]
    public void Create_Claude_ClearsSecureStorageDirAndPointsConfigDirAtTheFolder()
    {
        const string Folder = @"C:\Users\kim\AppData\Local\AITokenUsage\login\claude-k3x9q2";

        AccountLoginCommand? command = AccountLoginCommand.Create("claude", Folder);

        Assert.NotNull(command);
        Assert.Equal("/c claude auth login || pause", command.Arguments);
        Assert.Equal(2, command.Environment.Count);
        Assert.True(command.Environment.ContainsKey("CLAUDE_SECURESTORAGE_CONFIG_DIR"));
        Assert.Null(command.Environment["CLAUDE_SECURESTORAGE_CONFIG_DIR"]);
        Assert.Equal(Folder, command.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [WindowsFact]
    public void Create_Codex_SetsCodexHome()
    {
        const string Folder = @"C:\Users\kim\AppData\Local\AITokenUsage\accounts\codex-a1b2c3";

        AccountLoginCommand? command = AccountLoginCommand.Create("codex", Folder);

        Assert.NotNull(command);
        Assert.Equal("/c codex login || pause", command.Arguments);
        Assert.Equal(Folder, Assert.Single(command.Environment, v => v.Key == "CODEX_HOME").Value);
        Assert.Single(command.Environment);
    }

    [WindowsFact]
    public void Create_TheFolderNeverGoesIntoTheArguments()
    {
        AccountLoginCommand? claude = AccountLoginCommand.Create("claude", @"C:\Users\kim\login\claude-k3x9q2");
        AccountLoginCommand? codex = AccountLoginCommand.Create("codex", @"C:\Users\kim\accounts\codex-a1b2c3");

        Assert.DoesNotContain("kim", claude!.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("kim", codex!.Arguments, StringComparison.Ordinal);
    }

    [WindowsTheory]
    [InlineData(@"C:\Users\Kim Min Su\AppData\Local\AITokenUsage\accounts\codex-a1b2c3")]
    [InlineData(@"D:\내 폴더\accounts\codex-a1b2c3")]
    [InlineData(@"C:\Users\a&b ""c"" 100%^|<>;\accounts\codex-a1b2c3")]
    public void Create_SpacesAndShellCharactersInPath_AreKeptExactly(string directory)
    {
        // The value goes through the process environment, never a shell, so nothing is quoted, trimmed or refused.
        Assert.Equal(directory, AccountLoginCommand.Create("codex", directory)!.Environment["CODEX_HOME"]);
        Assert.Equal(directory, AccountLoginCommand.Create("claude", directory)!.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [WindowsFact]
    public void Create_EnvironmentNamesIgnoreCase()
    {
        AccountLoginCommand? command = AccountLoginCommand.Create("codex", @"C:\x");

        Assert.Equal(@"C:\x", command!.Environment["codex_home"]);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("claude#k3x9q2")]
    [InlineData("")]
    public void Create_UnsupportedKind_ReturnsNull(string kind)
    {
        Assert.Null(AccountLoginCommand.Create(kind, @"C:\x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\a\nb")]
    [InlineData("C:\\a\0b")]
    [InlineData(@"login\claude-k3x9q2")]
    [InlineData(@"\login\claude-k3x9q2")]
    [InlineData(@"C:login\claude-k3x9q2")]
    public void Create_BlankControlOrNotFullyQualifiedDirectory_ReturnsNull(string directory)
    {
        Assert.Null(AccountLoginCommand.Create("claude", directory));
        Assert.Null(AccountLoginCommand.Create("codex", directory));
    }

    [Fact]
    public void Create_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => AccountLoginCommand.Create(null!, @"C:\x"));
        Assert.Throws<ArgumentNullException>(() => AccountLoginCommand.Create("claude", null!));
    }
}
