using AITokenUsage.Core.Accounts;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountStorageTests
{
    [Fact]
    public void Names_UseTheKeyAfterTheSeparator()
    {
        Assert.Equal("claude-k3x9q2", AccountStorage.ClaudeHomeName("claude#k3x9q2"));
        Assert.Equal("codex-a1b2c3", AccountStorage.CodexHomeName("codex#a1b2c3"));
        Assert.Equal("claude-", AccountStorage.ClaudeHomePrefix);
        Assert.Equal("claude-account-", AccountStorage.ClaudeSecretPrefix);
        Assert.Equal("codex-", AccountStorage.CodexHomePrefix);
    }

    [Fact]
    public void ClaudeHomeOfAside_ReturnsTheAccountFolderOfAReplacedFolder()
    {
        Assert.Equal(".ready", AccountStorage.ReadySuffix);
        Assert.Equal(".old", AccountStorage.AsideSuffix);
        Assert.Equal("claude-k3x9q2", AccountStorage.ClaudeHomeOfAside("claude-k3x9q2.old"));
    }

    [Theory]
    [InlineData("claude-k3x9q2")]
    [InlineData("claude-k3x9q2.ready")]
    [InlineData("claude-K3X9Q2.old")]
    [InlineData("claude-abc.old")]
    [InlineData("codex-a1b2c3.old")]
    [InlineData("claude-account-abc123.old")]
    [InlineData(".old")]
    public void ClaudeHomeOfAside_IsNullForAnyOtherName(string name)
    {
        Assert.Null(AccountStorage.ClaudeHomeOfAside(name));
    }

    [Fact]
    public void Names_RejectNonAccountIds()
    {
        Assert.Throws<ArgumentException>(() => AccountStorage.ClaudeHomeName("claude"));
        Assert.Throws<ArgumentException>(() => AccountStorage.CodexHomeName("codex"));
    }

    [Fact]
    public void LegacyClaudeSecrets_ReturnsEveryPrefixedNameEvenWhenTheAccountIsInSettings()
    {
        string[] secrets = ["claude-account-k3x9q2", "claude-account-old111", "copilot-token", "codex-a1b2c3", "something"];

        Assert.Equal(["claude-account-k3x9q2", "claude-account-old111"], AccountStorage.LegacyClaudeSecrets(secrets));
    }

    [Fact]
    public void LegacyClaudeSecrets_ANameWithoutAValidKeyIsNeverReturned()
    {
        string[] secrets = ["claude-account-", "claude-account-abc", "claude-account-abcdefg", "claude-account-ABCDEF", "claude-account-ab.def", "claude-account-old111"];

        Assert.Equal(["claude-account-old111"], AccountStorage.LegacyClaudeSecrets(secrets));
    }

    [Fact]
    public void OrphanClaudeHomes_ReturnsPrefixedDirectoriesWhoseKeyIsNotInSettings()
    {
        string[] dirs = ["claude-k3x9q2", "claude-gone99", "codex-a1b2c3", "notes"];
        string[] settings = ["claude", "codex", "claude#k3x9q2", "codex#a1b2c3"];

        Assert.Equal(["claude-gone99"], AccountStorage.OrphanClaudeHomes(dirs, settings));
    }

    [Fact]
    public void OrphanClaudeHomes_AKeyOfAnotherKindDoesNotProtectTheDirectory()
    {
        Assert.Equal(["claude-a1b2c3"], AccountStorage.OrphanClaudeHomes(["claude-a1b2c3"], ["codex#a1b2c3"]));
    }

    [Fact]
    public void OrphanClaudeHomes_ANameWithoutAValidKeyIsNeverAnOrphan()
    {
        string[] dirs = ["claude-notes", "claude-", "claude-abc", "claude-abcdefg", "claude-ABCDEF", "claude-ab-def", "claude-account-abc123", "claude-gone99"];

        Assert.Equal(["claude-gone99"], AccountStorage.OrphanClaudeHomes(dirs, []));
    }

    [Fact]
    public void OrphanCodexHomes_ReturnsPrefixedDirectoriesWhoseKeyIsNotInSettings()
    {
        string[] dirs = ["codex-a1b2c3", "codex-gone99", "claude-k3x9q2", "notes"];
        string[] settings = ["claude#k3x9q2", "codex#a1b2c3"];

        Assert.Equal(["codex-gone99"], AccountStorage.OrphanCodexHomes(dirs, settings));
    }

    [Fact]
    public void OrphanCodexHomes_AKeyOfAnotherKindDoesNotProtectTheDirectory()
    {
        Assert.Equal(["codex-k3x9q2"], AccountStorage.OrphanCodexHomes(["codex-k3x9q2"], ["claude#k3x9q2"]));
    }

    [Fact]
    public void OrphanCodexHomes_ANameWithoutAValidKeyIsNeverAnOrphan()
    {
        string[] dirs = ["codex-notes", "codex-", "codex-abc", "codex-abcdefg", "codex-ABCDEF", "codex-ab-def", "codex-gone99"];

        Assert.Equal(["codex-gone99"], AccountStorage.OrphanCodexHomes(dirs, []));
    }

    [Fact]
    public void Orphans_IgnoreNonAccountSettingsIdsAndKeepInputOrder()
    {
        Assert.Equal(
            ["codex-zzzzzz", "codex-aaaaaa"],
            AccountStorage.OrphanCodexHomes(["codex-zzzzzz", "codex-aaaaaa"], ["codex", "gemini", "claude#aaaaaa"]));
    }

    [Fact]
    public void Orphans_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => AccountStorage.LegacyClaudeSecrets(null!));
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanClaudeHomes(null!, []));
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanClaudeHomes([], null!));
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanCodexHomes(null!, []));
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanCodexHomes([], null!));
    }
}
