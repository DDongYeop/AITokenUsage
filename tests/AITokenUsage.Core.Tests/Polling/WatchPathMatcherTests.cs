using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

public class WatchPathMatcherTests
{
    [Theory]
    [InlineData(@"C:\Users\test\.claude\.credentials.json", @"C:\Users\test\.claude\.credentials.json", true)]
    [InlineData(@"c:\users\TEST\.CLAUDE\.credentials.json", @"C:\Users\test\.claude\.credentials.json", true)]
    [InlineData(@"C:\Users\test\.codex\auth.json", @"C:\Users\test\.codex", true)]
    [InlineData(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-1.jsonl", @"C:\Users\test\.codex\", true)]
    [InlineData(@"C:\Users\test\.codex", @"C:\Users\test\.codex\", true)]
    [InlineData("C:/Users/test/.gemini/oauth_creds.json", @"C:\Users\test\.gemini", true)]
    [InlineData(@"C:\Users\test\.codex-old\auth.json", @"C:\Users\test\.codex", false)]
    [InlineData(@"C:\Users\test\.claude.json", @"C:\Users\test\.claude", false)]
    [InlineData(@"C:\Users\test", @"C:\Users\test\.claude", false)]
    [InlineData(@"D:\Users\test\.claude\.credentials.json", @"C:\Users\test\.claude", false)]
    [InlineData("", @"C:\Users\test\.claude", false)]
    [InlineData(@"C:\Users\test\.claude\x", "", false)]
    public void Matches_EqualOrDescendant_CaseInsensitive(string fullPath, string watchPath, bool expected)
    {
        Assert.Equal(expected, WatchPathMatcher.Matches(fullPath, watchPath));
    }

    [Fact]
    public void Matches_NullArguments_ReturnFalse()
    {
        Assert.False(WatchPathMatcher.Matches(null, @"C:\a"));
        Assert.False(WatchPathMatcher.Matches(@"C:\a", null));
    }
}
