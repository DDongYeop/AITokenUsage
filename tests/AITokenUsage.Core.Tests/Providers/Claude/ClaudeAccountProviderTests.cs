using System.Net;
using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

/// <summary>ClaudeUsageProvider for an extra account: Claude Code's login in the account folder (spec 2026-11-16).</summary>
public class ClaudeAccountProviderTests
{
    private const string Reauth = "다시 로그인하세요 · 설정 → AI 연결";
    private const string Network = "네트워크 오류 — 잠시 후 다시 시도";

    private const string AccountId = "claude#abc123";
    private const string AccountsDirectory = @"C:\Users\test\AppData\Local\AITokenUsage\accounts";
    private const string Token1 = "account-access-token-one";
    private const string Token2 = "account-access-token-two";
    private const string Token3 = "account-access-token-three";

    private static readonly string AccountDirectory = Path.Combine(AccountsDirectory, "claude-abc123");
    private static readonly string CredentialsPath = Path.Combine(AccountDirectory, ".credentials.json");
    private static readonly string[] AllTokens = [Token1, Token2, Token3];

    private sealed record Sent(HttpMethod Method, string Url, string? Authorization);

    private sealed class Harness
    {
        public Harness()
        {
            Processes.SetPath("claude", @"C:\Users\test\.local\bin\claude.exe");
            Processes.OnRun(_ => new ProcessResult(1, "", "", false));
            Http.When(_ => true, request =>
            {
                Sent.Add(new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
                return Responses.Dequeue()();
            });
        }

        public FakeTimeProvider Time { get; } = new(ClaudeTestData.Now);

        public FakeEnvironment Env { get; } = new();

        public FakeFileSystem Files { get; } = new();

        public FakeProcessRunner Processes { get; } = new();

        public StubHttpHandler Http { get; } = new();

        public RecordingLog Log { get; } = new();

        public List<Sent> Sent { get; } = [];

        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();

        public ProviderContext Context => TestContexts.Create(Http, Time, Files, Env, Processes, log: Log);

        public void Write(string accessToken, DateTimeOffset expiresAt)
        {
            Files.Remove(CredentialsPath);
            Files.AddFile(CredentialsPath, ClaudeTestData.Credentials(accessToken, expiresAt.ToUnixTimeMilliseconds()));
        }

        /// <summary>"claude mcp get" writes <paramref name="accessToken"/>, valid for 8 h.</summary>
        public void RenewWith(string accessToken) => Processes.OnRun(spec =>
        {
            if (spec.Arguments.Contains("get"))
            {
                Write(accessToken, Time.GetUtcNow().AddHours(8));
            }

            return new ProcessResult(1, "", "", false);
        });

        public ClaudeUsageProvider CreateProvider() => new(
            Context,
            AccountId,
            new ClaudeDelegatedCredentialSource(AccountDirectory, Files, new ClaudeCliRenewer(Processes, Files, Time, Log), Time),
            new ClaudeVersionProbe(
                Env,
                _ => new[] { "anthropic.claude-code-2.1.400-win32-x64" },
                () => Array.Empty<string?>()));

        /// <summary>The widget never calls the token endpoint: every request is the usage request.</summary>
        public void AssertOnlyUsageRequests()
        {
            Assert.All(Sent, s =>
            {
                Assert.Equal(HttpMethod.Get, s.Method);
                Assert.Equal(ClaudeUsageProvider.UsageUrl, s.Url);
            });
        }

        public void AssertNoTokensLeaked(params UsageSnapshot[] snapshots)
        {
            foreach (string token in AllTokens)
            {
                Assert.DoesNotContain(token, Log.AllText, StringComparison.Ordinal);
                foreach (UsageSnapshot snapshot in snapshots)
                {
                    Assert.DoesNotContain(token, snapshot.StatusMessage ?? "", StringComparison.Ordinal);
                    Assert.DoesNotContain(token, snapshot.PlanLabel ?? "", StringComparison.Ordinal);
                }
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Usage() => Json(HttpStatusCode.OK, Fixture.Read("claude/usage-live.json"));

    [Fact]
    public async Task ForAccount_reads_the_login_in_the_account_folder()
    {
        var h = new Harness();
        h.Write(Token1, ClaudeTestData.Now.AddHours(8));
        h.Files.AddFile(Path.Combine(AccountDirectory, ".claude.json"), """{"oauthAccount":{"emailAddress":"me@example.com"}}""");
        h.Responses.Enqueue(Usage);
        ClaudeUsageProvider provider = ClaudeUsageProvider.ForAccount(h.Context, AccountId, AccountsDirectory);

        UsageSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(AccountId, provider.Id);
        Assert.Equal("Claude", provider.DisplayName);
        Assert.IsType<ClaudeDelegatedCredentialSource>(provider.Source);
        Assert.Equal(new[] { CredentialsPath }, provider.WatchPaths);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(AccountId, snapshot.ProviderId);
        Assert.Equal("me@example.com", provider.AccountEmail);
        Assert.Equal("Bearer " + Token1, Assert.Single(h.Sent).Authorization);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex#abc123")]
    [InlineData("claude#ABC123")]
    public void ForAccount_rejects_non_claude_account_ids(string id)
    {
        var h = new Harness();

        Assert.Throws<ArgumentException>(() => ClaudeUsageProvider.ForAccount(h.Context, id, AccountsDirectory));
    }

    [Fact]
    public void ForAccount_needs_the_accounts_folder()
    {
        var h = new Harness();

        Assert.ThrowsAny<ArgumentException>(() => ClaudeUsageProvider.ForAccount(h.Context, AccountId, ""));
    }

    [Fact]
    public void Default_provider_keeps_the_cli_source()
    {
        var h = new Harness();
        var provider = new ClaudeUsageProvider(h.Context);

        Assert.Equal(ProviderIds.Claude, provider.Id);
        Assert.IsType<ClaudeCliCredentialSource>(provider.Source);
        Assert.Null(provider.AccountEmail);
        Assert.Equal(provider.Source.WatchPaths, provider.WatchPaths);
    }

    [Fact]
    public async Task Valid_token_queries_usage_without_a_command()
    {
        var h = new Harness();
        h.Write(Token1, ClaudeTestData.Now.AddHours(8));
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Bearer " + Token1, Assert.Single(h.Sent).Authorization);
        Assert.Empty(h.Processes.Calls);
    }

    [Theory]
    [InlineData(299)]
    [InlineData(-3600)]
    public async Task Expiring_token_is_renewed_by_claude_code_before_the_usage_request(int secondsLeft)
    {
        var h = new Harness();
        h.Write(Token1, ClaudeTestData.Now.AddSeconds(secondsLeft));
        h.RenewWith(Token2);
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(h.Processes.Calls);
        Assert.Equal("Bearer " + Token2, Assert.Single(h.Sent).Authorization);
        h.AssertOnlyUsageRequests();
        h.AssertNoTokensLeaked(snapshot);
    }

    [Fact]
    public async Task Token_close_to_expiry_is_used_while_claude_code_does_not_renew_it()
    {
        var h = new Harness();
        h.Write(Token1, ClaudeTestData.Now.AddMinutes(4));
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Bearer " + Token1, Assert.Single(h.Sent).Authorization);
    }

    [Fact]
    public async Task Missing_login_needs_sign_in_without_http_or_a_command()
    {
        var h = new Harness();

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(Reauth, snapshot.StatusMessage);
        Assert.Empty(h.Sent);
        Assert.Empty(h.Processes.Calls);
    }

    [Fact]
    public async Task Expired_and_not_renewed_is_a_network_error_until_the_third_failure_asks_to_sign_in_again()
    {
        var h = new Harness();
        h.Write(Token1, ClaudeTestData.Now.AddMinutes(-1));
        ClaudeUsageProvider provider = h.CreateProvider();

        var snapshots = new List<UsageSnapshot>();
        for (int i = 0; i < 3; i++)
        {
            snapshots.Add(await provider.FetchAsync(CancellationToken.None));
            h.Time.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(new[] { ProviderStatus.Error, ProviderStatus.Error, ProviderStatus.SignedOut }, snapshots.Select(s => s.Status));
        Assert.Equal(new[] { Network, Network, Reauth }, snapshots.Select(s => s.StatusMessage));
        Assert.Empty(h.Sent);
        h.AssertNoTokensLeaked([.. snapshots]);
    }

    [Fact]
    public async Task New_sign_in_in_the_folder_is_used_by_the_next_fetch()
    {
        var h = new Harness();
        ClaudeUsageProvider provider = h.CreateProvider();
        Assert.Equal(ProviderStatus.SignedOut, (await provider.FetchAsync(CancellationToken.None)).Status);

        h.Write(Token3, ClaudeTestData.Now.AddHours(8));
        h.Files.AddFile(Path.Combine(AccountDirectory, ".claude.json"), """{"oauthAccount":{"emailAddress":"other@example.com"}}""");
        h.Responses.Enqueue(Usage);
        UsageSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Bearer " + Token3, Assert.Single(h.Sent).Authorization);
        Assert.Equal("other@example.com", provider.AccountEmail);
    }

    [Fact]
    public async Task Unauthorized_with_an_unchanged_file_holds_the_token_and_asks_to_sign_in_again()
    {
        var h = new Harness();
        h.Write(Token1, ClaudeTestData.Now.AddHours(8));
        h.Responses.Enqueue(() => Json(HttpStatusCode.Unauthorized, "{}"));
        ClaudeUsageProvider provider = h.CreateProvider();

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.SignedOut, first.Status);
        Assert.Equal(Reauth, first.StatusMessage);

        h.Time.Advance(TimeSpan.FromMinutes(59));
        UsageSnapshot held = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.SignedOut, held.Status);
        Assert.Single(h.Sent);
        Assert.Empty(h.Processes.Calls);
        h.AssertOnlyUsageRequests();
    }

    [Fact]
    public async Task Unauthorized_retries_with_a_token_claude_code_wrote_meanwhile()
    {
        var h = new Harness();
        h.Write(Token1, ClaudeTestData.Now.AddHours(8));
        h.Responses.Enqueue(() =>
        {
            h.Write(Token2, ClaudeTestData.Now.AddHours(8));
            return Json(HttpStatusCode.Unauthorized, "{}");
        });
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(new[] { "Bearer " + Token1, "Bearer " + Token2 }, h.Sent.Select(s => s.Authorization));
        Assert.Empty(h.Processes.Calls);
        h.AssertOnlyUsageRequests();
        h.AssertNoTokensLeaked(snapshot);
    }
}
