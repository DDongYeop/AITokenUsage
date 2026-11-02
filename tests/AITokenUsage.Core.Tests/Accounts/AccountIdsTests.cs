using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Tests.Accounts;

public class AccountIdsTests
{
    [Theory]
    [InlineData("claude#k3x9q2")]
    [InlineData("codex#a1b2c3")]
    [InlineData("claude#000000")]
    [InlineData("codex#zzzzzz")]
    public void IsAccountId_AcceptsKindHashAndSixLowercaseAlphanumerics(string id)
    {
        Assert.True(AccountIds.IsAccountId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini#k3x9q2")]
    [InlineData("copilot#k3x9q2")]
    [InlineData("claude#")]
    [InlineData("claude#k3x9q")]
    [InlineData("claude#k3x9q22")]
    [InlineData("claude#K3X9Q2")]
    [InlineData("Claude#k3x9q2")]
    [InlineData("claude#k3x9q_")]
    [InlineData("claude#k3x9q2\n")]
    [InlineData(" claude#k3x9q2")]
    [InlineData("#k3x9q2")]
    [InlineData("claude##k3x9q2")]
    public void IsAccountId_RejectsEverythingElse(string? id)
    {
        Assert.False(AccountIds.IsAccountId(id));
    }

    [Fact]
    public void SupportedKinds_AreClaudeAndCodex()
    {
        Assert.Equal(["claude", "codex"], AccountIds.SupportedKinds);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Create_ProducesAValidIdOfTheKind(string kind)
    {
        string id = AccountIds.Create(kind);

        Assert.True(AccountIds.IsAccountId(id));
        Assert.StartsWith(kind + "#", id, StringComparison.Ordinal);
        Assert.Equal(kind.Length + 1 + AccountIds.KeyLength, id.Length);
    }

    [Fact]
    public void Create_UsesTheInjectedRandomSourceOverThe36CharacterAlphabet()
    {
        var maxima = new List<int>();
        int next = 0;
        string id = AccountIds.Create("claude", max =>
        {
            maxima.Add(max);
            return next++ * 7 % max;
        });

        Assert.Equal(Enumerable.Repeat(36, AccountIds.KeyLength), maxima);
        // indices 0, 7, 14, 21, 28, 35 of "a-z0-9" give a h o v 2 9
        Assert.Equal("claude#aho" + "v29", id);
    }

    [Fact]
    public void Create_DefaultSourceGivesDifferentKeys()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => AccountIds.Create("codex")).ToHashSet();

        Assert.True(ids.Count > 1);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("claude#k3x9q2")]
    [InlineData("")]
    [InlineData("Claude")]
    public void Create_RejectsUnsupportedKinds(string kind)
    {
        Assert.Throws<ArgumentException>(() => AccountIds.Create(kind));
    }

    [Fact]
    public void Create_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AccountIds.Create(null!));
    }

    [Fact]
    public void KeyOf_ReturnsTheSixCharactersAfterTheSeparator()
    {
        Assert.Equal("k3x9q2", AccountIds.KeyOf("claude#k3x9q2"));
        Assert.Equal("a1b2c3", AccountIds.KeyOf("codex#a1b2c3"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("claude#short")]
    [InlineData("")]
    public void KeyOf_RejectsNonAccountIds(string id)
    {
        Assert.Throws<ArgumentException>(() => AccountIds.KeyOf(id));
    }

    [Theory]
    [InlineData("claude#k3x9q2", "claude")]
    [InlineData("codex#a1b2c3", "codex")]
    [InlineData("claude", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("gemini", "gemini")]
    [InlineData("system", "system")]
    [InlineData("", "")]
    public void KindOf_ReturnsTheTextBeforeTheSeparator(string id, string expected)
    {
        Assert.Equal(expected, ProviderIds.KindOf(id));
    }
}
