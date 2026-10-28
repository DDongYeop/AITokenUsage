using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeCredentialReaderTests
{
    private const string CredPath = @"C:\Users\test\.claude\.credentials.json";

    private readonly List<TimeSpan> _delays = [];

    private ClaudeCredentialReader CreateReader(IFileSystem files) =>
        new(files, (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });

    private static FakeFileSystem FilesWith(string content)
    {
        var files = new FakeFileSystem();
        files.AddFile(CredPath, content, null);
        return files;
    }

    [Fact]
    public async Task Reads_valid_credentials_fixture()
    {
        var reader = CreateReader(FilesWith(Fixture.Read("claude/credentials.json")));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.NotNull(result.Credentials);
        Assert.Equal("test-access-token", result.Credentials.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_185_200_000), result.Credentials.ExpiresAt);
        Assert.Equal(ClaudeTestData.Now.AddHours(8), result.Credentials.ExpiresAt);
        Assert.Equal("team", result.Credentials.SubscriptionType);
        Assert.Equal("default_claude_max_5x", result.Credentials.RateLimitTier);
        Assert.NotNull(result.Credentials.Scopes);
        Assert.Contains("user:profile", result.Credentials.Scopes);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Missing_file_is_not_installed()
    {
        ClaudeCredentialReadResult result = await CreateReader(new FakeFileSystem()).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.NotInstalled, result.State);
        Assert.Null(result.Credentials);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_access_token_is_signed_out(string token)
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(accessToken: token)));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.SignedOut, result.State);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Zero_expires_at_is_signed_out()
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(expiresAtMs: 0)));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.SignedOut, result.State);
    }

    [Fact]
    public async Task Missing_claude_oauth_block_is_signed_out()
    {
        const string json = """{"mcpOAuth":{"test-server|0000":{"accessToken":"test-mcp-token"}},"organizationUuid":"00000000-0000-4000-8000-000000000001"}""";
        var reader = CreateReader(FilesWith(json));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.SignedOut, result.State);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Scopes_without_user_profile_cannot_read_usage()
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(scopes: ["user:inference"])));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.MissingProfileScope, result.State);
    }

    [Fact]
    public async Task Absent_scopes_array_is_allowed()
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(includeScopes: false)));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.NotNull(result.Credentials);
        Assert.Null(result.Credentials.Scopes);
    }

    [Fact]
    public async Task Parse_failure_is_retried_and_not_treated_as_logout()
    {
        string valid = ClaudeTestData.Credentials();
        var files = new SequenceFileSystem(() => valid[..20], () => valid);

        ClaudeCredentialReadResult result = await CreateReader(files).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(2, files.ReadCount);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50) }, _delays);
    }

    [Fact]
    public async Task Persistent_parse_failure_gives_up_after_three_retries_as_unreadable()
    {
        var files = new SequenceFileSystem(() => "");

        ClaudeCredentialReadResult result = await CreateReader(files).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Unreadable, result.State);
        Assert.Null(result.Credentials);
        Assert.Equal(4, files.ReadCount);
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300) },
            _delays);
    }

    [Fact]
    public async Task Io_error_while_reading_is_retried()
    {
        string valid = ClaudeTestData.Credentials();
        var files = new SequenceFileSystem(() => throw new IOException("The process cannot access the file."), () => valid);

        ClaudeCredentialReadResult result = await CreateReader(files).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(2, files.ReadCount);
    }

    [Fact]
    public void ToString_never_contains_the_access_token()
    {
        Assert.True(ClaudeCredentialReader.TryParse(ClaudeTestData.Credentials(), out ClaudeCredentialReadResult result));
        Assert.NotNull(result.Credentials);

        Assert.DoesNotContain(ClaudeTestData.TokenA, result.Credentials.ToString());
        Assert.DoesNotContain(ClaudeTestData.TokenA, result.ToString());
    }

    /// <summary>Returns the queued contents one per read; the last entry repeats.</summary>
    private sealed class SequenceFileSystem : IFileSystem
    {
        private readonly Queue<Func<string?>> _reads;

        public SequenceFileSystem(params Func<string?>[] reads)
        {
            _reads = new Queue<Func<string?>>(reads);
        }

        public int ReadCount { get; private set; }

        public bool FileExists(string path) => true;

        public bool DirectoryExists(string path) => true;

        public string? ReadAllTextShared(string path)
        {
            ReadCount++;
            Func<string?> next = _reads.Count > 1 ? _reads.Dequeue() : _reads.Peek();
            return next();
        }

        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
            throw new NotSupportedException();

        public DateTime GetLastWriteTimeUtc(string path) => throw new NotSupportedException();

        public IEnumerable<string> ReadLinesReverse(string path) => throw new NotSupportedException();
    }
}
