using AITokenUsage.Core.Providers.Codex;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexLoginDetectorTests
{
    private static CodexAuth Auth(CodexAuthMode mode = CodexAuthMode.ChatGpt, string? accessToken = "token-b")
        => new(mode, accessToken, "acct", false, null, null, null);

    [Fact]
    public void IsSignedIn_NoAuth_IsFalse() => Assert.False(CodexLoginDetector.IsSignedIn(null, null));

    [Fact]
    public void IsSignedIn_ChatGptWithTokenAndNoPrevious_IsTrue() => Assert.True(CodexLoginDetector.IsSignedIn(Auth(), null));

    [Fact]
    public void IsSignedIn_SameTokenAsBefore_IsFalse() => Assert.False(CodexLoginDetector.IsSignedIn(Auth(), "token-b"));

    [Fact]
    public void IsSignedIn_DifferentTokenFromBefore_IsTrue() => Assert.True(CodexLoginDetector.IsSignedIn(Auth(), "token-a"));

    [Theory]
    [InlineData(CodexAuthMode.ApiKey)]
    [InlineData(CodexAuthMode.Other)]
    public void IsSignedIn_NotChatGpt_IsFalse(CodexAuthMode mode) => Assert.False(CodexLoginDetector.IsSignedIn(Auth(mode), null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSignedIn_NoAccessToken_IsFalse(string? token) => Assert.False(CodexLoginDetector.IsSignedIn(Auth(accessToken: token), null));
}
