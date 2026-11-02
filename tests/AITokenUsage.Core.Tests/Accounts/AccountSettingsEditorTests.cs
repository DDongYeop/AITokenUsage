using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountSettingsEditorTests
{
    private static List<ProviderSettings> List(params string[] ids) =>
        ids.Select(ProviderDefaults.CreateDefault).ToList();

    private static string[] Ids(IEnumerable<ProviderSettings> providers) => providers.Select(p => p.Id).ToArray();

    [Fact]
    public void Add_InsertsRightAfterTheLastEntryOfTheSameKind()
    {
        List<ProviderSettings> providers = List("claude", "claude#aaaaaa", "codex", "gemini", "copilot");

        AccountSettingsEditor.Add(providers, "claude#bbbbbb");
        AccountSettingsEditor.Add(providers, "codex#cccccc");

        Assert.Equal(
            ["claude", "claude#aaaaaa", "claude#bbbbbb", "codex", "codex#cccccc", "gemini", "copilot"],
            Ids(providers));
    }

    [Fact]
    public void Add_FollowsTheUsersOrder_NotTheCanonicalOrder()
    {
        List<ProviderSettings> providers = List("gemini", "codex", "copilot", "claude");

        AccountSettingsEditor.Add(providers, "codex#a1b2c3");

        Assert.Equal(["gemini", "codex", "codex#a1b2c3", "copilot", "claude"], Ids(providers));
    }

    [Fact]
    public void Add_AppendsWhenNoEntryOfTheKindExists()
    {
        List<ProviderSettings> providers = List("gemini", "copilot");

        AccountSettingsEditor.Add(providers, "claude#k3x9q2");

        Assert.Equal(["gemini", "copilot", "claude#k3x9q2"], Ids(providers));
    }

    [Fact]
    public void Add_UsesTheDefaultEntry()
    {
        List<ProviderSettings> providers = List("claude", "codex");

        ProviderSettings entry = AccountSettingsEditor.Add(providers, "codex#a1b2c3");

        Assert.Same(entry, providers[2]);
        Assert.True(entry.Enabled);
        Assert.Equal("5h", entry.Line1Key);
        Assert.Equal("weekly", entry.Line2Key);
    }

    [Fact]
    public void Add_KeepsAnExistingEntryInPlace()
    {
        List<ProviderSettings> providers = List("claude", "claude#k3x9q2", "codex");
        providers[1].Enabled = false;

        ProviderSettings entry = AccountSettingsEditor.Add(providers, "claude#k3x9q2");

        Assert.Same(providers[1], entry);
        Assert.False(entry.Enabled);
        Assert.Equal(["claude", "claude#k3x9q2", "codex"], Ids(providers));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("gemini#abcdef")]
    [InlineData("claude#ABCDEF")]
    public void Add_And_Remove_RejectNonAccountIds(string id)
    {
        List<ProviderSettings> providers = List("claude", "codex");

        Assert.Throws<ArgumentException>(() => AccountSettingsEditor.Add(providers, id));
        Assert.Throws<ArgumentException>(() => AccountSettingsEditor.Remove(providers, id));
        Assert.Equal(["claude", "codex"], Ids(providers));
    }

    [Fact]
    public void Remove_RemovesOnlyThatAccount()
    {
        List<ProviderSettings> providers = List("claude", "claude#aaaaaa", "claude#bbbbbb", "codex");

        Assert.True(AccountSettingsEditor.Remove(providers, "claude#aaaaaa"));
        Assert.False(AccountSettingsEditor.Remove(providers, "claude#aaaaaa"));

        Assert.Equal(["claude", "claude#bbbbbb", "codex"], Ids(providers));
    }

    [Fact]
    public void InsertIndex_IsTheEndForAnEmptyList() =>
        Assert.Equal(0, AccountSettingsEditor.InsertIndex([], "claude"));

    [Fact]
    public void NewAccountId_SkipsIdsAlreadyInUse()
    {
        // The first key drawn is "aaaaaa" (index 0), the second "bbbbbb" (index 1).
        int draw = 0;
        int NextInt(int max) => draw++ / AccountIds.KeyLength;

        string id = AccountSettingsEditor.NewAccountId("codex", ["claude", "codex#aaaaaa"], NextInt);

        Assert.Equal("codex#bbbbbb", id);
    }

    [Fact]
    public void NewAccountId_GivesUpWhenEveryDrawIsTaken() =>
        Assert.Throws<InvalidOperationException>(() =>
            AccountSettingsEditor.NewAccountId("claude", ["claude#aaaaaa"], _ => 0));

    [Fact]
    public void CountOfKind_CountsTheBuiltInPanelAndItsAccounts()
    {
        string[] ids = ["claude", "codex", "claude#aaaaaa", "gemini", "codex#bbbbbb", "claude#cccccc"];

        Assert.Equal(3, AccountSettingsEditor.CountOfKind(ids, "claude"));
        Assert.Equal(2, AccountSettingsEditor.CountOfKind(ids, "codex"));
        Assert.Equal(1, AccountSettingsEditor.CountOfKind(ids, "gemini"));
        Assert.Equal(0, AccountSettingsEditor.CountOfKind(ids, "copilot"));
    }
}
