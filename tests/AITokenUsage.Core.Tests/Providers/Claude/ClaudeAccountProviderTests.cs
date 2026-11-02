using System.Net;
using System.Text;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static AITokenUsage.Core.Tests.Providers.Claude.ClaudeOwnedTestData;

namespace AITokenUsage.Core.Tests.Providers.Claude;

/// <summary>ClaudeUsageProvider with the widget-owned login of an extra account (spec §2.3–2.4).</summary>
public class ClaudeAccountProviderTests
{
    private const string Reauth = "다시 로그인하세요 · 설정 → AI 연결";
    private const string Network = "네트워크 오류 — 잠시 후 다시 시도";

    private sealed record Sent(HttpMethod Method, string Url, string? Authorization, string UserAgent, string? StoredAccessToken);

    private sealed class Harness
    {
        public Harness()
        {
            Http.When(_ => true, request =>
            {
                Sent.Add(new Sent(
                    request.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.UserAgent.ToString(),
                    ClaudeOwnedCredentials.FromJson(Secrets.Load(SecretName))?.AccessToken));
                return Responses.Dequeue()();
            });
        }

        public FakeTimeProvider Time { get; } = new(ClaudeTestData.Now);

        public FakeEnvironment Env { get; } = new();

        public StubHttpHandler Http { get; } = new();

        public FakeSecretStore Secrets { get; } = new();

        public RecordingLog Log { get; } = new();

        public List<Sent> Sent { get; } = [];

        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();

        public void Store(ClaudeOwnedCredentials login) => Secrets.Save(SecretName, login.ToJson());

        public ClaudeOwnedCredentials? Stored() => ClaudeOwnedCredentials.FromJson(Secrets.Load(SecretName));

