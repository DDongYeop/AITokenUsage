using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class CopilotUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _http = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly FakeSecretStore _secrets = new();

    private readonly Dictionary<string, Func<HttpResponseMessage>> _repliesByToken = [];
    private readonly List<string?> _tokensTried = [];
    private int _ghRuns;
    private string? _userAgent;
    private string? _accept;
    private string? _apiVersion;
    private string? _authScheme;
    private string? _userRequestAuthorization;
    private string? _userRequestApiVersion;

    public CopilotUsageProviderTests()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.CopilotUserUrl, r =>
        {
            var token = r.Headers.Authorization?.Parameter;
            _tokensTried.Add(token);
            _authScheme = r.Headers.Authorization?.Scheme;
            _userAgent = r.Headers.UserAgent.ToString();
            _accept = r.Headers.Accept.ToString();
            _apiVersion = r.Headers.TryGetValues("X-GitHub-Api-Version", out var values) ? values.Single() : null;
            return token is not null && _repliesByToken.TryGetValue(token, out var reply)
                ? reply()
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
    }

    [Fact]
    public void Metadata_matches_spec()
    {
        var provider = CreateProvider();

        Assert.Equal("copilot", provider.Id);
        Assert.Equal("Copilot", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(5), provider.MinInterval);
        Assert.Empty(provider.WatchPaths);
        Assert.Equal(["monthly", "reset", "completions"], provider.StaticMeterOptions.Select(o => o.Key).ToArray());
        Assert.Null(provider.SignedInLogin);
    }

    [Fact]
    public async Task Fetch_sends_required_headers_and_parses_usage()
    {
        _env.Set("GH_TOKEN", "gho_env");
        _repliesByToken["gho_env"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Pro", snapshot.PlanLabel);
        Assert.Equal(["monthly", "reset"], snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal("Bearer", _authScheme);
        Assert.Equal(GitHubApi.UserAgent, _userAgent);
        Assert.False(string.IsNullOrWhiteSpace(_userAgent));
        Assert.Contains("application/json", _accept);
        Assert.Equal("2025-04-01", _apiVersion);
    }

    [Fact]
    public async Task Falls_through_rejected_tokens_and_remembers_the_working_source()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        _env.Set("GH_TOKEN", "gho_env");
        GivenGh("gho_cli");
        _repliesByToken["gho_saved"] = Reply(HttpStatusCode.Unauthorized, "{}");
        _repliesByToken["gho_env"] = Reply(HttpStatusCode.NotFound, "{}");
        _repliesByToken["gho_cli"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        Assert.Equal(["gho_saved", "gho_env", "gho_cli", "gho_cli"], _tokensTried);
        Assert.Equal(1, _ghRuns);
    }

    [Fact]
    public async Task Forbidden_without_rate_limit_headers_falls_through()
    {
        _env.Set("COPILOT_GITHUB_TOKEN", "gho_first");
        _env.Set("GH_TOKEN", "gho_second");
        _repliesByToken["gho_first"] = Reply(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");
        _repliesByToken["gho_second"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["gho_first", "gho_second"], _tokensTried);
    }

    [Fact]
    public async Task Remembered_token_that_starts_failing_restarts_the_search()
    {
        _env.Set("GH_TOKEN", "gho_env");
        _env.Set("GITHUB_TOKEN", "gho_backup");
        _repliesByToken["gho_env"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        _repliesByToken["gho_backup"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();
        await provider.FetchAsync(CancellationToken.None);

        _repliesByToken["gho_env"] = Reply(HttpStatusCode.Unauthorized, "{}");
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["gho_env", "gho_env", "gho_backup"], _tokensTried);
    }

    [Fact]
    public async Task No_candidates_report_SignedOut_without_network()
    {
        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(CopilotMessages.SignInRequired, snapshot.StatusMessage);
        Assert.Empty(_tokensTried);
    }

    [Fact]
    public async Task All_candidates_rejected_report_SignedOut()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _env.Set("GITHUB_TOKEN", "gho_b");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(["gho_a", "gho_b"], _tokensTried);
    }

    [Fact]
    public async Task Status_429_stops_the_search_and_reports_retry_after()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _env.Set("GITHUB_TOKEN", "gho_b");
        _repliesByToken["gho_a"] = () =>
        {
            var response = Reply(HttpStatusCode.TooManyRequests, "{}")();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        };

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), snapshot.RetryAfter);
        Assert.Equal(["gho_a"], _tokensTried);
    }

    [Fact]
    public async Task Forbidden_with_zero_remaining_is_rate_limited()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = () =>
        {
            var response = Reply(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""")();
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", Now.AddMinutes(10).ToUnixTimeSeconds().ToString());
            return response;
        };

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Any_error_with_retry_after_is_rate_limited()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = () =>
        {
            var response = Reply(HttpStatusCode.ServiceUnavailable, "{}")();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Server_error_reports_Error()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = Reply(HttpStatusCode.InternalServerError, "{}");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(CopilotMessages.ServerError(500), snapshot.StatusMessage);
    }

    [Fact]
    public async Task No_plan_reports_Unsupported()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = Reply(HttpStatusCode.OK, """{"login":"octocat","can_signup_for_limited":true}""");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(CopilotMessages.NoSubscription, snapshot.StatusMessage);
    }

    [Fact]
    public async Task SignIn_validates_saves_token_json_and_exposes_login()
    {
        GivenUserEndpoint();
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        var saved = CopilotSavedToken.TryParse(_secrets.Load(CopilotSavedToken.SecretName));
        Assert.NotNull(saved);
        Assert.Equal("ghu_device", saved!.Token);
        Assert.Equal("octocat-user", saved.Login); // from GET /user, not the usage response's "octocat"
        Assert.Equal("octocat-user", provider.SignedInLogin);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["ghu_device", "ghu_device"], _tokensTried);
        Assert.Equal("Bearer ghu_device", _userRequestAuthorization);
        Assert.Null(_userRequestApiVersion);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SignIn_falls_back_to_the_usage_login_when_user_lookup_returns_an_error(HttpStatusCode status)
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.UserUrl, _ => Reply(status, "{}")());
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);

        Assert.Equal("octocat", CopilotSavedToken.TryParse(_secrets.Load(CopilotSavedToken.SecretName))!.Login);
        Assert.Equal("octocat", provider.SignedInLogin);
    }

    [Fact]
    public async Task SignIn_falls_back_to_the_usage_login_when_user_lookup_throws()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.UserUrl, _ => throw new HttpRequestException("connection reset"));
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);

        Assert.Equal("octocat", CopilotSavedToken.TryParse(_secrets.Load(CopilotSavedToken.SecretName))!.Login);
        Assert.Equal("octocat", provider.SignedInLogin);
    }

    [Fact]
    public async Task SignIn_falls_back_to_the_usage_login_when_user_lookup_times_out()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.UserUrl, _ => throw new TaskCanceledException("timeout"));
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);

        Assert.Equal("octocat", provider.SignedInLogin);
    }

    [Fact]
    public async Task SignIn_falls_back_to_the_usage_login_when_user_lookup_is_not_json()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.UserUrl, _ => Reply(HttpStatusCode.OK, "<html>")());
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);

        Assert.Equal("octocat", provider.SignedInLogin);
    }

    [Fact]
    public async Task SignIn_still_propagates_caller_cancellation_from_the_user_lookup()
    {
        using var cts = new CancellationTokenSource();
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.UserUrl, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SignInWithTokenAsync("ghu_device", cts.Token));

        Assert.Null(_secrets.Load(CopilotSavedToken.SecretName));
    }

    [Fact]
    public async Task SignOut_during_an_in_flight_fetch_is_not_undone_by_that_fetch()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        _repliesByToken["gho_saved"] = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"))();
        };
        var provider = CreateProvider();

        var inFlight = Task.Run(() => provider.FetchAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        provider.SignOut();
        release.Set();
        await inFlight;
        var next = await provider.FetchAsync(CancellationToken.None);

        Assert.Null(provider.SignedInLogin);
        Assert.Null(_secrets.Load(CopilotSavedToken.SecretName));
        Assert.Equal(ProviderStatus.SignedOut, next.Status);
        Assert.Equal(["gho_saved"], _tokensTried); // the next fetch did not reuse the old saved token
    }

    [Fact]
    public async Task SignIn_with_rejected_token_throws_and_saves_nothing()
    {
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<CopilotSignInException>(
            () => provider.SignInWithTokenAsync("ghu_bad", CancellationToken.None));

        Assert.Equal(CopilotMessages.TokenRejected, ex.Message);
        Assert.DoesNotContain("ghu_bad", ex.ToString());
        Assert.Null(_secrets.Load(CopilotSavedToken.SecretName));
        Assert.Null(provider.SignedInLogin);
    }

    [Fact]
    public async Task SignOut_deletes_saved_token_and_forgets_it()
    {
        GivenUserEndpoint();
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();
        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);

        provider.SignOut();
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Null(_secrets.Load(CopilotSavedToken.SecretName));
        Assert.Null(provider.SignedInLogin);
        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(["ghu_device"], _tokensTried); // only the sign-in validation call
    }

    [Fact]
    public async Task SignedInLogin_is_loaded_from_the_saved_token_on_fetch()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        _repliesByToken["gho_saved"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("saved-user", provider.SignedInLogin);
    }

    private CopilotUsageProvider CreateProvider() =>
        new(TestContexts.Create(_http, _time, _files, _env, _processes, _credentials, _secrets));

    private void GivenGh(string stdout)
    {
        _processes.SetPath("gh", @"C:\Program Files\GitHub CLI\gh.exe");
        _processes.OnRun(_ =>
        {
            _ghRuns++;
            return new ProcessResult(0, stdout, "", false);
        });
    }

    private void GivenUserEndpoint() =>
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.UserUrl, r =>
        {
            _userRequestAuthorization = r.Headers.Authorization?.ToString();
            _userRequestApiVersion = r.Headers.TryGetValues("X-GitHub-Api-Version", out var values) ? values.Single() : null;
            return Reply(HttpStatusCode.OK, Fixture.Read("copilot/github-user.json"))();
        });

    private static Func<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
