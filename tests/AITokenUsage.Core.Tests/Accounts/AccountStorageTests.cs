using AITokenUsage.Core.Accounts;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountStorageTests
{
    [Fact]
    public void Names_UseTheKeyAfterTheSeparator()
    {
        Assert.Equal("claude-account-k3x9q2", AccountStorage.ClaudeSecretName("claude#k3x9q2"));
        Assert.Equal("codex-a1b2c3", AccountStorage.CodexHomeName("codex#a1b2c3"));
        Assert.Equal("claude-account-", AccountStorage.ClaudeSecretPrefix);
        Assert.Equal("codex-", AccountStorage.CodexHomePrefix);
    }

    [Fact]
    public void Names_RejectNonAccountIds()
    {
        Assert.Throws<ArgumentException>(() => AccountStorage.ClaudeSecretName("claude"));
        Assert.Throws<ArgumentException>(() => AccountStorage.CodexHomeName("codex"));
    }

    [Fact]
    public void OrphanClaudeSecrets_ReturnsPrefixedNamesWhoseKeyIsNotInSettings()
    {
        string[] secrets = ["claude-account-k3x9q2", "claude-account-old111", "copilot-token", "codex-a1b2c3", "something"];
        string[] settings = ["claude", "codex", "claude#k3x9q2", "codex#a1b2c3"];

        Assert.Equal(["claude-account-old111"], AccountStorage.OrphanClaudeSecrets(secrets, settings));
    }

    [Fact]
    public void OrphanClaudeSecrets_AKeyOfAnotherKindDoesNotProtectTheSecret()
    {
        string[] secrets = ["claude-account-a1b2c3"];
        string[] settings = ["codex#a1b2c3"];

        Assert.Equal(["claude-account-a1b2c3"], AccountStorage.OrphanClaudeSecrets(secrets, settings));
    }

    [Fact]
    public void OrphanClaudeSecrets_NothingInSettings_EveryPrefixedNameIsOrphan()
    {
        Assert.Equal(
            ["claude-account-aaaaaa", "claude-account-bbbbbb"],
            AccountStorage.OrphanClaudeSecrets(["claude-account-aaaaaa", "other", "claude-account-bbbbbb"], []));
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
    public void OrphanClaudeSecrets_ANameWithoutAValidKeyIsNeverAnOrphan()
    {
        string[] secrets = ["claude-account-", "claude-account-abc", "claude-account-abcdefg", "claude-account-ABCDEF", "claude-account-ab.def", "claude-account-old111"];

        Assert.Equal(["claude-account-old111"], AccountStorage.OrphanClaudeSecrets(secrets, []));
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
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanClaudeSecrets(null!, []));
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanClaudeSecrets([], null!));
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanCodexHomes(null!, []));
        Assert.Throws<ArgumentNullException>(() => AccountStorage.OrphanCodexHomes([], null!));
    }
}