        public ClaudeUsageProvider CreateProvider()
        {
            var ctx = TestContexts.Create(Http, Time, env: Env, secrets: Secrets, log: Log);
            var source = new ClaudeOwnedCredentialSource(
                Secrets,
                SecretName,
                new ClaudeTokenRenewer(ctx.Http, Time, Log),
                Time,
                Log);
            return new ClaudeUsageProvider(
                ctx,
                AccountId,
                source,
                new ClaudeVersionProbe(
                    Env,
                    _ => new[] { "anthropic.claude-code-2.1.400-win32-x64" },
                    () => Array.Empty<string?>()));
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

    private static HttpResponseMessage Renewed(string accessToken = AccessToken2, string refreshToken = RefreshToken2) =>
        Json(HttpStatusCode.OK, TokenResponse(accessToken, refreshToken));

    [Fact]
    public void Exposes_account_id_and_no_watch_paths()
    {
        var h = new Harness();
        ClaudeUsageProvider provider = h.CreateProvider();

        Assert.Equal(AccountId, provider.Id);
        Assert.Equal("Claude", provider.DisplayName);
        Assert.Empty(provider.WatchPaths);
        Assert.IsType<ClaudeOwnedCredentialSource>(provider.Source);
        Assert.Null(provider.AccountEmail);
    }

    [Fact]
    public async Task ForAccount_uses_the_account_secret()
    {
        var h = new Harness();
        h.Store(Login());
        h.Responses.Enqueue(Usage);
        ClaudeUsageProvider provider = ClaudeUsageProvider.ForAccount(TestContexts.Create(h.Http, h.Time, env: h.Env, secrets: h.Secrets), AccountId);

        UsageSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(AccountId, provider.Id);
        Assert.Equal(AccountId, snapshot.ProviderId);
        Assert.Empty(provider.WatchPaths);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("me@example.com", provider.AccountEmail);
        Assert.Equal("Bearer " + AccessToken1, Assert.Single(h.Sent).Authorization);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex#abc123")]
    [InlineData("claude#ABC123")]
    public void ForAccount_rejects_non_claude_account_ids(string id)
    {
        var h = new Harness();

        Assert.Throws<ArgumentException>(() => ClaudeUsageProvider.ForAccount(TestContexts.Create(h.Http, h.Time), id));
    }

    [Fact]
    public void Default_provider_keeps_the_cli_source()
    {
        var h = new Harness();
        var provider = new ClaudeUsageProvider(TestContexts.Create(h.Http, h.Time, env: h.Env));

        Assert.Equal(ProviderIds.Claude, provider.Id);
        Assert.IsType<ClaudeCliCredentialSource>(provider.Source);
        Assert.Null(provider.AccountEmail);
        Assert.Equal(provider.Source.WatchPaths, provider.WatchPaths);
    }

    [Fact]
    public async Task Valid_token_queries_usage_without_renewing()
    {
        var h = new Harness();
        h.Store(Login());
        h.Responses.Enqueue(Usage);
        ClaudeUsageProvider provider = h.CreateProvider();

        UsageSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(AccountId, snapshot.ProviderId);
        Assert.Equal("Max 20x", snapshot.PlanLabel);
        Sent sent = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("Bearer " + AccessToken1, sent.Authorization);
        Assert.Equal("me@example.com", provider.AccountEmail);
    }

    [Theory]
    [InlineData(299)]
    [InlineData(0)]
    [InlineData(-3600)]
    public async Task Expiring_token_is_renewed_and_saved_before_the_usage_request(int secondsLeft)
    {
        var h = new Harness();
        h.Store(Login(expiresAt: ClaudeTestData.Now.AddSeconds(secondsLeft)));
        h.Responses.Enqueue(() => Renewed());
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(HttpMethod.Post, h.Sent[0].Method);
        Assert.Equal(ClaudeTokenRenewer.TokenUrl, h.Sent[0].Url);
        Assert.Null(h.Sent[0].Authorization);
        Assert.Equal("claude-code/2.1.400", h.Sent[0].UserAgent);
        Assert.Equal(HttpMethod.Get, h.Sent[1].Method);
        Assert.Equal("Bearer " + AccessToken2, h.Sent[1].Authorization);
        Assert.Equal("claude-code/2.1.400", h.Sent[1].UserAgent);
        Assert.Equal(AccessToken2, h.Sent[1].StoredAccessToken);
        Assert.Equal(RefreshToken2, h.Stored()!.RefreshToken);
        Assert.Equal(ClaudeTestData.Now.AddSeconds(28800), h.Stored()!.ExpiresAt);
        h.AssertNoTokensLeaked(snapshot);
    }

    [Fact]
    public async Task Missing_login_needs_sign_in_without_http()
    {
        var h = new Harness();

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(Reauth, snapshot.StatusMessage);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Rejected_renewal_stays_signed_out_until_sign_in_again()
    {
        var h = new Harness();
        h.Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        h.Responses.Enqueue(() => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        ClaudeUsageProvider provider = h.CreateProvider();

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.SignedOut, first.Status);
        Assert.Equal(Reauth, first.StatusMessage);
        Assert.Single(h.Sent);
        Assert.Equal(AccessToken1, h.Stored()!.AccessToken);

        h.Time.Advance(TimeSpan.FromHours(2));
        UsageSnapshot second = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.SignedOut, second.Status);
        Assert.Single(h.Sent);

        var source = (ClaudeOwnedCredentialSource)provider.Source;
        await source.ReplaceAsync(Login(AccessToken3, RefreshToken3, h.Time.GetUtcNow().AddHours(8)), CancellationToken.None);
        h.Responses.Enqueue(Usage);

        UsageSnapshot afterSignIn = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, afterSignIn.Status);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("Bearer " + AccessToken3, h.Sent[1].Authorization);
        Assert.Equal(AccessToken3, h.Stored()!.AccessToken);
        h.AssertNoTokensLeaked(first, second, afterSignIn);
    }

    [Fact]
    public async Task Expired_refresh_token_needs_sign_in_without_http()
    {
        var h = new Harness();
        h.Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1), refreshTokenExpiresAt: ClaudeTestData.Now.AddMinutes(-1)));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(Reauth, snapshot.StatusMessage);
        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Temporary_renewal_failure_is_a_network_error_and_retries_next_poll(HttpStatusCode status)
    {
        var h = new Harness();
        h.Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        h.Responses.Enqueue(() => Json(status, "{}"));
        ClaudeUsageProvider provider = h.CreateProvider();

        UsageSnapshot failed = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Error, failed.Status);
        Assert.Equal(Network, failed.StatusMessage);
        Assert.Equal(AccessToken1, h.Stored()!.AccessToken);

