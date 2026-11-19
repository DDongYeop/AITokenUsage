using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeUsageProviderTests
{
    private const string ClaudeExe = @"C:\fake-cli\claude.exe";

    private sealed record SentRequest(
        HttpMethod Method,
        string Url,
        string Query,
        string? Authorization,
        string? Beta,
        string UserAgent,
        string Accept);

    /// <summary>One provider world: fake clock, files, env and a scripted HTTP queue.</summary>
    private sealed class Harness
    {
        private bool _credentialsWritten;

        public Harness()
        {
            Http.When(_ => true, request =>
            {
                Sent.Add(new SentRequest(
                    request.Method,
                    request.RequestUri!.GetLeftPart(UriPartial.Path),
                    request.RequestUri.Query,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? beta) ? string.Join(",", beta) : null,
                    request.Headers.UserAgent.ToString(),
                    request.Headers.Accept.ToString()));
                return Responses.Dequeue()();
            });
        }

        public FakeTimeProvider Time { get; } = new(ClaudeTestData.Now);

        public FakeFileSystem Files { get; } = new();

        public FakeEnvironment Env { get; } = new();

        public FakeProcessRunner Processes { get; } = new();

        public StubHttpHandler Http { get; } = new();

        public List<SentRequest> Sent { get; } = [];

        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();

        public string CredentialsPath => Path.Combine(Env.UserProfile, ".claude", ".credentials.json");

        public void WriteCredentials(string json)
        {
            if (_credentialsWritten)
            {
                Files.Remove(CredentialsPath);
            }

            Files.AddFile(CredentialsPath, json, Time.GetUtcNow().UtcDateTime);
            _credentialsWritten = true;
        }

        public ProviderContext Context => TestContexts.Create(Http, Time, Files, Env, Processes);

        public ClaudeVersionProbe VersionProbe => new(
            Env,
            _ => new[] { "anthropic.claude-code-2.1.400-win32-x64" },
            () => Array.Empty<string?>());

        public ClaudeUsageProvider CreateProvider() => new(Context, VersionProbe);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Usage() => JsonResponse(HttpStatusCode.OK, Fixture.Read("claude/usage-live.json"));

    [Fact]
    public void Exposes_id_interval_watch_path_and_static_meters()
    {
        var h = new Harness();
        ClaudeUsageProvider provider = h.CreateProvider();

        Assert.Equal("claude", provider.Id);
        Assert.Equal("Claude", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(2), provider.MinInterval);
        Assert.Equal(new[] { h.CredentialsPath }, provider.WatchPaths);
        Assert.Equal(new[] { "session", "weekly" }, provider.StaticMeterOptions.Select(o => o.Key));
        Assert.Equal(new[] { "현재 세션 (5시간)", "주간 · 모든 모델" }, provider.StaticMeterOptions.Select(o => o.Label));
    }

    [WindowsFact]
    public void Watch_path_follows_secure_storage_override()
    {
        var h = new Harness();
        h.Env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");
        h.Env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", @"E:\secure");

        Assert.Equal(new[] { @"E:\secure\.credentials.json" }, h.CreateProvider().WatchPaths);
    }

    [Fact]
    public async Task Missing_credentials_file_is_not_installed_without_http()
    {
        var h = new Harness();

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Empty(snapshot.Meters);
        Assert.Empty(h.Http.Requests);
    }

    [Theory]
    [InlineData("", 1_793_185_200_000L)]
    [InlineData("test-access-token", 0L)]
    public async Task Empty_token_or_zero_expiry_is_signed_out_without_http(string token, long expiresAtMs)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(accessToken: token, expiresAtMs: expiresAtMs));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal("터미널에서 claude 실행 후 /login", snapshot.StatusMessage);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Missing_user_profile_scope_is_unsupported_without_http()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(scopes: ["user:inference"]));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal("이 로그인은 사용량 조회 불가", snapshot.StatusMessage);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Unparseable_credentials_are_an_error_not_a_logout()
    {
        var h = new Harness();
        h.WriteCredentials("{\"claudeAiOauth\": {\"accessTo");

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Empty(h.Http.Requests);
    }

    [Theory]
    [InlineData(299)]
    [InlineData(0)]
    [InlineData(-3600)]
    public async Task Unavailable_cli_leaves_expiring_token_waiting_without_http(int secondsLeft)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(
            expiresAtMs: ClaudeTestData.Now.AddSeconds(secondsLeft).ToUnixTimeMilliseconds()));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.WaitingForCliRefresh, snapshot.Status);
        Assert.Equal("Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다", snapshot.StatusMessage);
        Assert.Equal("Team · Max 5x", snapshot.PlanLabel);
        Assert.Empty(h.Processes.Calls);
        Assert.Empty(h.Http.Requests);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Token_with_more_than_five_minutes_left_is_used_without_running_claude()
    {
        var h = new Harness();
        h.Processes.SetPath("claude", ClaudeExe);
        h.WriteCredentials(ClaudeTestData.Credentials(expiresAtMs: ClaudeTestData.Now.AddSeconds(301).ToUnixTimeMilliseconds()));
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Empty(h.Processes.Calls);
        Assert.Equal("Bearer " + ClaudeTestData.TokenA, Assert.Single(h.Sent).Authorization);
    }

    [Theory]
    [InlineData(300, null, null)]
    [InlineData(299, null, null)]
    [InlineData(0, null, null)]
    [InlineData(-1, null, null)]
    [InlineData(299, "custom-config", null)]
    [InlineData(-1, "custom-config", "secure-store")]
    [InlineData(-1, "custom-config", "")]
    public async Task Primary_cli_renews_and_usage_reads_the_new_bearer_from_the_selected_store(
        int secondsLeft, string? config, string? secure)
    {
        var h = new Harness();
        string? configDir = config is null ? null : Path.Combine(h.Env.UserProfile, config);
        string? secureDir = string.IsNullOrEmpty(secure) ? secure : Path.Combine(h.Env.UserProfile, secure);
        h.Env.Set(ClaudePaths.ConfigDirVariable, configDir);
        h.Env.Set(ClaudePaths.SecureStorageDirVariable, secureDir);

        // A defined empty secure override pins the default store, even with a custom config.
        string? selectedDir = secureDir ?? configDir;
        string path = string.IsNullOrEmpty(selectedDir) ? h.CredentialsPath : Path.Combine(selectedDir, ".credentials.json");
        h.Files.AddFile(path, ClaudeTestData.Credentials(
            expiresAtMs: ClaudeTestData.Now.AddSeconds(secondsLeft).ToUnixTimeMilliseconds()));
        h.Processes.SetPath("claude", ClaudeExe).OnRun(_ =>
        {
            h.Files.AddFile(path, ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
            return new ProcessResult(1, "", "", false);
        });
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        ProcessSpec command = Assert.Single(h.Processes.Calls);
        Assert.Equal(ClaudeExe, command.FileName);
        Assert.Equal(new[] { "mcp", "get", ClaudeCliRenewer.ProbeServerName }, command.Arguments);
        var overrides = command.EnvironmentOverrides?.Keys ?? Enumerable.Empty<string>();
        Assert.DoesNotContain(ClaudePaths.ConfigDirVariable, overrides, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClaudePaths.SecureStorageDirVariable, overrides, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        AssertOnlyRefreshedUsageRequest(h);
    }

    [Fact]
    public async Task Primary_keychain_renewal_rereads_keychain_even_when_the_file_has_a_fresh_token()
    {
        var h = new Harness();
        var keychain = new FakeCredentialStore();
        keychain.Add(ClaudeKeychainCredentialSource.ServiceName, null,
            ClaudeTestData.Credentials(expiresAtMs: ClaudeTestData.Now.ToUnixTimeMilliseconds()));
        h.WriteCredentials(ClaudeTestData.Credentials(accessToken: "test-file-only-token"));
        var source = new ClaudeKeychainCredentialSource(keychain, h.Files, h.Env);
        var provider = new ClaudeUsageProvider(h.Context, ProviderIds.Claude, source, h.VersionProbe);
        h.Processes.SetPath("claude", ClaudeExe).OnRun(_ =>
        {
            keychain.Add(ClaudeKeychainCredentialSource.ServiceName, null,
                ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
            return new ProcessResult(1, "", "", false);
        });
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(h.Processes.Calls);
        AssertOnlyRefreshedUsageRequest(h);
    }

    [Theory]
    [InlineData(true, 299)]
    [InlineData(true, -1)]
    [InlineData(false, -1)]
    public async Task Unsuccessful_primary_renewal_waits_and_retries_at_two_minutes(bool cliAvailable, int secondsLeft)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(
            expiresAtMs: ClaudeTestData.Now.AddSeconds(secondsLeft).ToUnixTimeMilliseconds()));
        // A successful exit alone does not mean the credential was renewed.
        h.Processes.SetPath("claude", cliAvailable ? ClaudeExe : null)
            .OnRun(_ => new ProcessResult(0, "", "", false));
        ClaudeUsageProvider provider = h.CreateProvider();

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, first.Status);
        int firstAttemptCalls = h.Processes.Calls.Count;
        Assert.Equal(cliAvailable, firstAttemptCalls > 0);
        Assert.Empty(h.Sent);

        h.Processes.SetPath("claude", ClaudeExe).OnRun(_ =>
        {
            h.WriteCredentials(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
            return new ProcessResult(1, "", "", false);
        });
        h.Time.Advance(TimeSpan.FromSeconds(119));
        UsageSnapshot throttled = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, throttled.Status);
        Assert.Equal(firstAttemptCalls, h.Processes.Calls.Count);
        Assert.Empty(h.Sent);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Responses.Enqueue(Usage);
        UsageSnapshot retried = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, retried.Status);
        Assert.Equal(firstAttemptCalls + 1, h.Processes.Calls.Count);
        AssertOnlyRefreshedUsageRequest(h);
    }

    [Fact]
    public async Task Cancelling_primary_cli_renewal_propagates_without_another_command_or_http()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(expiresAtMs: ClaudeTestData.Now.ToUnixTimeMilliseconds()));
        using var cancellation = new CancellationTokenSource();
        h.Processes.SetPath("claude", ClaudeExe).OnRun(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.CreateProvider().FetchAsync(cancellation.Token));

        Assert.Single(h.Processes.Calls);
        Assert.Empty(h.Sent);
    }

    private static void AssertOnlyRefreshedUsageRequest(Harness h)
    {
        SentRequest request = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(ClaudeUsageProvider.UsageUrl, request.Url);
        Assert.Empty(request.Query);
        Assert.Equal("Bearer " + ClaudeTestData.TokenB, request.Authorization);
    }

    [Fact]
    public async Task Ok_response_maps_meters_and_plan_and_sends_expected_request()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("claude", snapshot.ProviderId);
        Assert.Equal("Team · Max 5x", snapshot.PlanLabel);
        Assert.Equal(ClaudeTestData.Now, snapshot.FetchedAt);
        Assert.Null(snapshot.StatusMessage);
        Assert.Null(snapshot.RetryAfter);
        Assert.Null(snapshot.ExtraLine);
        Assert.Equal(new[] { "session", "weekly", "weekly:fable" }, snapshot.Meters.Select(m => m.Key));
        Assert.Equal(14.0, snapshot.Meters[0].UsedPercent);

        SentRequest sent = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", sent.Url);
        Assert.Equal("", sent.Query);
        Assert.Equal("Bearer test-access-token", sent.Authorization);
        Assert.Equal("oauth-2025-04-20", sent.Beta);
        Assert.Equal("claude-code/2.1.400", sent.UserAgent);
        Assert.Equal("application/json", sent.Accept);
    }

    [Fact]
    public async Task Extra_usage_line_is_passed_through()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.OK, Fixture.Read("claude/usage-extra-enabled.json")));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal("추가 사용 $12.40 / $50.00", snapshot.ExtraLine);
    }

    [Fact]
    public async Task Ok_response_without_meters_reports_message()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.OK, """{"five_hour":null,"seven_day":null,"limits":[]}"""));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Empty(snapshot.Meters);
        Assert.Equal("서버가 보고한 한도 없음", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Unauthorized_with_unchanged_token_waits_and_holds_that_token_for_one_hour()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        ClaudeUsageProvider provider = h.CreateProvider();
        h.Responses.Enqueue(() => JsonResponse(
            HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid token"}}"""));

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, first.Status);
        Assert.Single(h.Sent);

        h.Time.Advance(TimeSpan.FromMinutes(59));
        UsageSnapshot held = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, held.Status);
        Assert.Single(h.Sent);

        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Responses.Enqueue(Usage);
        UsageSnapshot afterHold = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, afterHold.Status);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Unauthorized_then_changed_token_on_reread_retries_once_with_new_token()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() =>
        {
            // Claude Code refreshed the login while our request was in flight.
            h.WriteCredentials(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
            return JsonResponse(HttpStatusCode.Unauthorized, "{}");
        });
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("Bearer test-access-token", h.Sent[0].Authorization);
        Assert.Equal("Bearer test-access-token-2", h.Sent[1].Authorization);
    }

    [Fact]
    public async Task Unauthorized_retry_happens_only_once()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        ClaudeUsageProvider provider = h.CreateProvider();
        h.Responses.Enqueue(() =>
        {
            h.WriteCredentials(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
            return JsonResponse(HttpStatusCode.Unauthorized, "{}");
        });
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.Unauthorized, "{}"));

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, first.Status);
        Assert.Equal(2, h.Sent.Count);

        UsageSnapshot second = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, second.Status);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Forbidden_with_anthropic_error_is_unsupported_until_token_changes()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        ClaudeUsageProvider provider = h.CreateProvider();
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.Forbidden, Fixture.Read("claude/error-403-anthropic.json")));

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Unsupported, first.Status);
        Assert.Equal("이 로그인은 사용량 조회 불가", first.StatusMessage);

        h.Time.Advance(TimeSpan.FromMinutes(30));
        UsageSnapshot held = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Unsupported, held.Status);
        Assert.Single(h.Sent);

        h.WriteCredentials(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
        h.Responses.Enqueue(Usage);
        UsageSnapshot afterChange = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, afterChange.Status);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("Bearer test-access-token-2", h.Sent[1].Authorization);
    }

    [Theory]
    [InlineData("<html><body>403 Forbidden</body></html>")]
    [InlineData("error code: 1010")]
    [InlineData("")]
    [InlineData("""{"message":"forbidden"}""")]
    public async Task Forbidden_without_anthropic_error_is_rate_limited_for_five_minutes(string body)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        });

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.RetryAfter);
        Assert.Equal("Team · Max 5x", snapshot.PlanLabel);
    }

    [Theory]
    [InlineData(1200, 1200)]
    [InlineData(0, 300)]
    [InlineData(null, 300)]
    [InlineData(7200, 3600)]
    public async Task Too_many_requests_honors_retry_after(int? retryAfterSeconds, int expectedSeconds)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() =>
        {
            HttpResponseMessage response = JsonResponse(HttpStatusCode.TooManyRequests, Fixture.Read("claude/error-429.json"));
            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        });

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), snapshot.RetryAfter);
        Assert.Equal("요청 제한 — 잠시 후 다시 시도", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Too_many_requests_honors_retry_after_http_date()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() =>
        {
            HttpResponseMessage response = JsonResponse(HttpStatusCode.TooManyRequests, Fixture.Read("claude/error-429.json"));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(ClaudeTestData.Now.AddMinutes(12));
            return response;
        });

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(12), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Server_error_is_error_with_status_code()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.InternalServerError, "{}"));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("서버 오류 (HTTP 500)", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Network_failure_is_error()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => throw new HttpRequestException("No connection could be made."));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("네트워크 오류 — 잠시 후 다시 시도", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Unparseable_ok_body_is_error()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.OK, "<html>maintenance</html>"));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("사용량 응답 형식을 해석하지 못함", snapshot.StatusMessage);
    }
}
