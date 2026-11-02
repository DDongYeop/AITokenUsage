using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Tests.Settings;

public class ProviderDefaultsTests
{
    [Theory]
    [InlineData("claude", "session", "weekly")]
    [InlineData("codex", "5h", "weekly")]
    [InlineData("gemini", "pro", "flash")]
    [InlineData("copilot", "monthly", "reset")]
    public void DefaultLines_FollowTheSpecTable(string id, string line1, string line2)
    {
        Assert.Equal((line1, line2), ProviderDefaults.DefaultLines(id));
    }

    [Fact]
    public void DefaultLines_ThrowsForUnknownProvider()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderDefaults.DefaultLines("antigravity"));
    }

    [Fact]
    public void CreateDefaultProviders_ReturnsAFreshEnabledListInCanonicalOrder()
    {
        List<ProviderSettings> first = ProviderDefaults.CreateDefaultProviders();
        first[0].Enabled = false;
        first.RemoveAt(3);

        List<ProviderSettings> second = ProviderDefaults.CreateDefaultProviders();

        Assert.Equal(["claude", "codex", "gemini", "copilot"], second.Select(p => p.Id));
        Assert.All(second, p => Assert.True(p.Enabled));
        Assert.Equal("session", second[0].Line1Key);
    }

    [Theory]
    [InlineData("claude#k3x9q2", "session", "weekly")]
    [InlineData("codex#a1b2c3", "5h", "weekly")]
    public void DefaultLines_AccountIdsUseTheirKindsLines(string id, string line1, string line2)
    {
        Assert.Equal((line1, line2), ProviderDefaults.DefaultLines(id));
    }

    [Fact]
    public void CreateDefault_AccountId_KeepsTheIdAndKindLines()
    {
        ProviderSettings settings = ProviderDefaults.CreateDefault("codex#a1b2c3");

        Assert.Equal("codex#a1b2c3", settings.Id);
        Assert.True(settings.Enabled);
        Assert.Equal(("5h", "weekly"), (settings.Line1Key, settings.Line2Key));
    }

    [Fact]
    public void RefreshChoices_Are2_5_10_15()
    {
        Assert.Equal([2, 5, 10, 15], ProviderDefaults.RefreshChoicesMinutes);
    }
}