        h.Responses.Enqueue(() => Renewed());
        h.Responses.Enqueue(Usage);
        UsageSnapshot next = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, next.Status);
        Assert.Equal(3, h.Sent.Count);
    }

    [Fact]
    public async Task Network_failure_while_renewing_is_a_network_error()
    {
        var h = new Harness();
        h.Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        h.Responses.Enqueue(() => throw new HttpRequestException("No connection could be made."));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(Network, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Unauthorized_forces_one_renewal_and_retries()
    {
        var h = new Harness();
        h.Store(Login());
        h.Responses.Enqueue(() => Json(HttpStatusCode.Unauthorized, "{}"));
        h.Responses.Enqueue(() => Renewed());
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Get }, h.Sent.Select(s => s.Method));
        Assert.Equal("Bearer " + AccessToken1, h.Sent[0].Authorization);
        Assert.Equal("Bearer " + AccessToken2, h.Sent[2].Authorization);
        Assert.Equal(AccessToken2, h.Sent[2].StoredAccessToken);
        h.AssertNoTokensLeaked(snapshot);
    }

    [Fact]
    public async Task Unauthorized_right_after_renewing_does_not_renew_again_and_holds()
    {
        var h = new Harness();
        h.Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        h.Responses.Enqueue(() => Renewed());
        h.Responses.Enqueue(() => Json(HttpStatusCode.Unauthorized, "{}"));
        ClaudeUsageProvider provider = h.CreateProvider();

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.SignedOut, first.Status);
        Assert.Equal(Reauth, first.StatusMessage);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, h.Sent.Select(s => s.Method));

        h.Time.Advance(TimeSpan.FromMinutes(59));
        UsageSnapshot held = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.SignedOut, held.Status);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Unauthorized_after_the_retry_holds_that_token()
    {
        var h = new Harness();
        h.Store(Login());
        h.Responses.Enqueue(() => Json(HttpStatusCode.Unauthorized, "{}"));
        h.Responses.Enqueue(() => Renewed());
        h.Responses.Enqueue(() => Json(HttpStatusCode.Unauthorized, "{}"));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(Reauth, snapshot.StatusMessage);
        Assert.Equal(3, h.Sent.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderStatus.SignedOut, Reauth)]
    [InlineData(HttpStatusCode.BadRequest, ProviderStatus.SignedOut, Reauth)]
    [InlineData(HttpStatusCode.BadGateway, ProviderStatus.Error, Network)]
    public async Task Unauthorized_then_failed_renewal_maps_the_state(HttpStatusCode renewStatus, ProviderStatus expected, string message)
    {
        var h = new Harness();
        h.Store(Login());
        h.Responses.Enqueue(() => Json(HttpStatusCode.Unauthorized, "{}"));
        h.Responses.Enqueue(() => Json(renewStatus, "{}"));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(expected, snapshot.Status);
        Assert.Equal(message, snapshot.StatusMessage);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Replace_is_used_by_the_next_fetch()
    {
        var h = new Harness();
        h.Store(Login());
        ClaudeUsageProvider provider = h.CreateProvider();
        h.Responses.Enqueue(Usage);
        await provider.FetchAsync(CancellationToken.None);

        await ((ClaudeOwnedCredentialSource)provider.Source).ReplaceAsync(
            Login(AccessToken3, RefreshToken3) with { Email = "other@example.com" }, CancellationToken.None);
        h.Responses.Enqueue(Usage);
        UsageSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Bearer " + AccessToken3, h.Sent[1].Authorization);
        Assert.Equal("other@example.com", provider.AccountEmail);
    }
}
