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

    [Fact]
    public void RefreshChoices_Are2_5_10_15()
    {
        Assert.Equal([2, 5, 10, 15], ProviderDefaults.RefreshChoicesMinutes);
    }
}
