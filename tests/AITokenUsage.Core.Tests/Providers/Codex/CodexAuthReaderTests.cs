using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexAuthReaderTests
{
    private static readonly DateTimeOffset Now = CodexTestData.Now;

    [Fact]
    public void TryParse_CurrentChatGptFile_ReadsTokensClaimsAndTimestamps()
    {
        var auth = CodexAuthReader.TryParse(Fixture.Read("codex/auth-chatgpt.json"));

        Assert.NotNull(auth);
        Assert.Equal(CodexAuthMode.ChatGpt, auth.Mode); // OPENAI_API_KEY is set but auth_mode wins
        Assert.StartsWith("eyJ", auth.AccessToken);
        Assert.Equal("acct-fixture-0001", auth.AccountId);
        Assert.False(auth.IsFedramp);
        Assert.Equal("pro", auth.PlanTypeClaim);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793170800), auth.AccessTokenExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 2, 59, 0, TimeSpan.Zero), auth.LastRefresh!.Value, TimeSpan.FromSeconds(1));
        Assert.True(auth.IsFresh(Now));
    }

    [Fact]
    public void TryParse_LegacyFileWithoutAuthMode_UsesTokensAndJwtAccountId()
    {
        var auth = CodexAuthReader.TryParse(Fixture.Read("codex/auth-legacy.json"));

        Assert.NotNull(auth);
        Assert.Equal(CodexAuthMode.ChatGpt, auth.Mode);
        Assert.Equal("test-opaque-access-token", auth.AccessToken);
        Assert.Equal("acct-fixture-0001", auth.AccountId); // tokens.account_id is null → id_token claim
        Assert.Null(auth.AccessTokenExpiresAt);             // opaque token: no exp
        Assert.True(auth.IsFresh(Now));                     // last_refresh 3 days ago
    }

    [Theory]
    [InlineData("chatgpt", CodexAuthMode.ChatGpt)]
    [InlineData("chatgptAuthTokens", CodexAuthMode.ChatGpt)]
    [InlineData("apikey", CodexAuthMode.ApiKey)]
    [InlineData("bedrockApiKey", CodexAuthMode.ApiKey)]
    [InlineData("bedrockAccessKeys", CodexAuthMode.ApiKey)]
    [InlineData("personalAccessToken", CodexAuthMode.Other)]
    [InlineData("agentIdentity", CodexAuthMode.Other)]
    [InlineData("headers", CodexAuthMode.Other)]
    [InlineData("somethingNew", CodexAuthMode.Other)]
    public void TryParse_MapsExplicitAuthMode(string authMode, CodexAuthMode expected)
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(authMode: authMode, openAiApiKey: "sk-test-key"));

        Assert.Equal(expected, auth!.Mode);
    }

    [Theory]
    [InlineData("""{"OPENAI_API_KEY":"sk-test-key"}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"OPENAI_API_KEY":null}""", CodexAuthMode.ChatGpt)]
    [InlineData("""{"OPENAI_API_KEY":null,"personal_access_token":"test-pat"}""", CodexAuthMode.Other)]
    [InlineData("""{"OPENAI_API_KEY":null,"bedrock_api_key":{"key":"test"}}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"OPENAI_API_KEY":null,"bedrock_access_keys":{"id":"test"}}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"OPENAI_API_KEY":"sk-test-key","tokens":{"access_token":"test-access-token"}}""", CodexAuthMode.ChatGpt)]
    public void TryParse_ResolvesMissingAuthMode(string json, CodexAuthMode expected)
    {
        Assert.Equal(expected, CodexAuthReader.TryParse(json)!.Mode);
    }

    [Fact]
    public void TryParse_AccountId_FallsBackToTopLevelJwtClaim()
    {
        var idToken = CodexTestData.Jwt("""{"chatgpt_account_id":"acct-top-level","https://api.openai.com/auth":{"chatgpt_account_id":"acct-nested"}}""");

        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: idToken, accountId: null));

        Assert.Equal("acct-top-level", auth!.AccountId);
    }

    [Fact]
    public void TryParse_AccountId_FallsBackToFirstNonEmptyOrganization()
    {
        var idToken = CodexTestData.Jwt("""{"organizations":[{"id":""},{"id":"org-test-0002"},{"id":"org-test-0003"}]}""");

        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: idToken, accountId: null));

        Assert.Equal("org-test-0002", auth!.AccountId);
    }

    [Fact]
    public void TryParse_AccountId_FallsBackToAccessTokenClaims()
    {
        var idToken = CodexTestData.Jwt("""{"email":"dev@example.com"}""");
        var accessToken = CodexTestData.Jwt("""{"exp":1793170800,"https://api.openai.com/auth":{"chatgpt_account_id":"acct-from-access"}}""");

        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: idToken, accessToken: accessToken, accountId: null));

        Assert.Equal("acct-from-access", auth!.AccountId);
    }

    [Fact]
    public void TryParse_StoredAccountId_WinsOverClaims()
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: CodexTestData.IdJwt("acct-claim"), accountId: "acct-stored"));

        Assert.Equal("acct-stored", auth!.AccountId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryParse_ReadsFedrampClaimFromIdToken(bool fedramp)
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: CodexTestData.IdJwt(fedramp: fedramp)));

        Assert.Equal(fedramp, auth!.IsFedramp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"auth_mode\":\"chat")]
    [InlineData("[]")]
    [InlineData("null")]
    public void TryParse_ReturnsNull_ForEmptyPartialOrNonObject(string text)
    {
        Assert.Null(CodexAuthReader.TryParse(text));
    }

    [Theory]
    [InlineData(6 * 60, true)]   // exp = now + 6 min
    [InlineData(5 * 60, false)]  // exp = now + 5 min: inside the margin
    [InlineData(4 * 60, false)]
    [InlineData(-60, false)]
    public void IsFresh_UsesExpiryWithFiveMinuteMargin(int secondsUntilExpiry, bool expected)
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: CodexTestData.AccessJwt(Now.AddSeconds(secondsUntilExpiry))));

        Assert.Equal(expected, auth!.IsFresh(Now));
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(30, false)]
    public void IsFresh_WithoutExp_UsesLastRefreshWithinEightDays(int daysAgo, bool expected)
    {
        var lastRefresh = Now.AddDays(-daysAgo).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: "test-opaque-access-token", lastRefresh: lastRefresh));

        Assert.Equal(expected, auth!.IsFresh(Now));
    }

    [Fact]
    public void IsFresh_IsFalse_WithoutExpOrLastRefresh_OrWithoutAccessToken()
    {
        var noTimes = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: "test-opaque-access-token", lastRefresh: null));
        var noTokens = CodexAuthReader.TryParse(CodexTestData.AuthJson(includeTokens: false));

        Assert.False(noTimes!.IsFresh(Now));
        Assert.False(noTokens!.HasAccessToken);
        Assert.False(noTokens.IsFresh(Now));
    }

    [Fact]
    public void ToString_NeverContainsTokens()
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: "test-secret-access-token"))!;

        Assert.DoesNotContain("test-secret-access-token", auth.ToString());
    }

    [Fact]
    public async Task ReadAsync_ReturnsMissing_WhenFileDoesNotExist()
    {
        var reader = new CodexAuthReader(new FakeFileSystem(), (_, _) => Task.CompletedTask);

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Missing, result.Status);
        Assert.Null(result.Auth);
    }

    [Fact]
    public async Task ReadAsync_RetriesPartialFile_ThenSucceeds()
    {
        var files = new SequenceFileSystem("", "{\"auth_mode\":\"chatgpt\",\"tok", CodexTestData.AuthJson());
        var delays = new List<TimeSpan>();
        var reader = new CodexAuthReader(files, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Ok, result.Status);
        Assert.Equal(CodexAuthMode.ChatGpt, result.Auth!.Mode);
        Assert.Equal(3, files.Reads);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100) }, delays);
    }

    [Fact]
    public async Task ReadAsync_ReturnsUnreadable_AfterThreeRetries_NotSignedOut()
    {
        var files = new SequenceFileSystem("{", "{", "{", "{", "{");
        var delays = new List<TimeSpan>();
        var reader = new CodexAuthReader(files, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Unreadable, result.Status);
        Assert.Equal(4, files.Reads);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) }, delays);
    }

    [Fact]
    public async Task ReadAsync_TreatsIoExceptionAsTransient()
    {
        var files = new SequenceFileSystem(new IOException("sharing violation"), CodexTestData.AuthJson());
        var reader = new CodexAuthReader(files, (_, _) => Task.CompletedTask);

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Ok, result.Status);
        Assert.Equal(2, files.Reads);
    }

    /// <summary>Returns the queued contents (or throws the queued exception) on each read.</summary>
    private sealed class SequenceFileSystem(params object[] reads) : IFileSystem
    {
        private readonly Queue<object> _reads = new(reads);
        public int Reads { get; private set; }

        public string? ReadAllTextShared(string path)
        {
            Reads++;
            var next = _reads.Count > 1 ? _reads.Dequeue() : _reads.Peek();
            if (next is Exception ex) throw ex;
            return (string)next;
        }

        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) => [];
        public DateTime GetLastWriteTimeUtc(string path) => CodexTestData.FileTime;
        public IEnumerable<string> ReadLinesReverse(string path) => [];
    }
}
