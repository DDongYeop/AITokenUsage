using System.Net;
using System.Text.Json;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static AITokenUsage.Core.Tests.Providers.Claude.ClaudeOwnedTestData;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeTokenRenewerTests
{
    private const string UserAgent = "claude-code/2.1.400";

    private readonly StubHttpHandler _http = new();
    private readonly FakeTimeProvider _time = new(ClaudeTestData.Now);
    private readonly RecordingLog _log = new();

    private ClaudeTokenRenewer CreateRenewer() => new(new HttpClient(_http, disposeHandler: false), _time, _log);

    private void Respond(Func<HttpResponseMessage> respond) =>
        _http.When(StubHttpHandler.Post(ClaudeTokenRenewer.TokenUrl), _ => respond());

    private void AssertNoTokensInLog()
    {
        foreach (string token in AllTokens)
        {
            Assert.DoesNotContain(token, _log.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Constants_match_claude_code()
    {
        Assert.Equal("https://platform.claude.com/v1/oauth/token", ClaudeTokenRenewer.TokenUrl);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", ClaudeTokenRenewer.ClientId);
        Assert.Equal("user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload", ClaudeTokenRenewer.DefaultScope);
        Assert.Equal(TimeSpan.FromSeconds(30), ClaudeTokenRenewer.Timeout);
    }

    [Fact]
    public async Task Sends_json_body_and_headers()
    {
        Respond(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));

        await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        HttpRequestMessage request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ClaudeTokenRenewer.TokenUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString());
        Assert.Equal(UserAgent, request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(
            """{"grant_type":"refresh_token","refresh_token":"owned-refresh-token-one","client_id":"9d1c250a-e61b-44d9-88ed-5944d1962f5e","scope":"user:profile user:inference"}""",
            _http.RequestBodies[0]);
    }

    [Fact]
    public async Task Sends_default_scope_when_none_stored()
    {
        Respond(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));

        await CreateRenewer().RenewAsync(Login(scopes: []), UserAgent, CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(_http.RequestBodies[0]!);
        Assert.Equal(ClaudeTokenRenewer.DefaultScope, body.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Success_replaces_tokens_and_times()
    {
        Respond(() => StubHttpHandler.Json(
            HttpStatusCode.OK,
            TokenResponse(expiresIn: 28800, refreshTokenExpiresIn: 86400, scope: "user:profile user:inference user:mcp_servers")));
        ClaudeOwnedCredentials current = Login(refreshTokenExpiresAt: ClaudeTestData.Now.AddHours(1));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(current, UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.Renewed, result.Outcome);
        ClaudeOwnedCredentials renewed = Assert.IsType<ClaudeOwnedCredentials>(result.Credentials);
        Assert.Equal(AccessToken2, renewed.AccessToken);
        Assert.Equal(RefreshToken2, renewed.RefreshToken);
        Assert.Equal(ClaudeTestData.Now.AddSeconds(28800), renewed.ExpiresAt);
        Assert.Equal(ClaudeTestData.Now.AddSeconds(86400), renewed.RefreshTokenExpiresAt);
        Assert.Equal(new[] { "user:profile", "user:inference", "user:mcp_servers" }, renewed.Scopes);
        Assert.Equal(current.SubscriptionType, renewed.SubscriptionType);
        Assert.Equal(current.RateLimitTier, renewed.RateLimitTier);
        Assert.Equal(current.Email, renewed.Email);
        Assert.Equal(current.OrganizationName, renewed.OrganizationName);
        AssertNoTokensInLog();
    }

    [Fact]
    public async Task Response_without_refresh_token_keeps_old_refresh_token_scopes_and_refresh_expiry()
    {
        Respond(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse(refreshToken: null)));
        ClaudeOwnedCredentials current = Login(refreshTokenExpiresAt: ClaudeTestData.Now.AddDays(3));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(current, UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.Renewed, result.Outcome);
        Assert.Equal(AccessToken2, result.Credentials!.AccessToken);
        Assert.Equal(RefreshToken1, result.Credentials.RefreshToken);
        Assert.Equal(ClaudeTestData.Now.AddDays(3), result.Credentials.RefreshTokenExpiresAt);
        Assert.Equal(Scopes, result.Credentials.Scopes);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Refresh token not found or invalid"}""")]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    public async Task Bad_request_or_unauthorized_needs_sign_in_again(HttpStatusCode status, string body)
    {
        Respond(() => StubHttpHandler.Json(status, body));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.ReauthRequired, result.Outcome);
        Assert.Null(result.Credentials);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), _log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid_grant", _log.AllText, StringComparison.Ordinal);
        AssertNoTokensInLog();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rate_limit_server_and_other_errors_are_temporary(HttpStatusCode status)
    {
        Respond(() => StubHttpHandler.Json(status, "{}"));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.Failed, result.Outcome);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public async Task Failure_log_names_the_error_type_and_refusal_headers_but_no_message()
    {
        Respond(() =>
        {
            HttpResponseMessage response = StubHttpHandler.Json(
                HttpStatusCode.TooManyRequests,
                """{"type":"error","error":{"type":"rate_limit_error","message":"Slow down, friend."}}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            response.Headers.TryAddWithoutValidation("Server", "cloudflare");
            response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            response.Headers.TryAddWithoutValidation("x-should-retry", "false");
            return response;
        });

        await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        LogEntry entry = Assert.Single(_log.Entries);
        Assert.Equal(
            "Claude token renewal: failed (HTTP 429; error=rate_limit_error; content-type=application/json; retry-after=120; server=cloudflare; cf-mitigated=challenge; x-should-retry=false).",
            entry.Message);
        AssertNoTokensInLog();
    }

    [Fact]
    public async Task Failure_log_names_an_oauth_style_error_code_without_its_description()
    {
        Respond(() => StubHttpHandler.Json(
            HttpStatusCode.ServiceUnavailable,
            """{"error":"temporarily_unavailable","error_description":"Try again in a while."}"""));

        await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(
            "Claude token renewal: failed (HTTP 503; error=temporarily_unavailable; content-type=application/json).",
            Assert.Single(_log.Entries).Message);
    }

    [Fact]
    public async Task Failure_log_of_a_non_json_body_names_only_the_content_type()
    {
        Respond(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("<html><body>Just a moment...</body></html>", System.Text.Encoding.UTF8, "text/html"),
        });

        await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(
            "Claude token renewal: failed (HTTP 429; content-type=text/html).",
            Assert.Single(_log.Entries).Message);
    }

    [Theory]
    [InlineData("""{"error":"owned-refresh-token-one"}""")]
    [InlineData("""{"error":{"type":"owned-refresh-token-one"}}""")]
    [InlineData("""{"error":"not an identifier; has spaces"}""")]
    [InlineData("""{"error":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"error":42}""")]
    [InlineData("""[1]""")]
    public async Task Failure_log_drops_an_error_value_that_is_not_a_short_identifier_or_echoes_a_token(string body)
    {
        Respond(() => StubHttpHandler.Json(HttpStatusCode.TooManyRequests, body));

        await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(
            "Claude token renewal: failed (HTTP 429; content-type=application/json).",
            Assert.Single(_log.Entries).Message);
        AssertNoTokensInLog();
    }

    [Fact]
    public async Task Failure_log_cleans_and_shortens_header_values()
    {
        Respond(() =>
        {
            HttpResponseMessage response = StubHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.TryAddWithoutValidation("Retry-After", "Wed, 21 Oct 2026 07:28:00 GMT");
            response.Headers.TryAddWithoutValidation("Server", new string('s', 80) + "é<tag>");
            return response;
        });

        await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(
            "Claude token renewal: failed (HTTP 429; content-type=application/json; retry-after=Wed, 21 Oct 2026 07:28:00 GMT; server=" + new string('s', 48) + ").",
            Assert.Single(_log.Entries).Message);
    }

    [Fact]
    public async Task Network_error_is_temporary()
    {
        Respond(() => throw new HttpRequestException("No connection could be made."));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.Failed, result.Outcome);
        AssertNoTokensInLog();
    }

    [Fact]
    public async Task Timeout_is_temporary()
    {
        Respond(() => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Pending_request_times_out_after_thirty_seconds_on_the_injected_clock()
    {
        var handler = new PendingHandler();
        var renewer = new ClaudeTokenRenewer(new HttpClient(handler), _time, _log);

        Task<ClaudeRenewResult> renewal = renewer.RenewAsync(Login(), UserAgent, CancellationToken.None);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(renewal.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));
        ClaudeRenewResult result = await renewal.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ClaudeRenewOutcome.Failed, result.Outcome);
        Assert.Contains("timeout", _log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_of_a_pending_request_is_thrown()
    {
        var handler = new PendingHandler();
        var renewer = new ClaudeTokenRenewer(new HttpClient(handler), _time, _log);
        using var cts = new CancellationTokenSource();

        Task<ClaudeRenewResult> renewal = renewer.RenewAsync(Login(), UserAgent, cts.Token);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renewal.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Caller_cancellation_is_thrown()
    {
        Respond(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateRenewer().RenewAsync(Login(), UserAgent, cts.Token));
    }

    [Theory]
    [InlineData("<html>maintenance</html>")]
    [InlineData("[]")]
    [InlineData("""{"refresh_token":"x","expires_in":28800}""")]
    [InlineData("""{"access_token":"","expires_in":28800}""")]
    [InlineData("""{"access_token":"owned-access-token-two"}""")]
    [InlineData("""{"access_token":"owned-access-token-two","expires_in":"soon"}""")]
    [InlineData("""{"access_token":"owned-access-token-two","expires_in":0}""")]
    public async Task Bad_success_body_is_temporary(string body)
    {
        Respond(() => StubHttpHandler.Json(HttpStatusCode.OK, body));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(Login(), UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.Failed, result.Outcome);
        Assert.Null(result.Credentials);
        AssertNoTokensInLog();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task Expired_refresh_token_needs_sign_in_without_request(int secondsLeft)
    {
        ClaudeOwnedCredentials current = Login(refreshTokenExpiresAt: ClaudeTestData.Now.AddSeconds(secondsLeft));

        ClaudeRenewResult result = await CreateRenewer().RenewAsync(current, UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeRenewOutcome.ReauthRequired, result.Outcome);
        Assert.Empty(_http.Requests);
    }

    /// <summary>A request that never answers until its cancellation token fires.</summary>
    private sealed class PendingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }
}
