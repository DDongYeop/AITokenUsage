using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeCliCredentialSourceTests
{
    private const string CredentialsPath = @"C:\Users\test\.claude\.credentials.json";

    private readonly FakeFileSystem _files = new();

    private void Write(string json)
    {
        _files.Remove(CredentialsPath);
        _files.AddFile(CredentialsPath, json, ClaudeTestData.Now.UtcDateTime);
    }

    [Fact]
    public void Watches_the_credentials_file_and_never_renews()
    {
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);

        Assert.Equal(new[] { CredentialsPath }, source.WatchPaths);
        Assert.False(source.RenewsItself);
    }

    [Fact]
    public async Task Read_returns_file_credentials()
    {
        Write(ClaudeTestData.Credentials());

        ClaudeCredentialReadResult result = await new ClaudeCliCredentialSource(_files, CredentialsPath).ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(ClaudeTestData.TokenA, result.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Recover_returns_null_when_the_token_did_not_change()
    {
        Write(ClaudeTestData.Credentials());
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);
        ClaudeCredentialReadResult first = await source.ReadAsync(CancellationToken.None);

        Assert.Null(await source.RecoverAsync(first.Credentials!, CancellationToken.None));
    }

    [Fact]
    public async Task Recover_returns_the_changed_token()
    {
        Write(ClaudeTestData.Credentials());
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);
        ClaudeCredentialReadResult first = await source.ReadAsync(CancellationToken.None);
        Write(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(first.Credentials!, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.Ok, recovered.State);
        Assert.Equal(ClaudeTestData.TokenB, recovered.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Recover_passes_a_failed_state_through()
    {
        Write(ClaudeTestData.Credentials());
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);
        ClaudeCredentialReadResult first = await source.ReadAsync(CancellationToken.None);
        Write(ClaudeTestData.Credentials(accessToken: ""));

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(first.Credentials!, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.SignedOut, recovered.State);
    }
}

/// <summary>The login of an extra Claude account, renewed by Claude Code (spec 2026-11-16 §2.2–2.3).</summary>
public class ClaudeDelegatedCredentialSourceTests
{
    private const string AccountDirectory = @"C:\Users\test\AppData\Local\AITokenUsage\accounts\claude-abc123";
    private const string Token1 = "delegated-access-token-one";
    private const string Token2 = "delegated-access-token-two";
    private const string Token3 = "delegated-access-token-three";

    private static readonly string CredentialsPath = Path.Combine(AccountDirectory, ".credentials.json");
    private static readonly string AccountPath = Path.Combine(AccountDirectory, ".claude.json");
    private static readonly string[] AllTokens = [Token1, Token2, Token3];

    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeTimeProvider _time = new(ClaudeTestData.Now);
    private readonly RecordingLog _log = new();

    public ClaudeDelegatedCredentialSourceTests()
    {
        _processes.SetPath("claude", @"C:\Users\test\.local\bin\claude.exe");
        NeverRenew();
    }

    private ClaudeDelegatedCredentialSource CreateSource() =>
        new(AccountDirectory, _files, new ClaudeCliRenewer(_processes, _files, _time, _log), _time);

    private void Write(string accessToken, DateTimeOffset expiresAt)
    {
        _files.Remove(CredentialsPath);
        _files.AddFile(CredentialsPath, ClaudeTestData.Credentials(accessToken, expiresAt.ToUnixTimeMilliseconds()));
    }

    private void WriteAccount(string json)
    {
        _files.Remove(AccountPath);
        _files.AddFile(AccountPath, json);
    }

    private void NeverRenew() => _processes.OnRun(_ => new ProcessResult(1, "", "", false));

    /// <summary>The next "mcp get" writes <paramref name="accessToken"/>, valid for 8 h.</summary>
    private void RenewWith(string accessToken) => _processes.OnRun(spec =>
    {
        if (spec.Arguments.Contains("get"))
        {
            Write(accessToken, _time.GetUtcNow().AddHours(8));
        }

        return new ProcessResult(1, "", "", false);
    });

    private void AssertNoTokensLogged()
    {
        foreach (string token in AllTokens)
        {
            Assert.DoesNotContain(token, _log.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Watches_the_account_credentials_file_and_renews_itself()
    {
        ClaudeDelegatedCredentialSource source = CreateSource();

        Assert.Equal(new[] { CredentialsPath }, source.WatchPaths);
        Assert.True(source.RenewsItself);
        Assert.Null(source.Email);
    }

    [Fact]
    public async Task Valid_token_runs_no_command()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(5).AddSeconds(1));

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(Token1, result.Credentials!.AccessToken);
        Assert.Empty(_processes.Calls);
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(0)]
    [InlineData(300)]
    public async Task Expired_or_expiring_token_runs_the_command_and_reads_the_renewed_login(int secondsLeft)
    {
        Write(Token1, ClaudeTestData.Now.AddSeconds(secondsLeft));
        RenewWith(Token2);

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(Token2, result.Credentials!.AccessToken);
        Assert.Single(_processes.Calls);
        AssertNoTokensLogged();
    }

    [Fact]
    public async Task Near_expiry_with_the_command_failing_still_returns_the_unexpired_token()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(4));

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(Token1, result.Credentials!.AccessToken);
        Assert.Equal(2, _processes.Calls.Count);
    }

    [Fact]
    public async Task Expired_and_not_renewed_fails_and_the_third_failure_in_a_row_needs_sign_in()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(-1));
        ClaudeDelegatedCredentialSource source = CreateSource();

        var states = new List<ClaudeCredentialState>();
        for (int i = 0; i < 4; i++)
        {
            ClaudeCredentialReadResult result = await source.ReadAsync(CancellationToken.None);
            Assert.Null(result.Credentials);
            states.Add(result.State);
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(
            new[] { ClaudeCredentialState.RenewFailed, ClaudeCredentialState.RenewFailed, ClaudeCredentialState.ReauthRequired, ClaudeCredentialState.ReauthRequired },
            states);
        Assert.Equal(8, _processes.Calls.Count);
        AssertNoTokensLogged();
    }

    [Fact]
    public async Task A_renewal_resets_the_failure_count()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(-1));
        ClaudeDelegatedCredentialSource source = CreateSource();
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(ClaudeCredentialState.RenewFailed, (await source.ReadAsync(CancellationToken.None)).State);
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        RenewWith(Token2);
        Assert.Equal(Token2, (await source.ReadAsync(CancellationToken.None)).Credentials!.AccessToken);

        _time.Advance(TimeSpan.FromHours(9));
        NeverRenew();
        Assert.Equal(ClaudeCredentialState.RenewFailed, (await source.ReadAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task A_new_login_in_the_file_resets_the_failure_count()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(-1));
        ClaudeDelegatedCredentialSource source = CreateSource();
        for (int i = 0; i < 3; i++)
        {
            await source.ReadAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        Write(Token3, _time.GetUtcNow().AddHours(8));
        Assert.Equal(Token3, (await source.ReadAsync(CancellationToken.None)).Credentials!.AccessToken);

        _time.Advance(TimeSpan.FromHours(9));
        Assert.Equal(ClaudeCredentialState.RenewFailed, (await source.ReadAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task The_command_runs_at_most_once_per_two_minutes()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(-1));
        ClaudeDelegatedCredentialSource source = CreateSource();

        Assert.Equal(ClaudeCredentialState.RenewFailed, (await source.ReadAsync(CancellationToken.None)).State);
        Assert.Equal(2, _processes.Calls.Count);

        // Within the gap: the previous outcome, no command and no extra failure counted.
        for (int i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(39));
            Assert.Equal(ClaudeCredentialState.RenewFailed, (await source.ReadAsync(CancellationToken.None)).State);
        }

        Assert.Equal(2, _processes.Calls.Count);

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ClaudeCredentialState.RenewFailed, (await source.ReadAsync(CancellationToken.None)).State);
        Assert.Equal(4, _processes.Calls.Count);
    }

    [Fact]
    public async Task Within_the_gap_an_unexpired_token_is_still_used()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(4));
        ClaudeDelegatedCredentialSource source = CreateSource();
        await source.ReadAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(1));
        ClaudeCredentialReadResult result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(Token1, result.Credentials!.AccessToken);
        Assert.Equal(2, _processes.Calls.Count);
    }

    [Fact]
    public async Task Missing_file_needs_sign_in_without_a_command()
    {
        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.ReauthRequired, result.State);
        Assert.Null(result.Credentials);
        Assert.Empty(_processes.Calls);
    }

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":"","expiresAt":1793185200000}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"delegated-access-token-one","expiresAt":0}}""")]
    [InlineData("""{"mcpOAuth":{}}""")]
    public async Task No_token_needs_sign_in_without_a_command(string json)
    {
        _files.AddFile(CredentialsPath, json);

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.ReauthRequired, result.State);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Missing_profile_scope_is_passed_through()
    {
        _files.AddFile(CredentialsPath, ClaudeTestData.Credentials(Token1, ClaudeTestData.Now.AddMinutes(-1).ToUnixTimeMilliseconds(), ["user:inference"]));

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.MissingProfileScope, result.State);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Claude_not_on_path_counts_as_a_failed_renewal()
    {
        _processes.SetPath("claude", null);
        Write(Token1, ClaudeTestData.Now.AddMinutes(-1));

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.RenewFailed, result.State);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Concurrent_reads_run_one_command()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(-1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _processes.OnRun(_ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            Write(Token2, _time.GetUtcNow().AddHours(8));
            return new ProcessResult(1, "", "", false);
        });
        ClaudeDelegatedCredentialSource source = CreateSource();

        Task<ClaudeCredentialReadResult> first = Task.Run(() => source.ReadAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Task<ClaudeCredentialReadResult> second = Task.Run(() => source.ReadAsync(CancellationToken.None));

        // The first read has read the file twice (before and inside the lock); the second has now seen the expired token.
        Assert.True(SpinWait.SpinUntil(() => _files.ReadCount(CredentialsPath) >= 3, TimeSpan.FromSeconds(10)));
        release.Set();
        ClaudeCredentialReadResult[] results = await Task.WhenAll(first, second);

        Assert.Single(_processes.Calls);
        Assert.All(results, r => Assert.Equal(Token2, r.Credentials!.AccessToken));
    }

    [Fact]
    public async Task RunExclusive_waits_for_a_running_renewal()
    {
        Write(Token1, ClaudeTestData.Now.AddMinutes(-1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _processes.OnRun(_ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new ProcessResult(1, "", "", false);
        });
        ClaudeDelegatedCredentialSource source = CreateSource();
        Task<ClaudeCredentialReadResult> read = Task.Run(() => source.ReadAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        bool ran = false;
        Task exclusive = source.RunExclusiveAsync(() => ran = true, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(ran);

        release.Set();
        await exclusive;
        await read;
        Assert.True(ran);
    }

    [Fact]
    public async Task Recover_returns_the_changed_token_without_a_command()
    {
        Write(Token1, ClaudeTestData.Now.AddHours(8));
        ClaudeDelegatedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(CancellationToken.None);
        Write(Token2, ClaudeTestData.Now.AddHours(8));

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(read.Credentials!, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.Ok, recovered.State);
        Assert.Equal(Token2, recovered.Credentials!.AccessToken);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Recover_returns_null_when_the_token_did_not_change()
    {
        Write(Token1, ClaudeTestData.Now.AddHours(8));
        ClaudeDelegatedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(CancellationToken.None);

        Assert.Null(await source.RecoverAsync(read.Credentials!, CancellationToken.None));
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Recover_returns_null_when_the_file_is_gone()
    {
        Write(Token1, ClaudeTestData.Now.AddHours(8));
        ClaudeDelegatedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(CancellationToken.None);
        _files.Remove(CredentialsPath);

        Assert.Null(await source.RecoverAsync(read.Credentials!, CancellationToken.None));
    }

    [Fact]
    public async Task Email_comes_from_the_account_folder()
    {
        Write(Token1, ClaudeTestData.Now.AddHours(8));
        WriteAccount("""{"numStartups":3,"oauthAccount":{"accountUuid":"u","emailAddress":"me@example.com","organizationName":"Example Org"}}""");
        ClaudeDelegatedCredentialSource source = CreateSource();

        await source.ReadAsync(CancellationToken.None);
        Assert.Equal("me@example.com", source.Email);

        // A half-written file keeps the last email; a file without the account clears it.
        WriteAccount("""{"numStartups":3,"oauthAcc""");
        await source.ReadAsync(CancellationToken.None);
        Assert.Equal("me@example.com", source.Email);

        WriteAccount("""{"numStartups":3}""");
        await source.ReadAsync(CancellationToken.None);
        Assert.Null(source.Email);
    }

    [Fact]
    public async Task Email_is_null_without_the_account_file()
    {
        Write(Token1, ClaudeTestData.Now.AddHours(8));
        ClaudeDelegatedCredentialSource source = CreateSource();

        await source.ReadAsync(CancellationToken.None);

        Assert.Null(source.Email);
    }
}
