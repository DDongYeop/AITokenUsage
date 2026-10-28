using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiUsageProviderTests
{
    // oauth_creds.json fixtures expire at 2026-10-28T04:00:00Z.
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _http = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly GeminiPaths _paths;

    private readonly List<string?> _loadTokens = [];
    private readonly List<string?> _quotaTokens = [];
    private readonly List<string> _loadBodies = [];
    private readonly List<string> _quotaBodies = [];
    private readonly List<string> _refreshBodies = [];

    public GeminiUsageProviderTests()
    {
        _paths = new GeminiPaths(_env);
        // The real OAuth client is located from the installed gemini-cli at runtime; tests use fake values.
        _env.Set(GeminiOAuthClientLocator.EnvClientId, "test-client-id.apps.googleusercontent.com");
        _env.Set(GeminiOAuthClientLocator.EnvClientSecret, "test-client-secret");
    }

    [Fact]
    public void Metadata_matches_spec()
    {
        var provider = CreateProvider();

        Assert.Equal("gemini", provider.Id);
        Assert.Equal("Gemini", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(2), provider.MinInterval);
        Assert.Equal([_paths.OAuthCredsPath, _paths.SettingsPath], provider.WatchPaths.ToArray());
        Assert.Equal(["pro", "flash", "flash-lite"], provider.StaticMeterOptions.Select(o => o.Key).ToArray());
    }

    [Fact]
    public async Task Missing_credentials_report_NotInstalled()
    {
        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Equal(GeminiMessages.NotInstalled, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Api_key_settings_report_Unsupported_without_network()
    {
        GivenCredsFile();
        _files.AddFile(_paths.SettingsPath, Fixture.Read("gemini/settings-api-key.json"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.ApiKeyMode, snapshot.StatusMessage);
        Assert.Empty(_loadTokens);
    }

    [Fact]
    public async Task Fresh_file_token_fetches_quota()
    {
        GivenCredsFile();
        _files.AddFile(_paths.SettingsPath, Fixture.Read("gemini/settings-oauth-jsonc.json"));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Standard", snapshot.PlanLabel);
        Assert.Equal(["pro", "flash", "flash-lite"], snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(40, snapshot.Meters[0].UsedPercent!.Value, 6);
        Assert.Equal(["test-access-token"], _loadTokens);
        Assert.Equal(["test-access-token"], _quotaTokens);
        Assert.Equal("""{"project":"fake-project-123"}""", Assert.Single(_quotaBodies));
        var loadBody = Assert.Single(_loadBodies);
        Assert.Contains("\"ideType\":\"IDE_UNSPECIFIED\"", loadBody);
        Assert.Contains("\"pluginType\":\"GEMINI\"", loadBody);
        Assert.Empty(_refreshBodies);
    }

    [Fact]
    public async Task Credential_manager_token_is_used_when_file_is_missing()
    {
        _credentials.Add(GeminiCredentialReader.CredentialTarget, "main-account", Fixture.Read("gemini/credman-main-account.json"));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["test-credman-access-token"], _quotaTokens);
    }

    [Fact]
    public async Task Expiring_token_is_refreshed_in_memory_and_reused()
    {
        GivenCredsFile();
        var originalFile = _files.ReadAllTextShared(_paths.OAuthCredsPath);
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));
        var provider = CreateProvider();

        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 3, 56, 0, TimeSpan.Zero)); // expiry - 4 min
        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10)); // file token is now expired, memory token is not
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        var refreshBody = Assert.Single(_refreshBodies);
        Assert.Contains("grant_type=refresh_token", refreshBody);
        Assert.Contains("refresh_token=test-refresh-token", refreshBody);
        Assert.Equal(["test-refreshed-access-token", "test-refreshed-access-token"], _quotaTokens);
        Assert.Equal(originalFile, _files.ReadAllTextShared(_paths.OAuthCredsPath)); // never written back
    }

    [Fact]
    public async Task Invalid_grant_reports_SignedOut()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.BadRequest, Fixture.Read("gemini/refresh-invalid-grant.json")));
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(GeminiMessages.SignInAgain, snapshot.StatusMessage);
        Assert.Empty(_loadTokens);
    }

    [Fact]
    public async Task Unauthorized_quota_refreshes_once_and_retries()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(
            Reply(HttpStatusCode.Unauthorized, """{"error":{"code":401}}"""),
            Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(_refreshBodies);
        Assert.Equal(["test-access-token", "test-refreshed-access-token"], _quotaTokens);
    }

    [Fact]
    public async Task Second_unauthorized_reports_SignedOut()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.Unauthorized, """{"error":{"code":401}}"""));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(GeminiMessages.SignInAgain, snapshot.StatusMessage);
        Assert.Single(_refreshBodies);
        Assert.Equal(2, _quotaTokens.Count);
    }

    [Fact]
    public async Task Consumer_shutdown_is_reported_without_quota_or_onboard_calls()
    {
        // StubHttpHandler throws on unmatched requests, so an onboardUser call would fail this test.
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-shutdown.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.ConsumerShutdown, snapshot.StatusMessage);
        Assert.Empty(_quotaTokens);
    }

    [Fact]
    public async Task Workspace_account_is_not_treated_as_shutdown()
    {
        GivenCredsFile("gemini/oauth_creds-workspace.json");
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-shutdown.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(GeminiMessages.NotOnboarded, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Current_tier_without_project_reports_ProjectRequired()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-no-project.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(GeminiMessages.ProjectRequired, snapshot.StatusMessage);
        Assert.Empty(_quotaTokens);
    }

    [Fact]
    public async Task Environment_project_is_used_when_response_has_none()
    {
        GivenCredsFile();
        _env.Set("GOOGLE_CLOUD_PROJECT", "env-project");
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-no-project.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("""{"project":"env-project"}""", Assert.Single(_quotaBodies));
    }

    [Fact]
    public async Task Dotenv_project_is_used_when_env_is_unset()
    {
        GivenCredsFile();
        _files.AddFile(_paths.GeminiDotEnvPath, "GOOGLE_CLOUD_PROJECT=dotenv-project\n");
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-no-project.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("""{"project":"dotenv-project"}""", Assert.Single(_quotaBodies));
    }

    [Fact]
    public async Task Subscription_required_after_unsupported_client_is_the_shutdown()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-free-unsupported-client.json")));
        GivenQuota(Reply(HttpStatusCode.Forbidden, Fixture.Read("gemini/quota-subscription-required.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.ConsumerShutdown, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Subscription_required_on_standard_tier_is_no_license()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.Forbidden, Fixture.Read("gemini/quota-subscription-required.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.NoLicense, snapshot.StatusMessage);
    }

    [Fact]
    public async Task LoadCodeAssist_is_cached_for_one_hour()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));
        var provider = CreateProvider();

        await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Single(_loadTokens);
        Assert.Equal(2, _quotaTokens.Count);

        _time.Advance(TimeSpan.FromMinutes(31));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, _loadTokens.Count);
    }

    [Fact]
    public async Task Rate_limited_quota_reports_retry_after()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(() =>
        {
            var response = Reply(HttpStatusCode.TooManyRequests, """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""")();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Corrupt_credentials_report_Error_not_SignedOut()
    {
        _files.AddFile(_paths.OAuthCredsPath, "{ \"access_token\": ");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(GeminiMessages.CredentialsUnreadable, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Network_failure_reports_Error()
    {
        GivenCredsFile();
        _http.When(r => r.RequestUri!.AbsoluteUri == GeminiUsageProvider.LoadCodeAssistUrl,
            _ => throw new HttpRequestException("connection refused"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(GeminiMessages.Network, snapshot.StatusMessage);
    }

    private GeminiUsageProvider CreateProvider() =>
        new(TestContexts.Create(_http, _time, _files, _env, creds: _credentials));

    private void GivenCredsFile(string fixture = "gemini/oauth_creds.json") =>
        _files.AddFile(_paths.OAuthCredsPath, Fixture.Read(fixture));

    private void GivenLoad(params Func<HttpResponseMessage>[] replies) =>
        Route(GeminiUsageProvider.LoadCodeAssistUrl, replies, r =>
        {
            _loadTokens.Add(r.Headers.Authorization?.Parameter);
            _loadBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        });

    private void GivenQuota(params Func<HttpResponseMessage>[] replies) =>
        Route(GeminiUsageProvider.RetrieveUserQuotaUrl, replies, r =>
        {
            _quotaTokens.Add(r.Headers.Authorization?.Parameter);
            _quotaBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        });

    private void GivenRefresh(params Func<HttpResponseMessage>[] replies) =>
        Route(GeminiTokenRefresher.TokenUrl, replies,
            r => _refreshBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));

    /// <summary>Each call takes the next reply; the last one repeats.</summary>
    private void Route(string url, Func<HttpResponseMessage>[] replies, Action<HttpRequestMessage> record)
    {
        var queue = new Queue<Func<HttpResponseMessage>>(replies);
        _http.When(r => r.RequestUri!.AbsoluteUri == url, r =>
        {
            record(r);
            return (queue.Count > 1 ? queue.Dequeue() : queue.Peek())();
        });
    }

    private static Func<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
