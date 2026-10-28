using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexUsageProviderTests
{
    private const string NpmShim = @"C:\Users\test\AppData\Roaming\npm\codex.cmd";
    private const string RolloutFile =
        CodexTestData.SessionsDir + @"\2026\10\28\rollout-2026-10-28T11-40-00-00000000-0000-4000-8000-000000000001.jsonl";
    private const string InitializeOk = """{"id":1,"result":{"userAgent":"codex_cli_rs/0.142.4"}}""";
    private const string RateLimitsOk =
        """{"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":77,"windowDurationMins":300,"resetsAt":1793163600},"secondary":{"usedPercent":8,"windowDurationMins":10080,"resetsAt":1793415600},"planType":"plus"},"rateLimitsByLimitId":null}}""";
    private const string RateLimitsFailed =
        """{"id":2,"error":{"code":-32603,"message":"failed to fetch codex rate limits: upstream 502"}}""";
    private const string RateLimitsAuthRequired =
        """{"id":2,"error":{"code":-32600,"message":"chatgpt authentication required to read rate limits"}}""";

    private readonly FakeTimeProvider _time = new(CodexTestData.Now);
    private readonly StubHttpHandler _http = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly List<CapturedRequest> _requests = new();
    private readonly List<string> _startedFileNames = new();

    private sealed record CapturedRequest(string Url, IReadOnlyDictionary<string, string> Headers);

    private CodexUsageProvider CreateProvider() => new(TestContexts.Create(_http, _time, _files, _env, _processes));

    private void GivenAuth(string json)
    {
        _files.AddDirectory(CodexTestData.Home);
        if (_files.FileExists(CodexTestData.AuthPath)) _files.Remove(CodexTestData.AuthPath);
        _files.AddFile(CodexTestData.AuthPath, json, CodexTestData.FileTime);
    }

    private void GivenWham(Func<CapturedRequest, HttpResponseMessage> respond)
        => _http.When(_ => true, request =>
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var captured = new CapturedRequest(request.RequestUri!.AbsoluteUri, headers);
            _requests.Add(captured);
            return respond(captured);
        });

    private void GivenCodexInstalled(params string[] stdoutLines)
    {
        _processes.SetPath("codex", NpmShim);
        _processes.OnInteractive(spec =>
        {
            _startedFileNames.Add(spec.FileName);
            var process = new FakeInteractiveProcess();
            foreach (var line in stdoutLines) process.EnqueueOutput(line);
            return process;
        });
    }

    private void GivenRollout(string fixture)
    {
        _files.AddDirectory(CodexTestData.SessionsDir);
        _files.AddFile(RolloutFile, Fixture.Read(fixture), CodexTestData.FileTime);
    }

    private void UseKoreanTime() => _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string StaleAuth() => CodexTestData.AuthJson(accessToken: CodexTestData.AccessJwt(CodexTestData.Now.AddMinutes(2)));

    [Fact]
    public void Metadata_FollowsSpec()
    {
        _env.Set("CODEX_HOME", @"D:\codex-home");

        var provider = CreateProvider();

        Assert.Equal(ProviderIds.Codex, provider.Id);
        Assert.Equal("Codex", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(1), provider.MinInterval);
        Assert.Equal(new[] { @"D:\codex-home\auth.json" }, provider.WatchPaths);
        Assert.Equal(new[] { "5h", "weekly", "daily", "monthly" }, provider.StaticMeterOptions.Select(o => o.Key).ToArray());
    }

    [Fact]
    public async Task NothingInstalled_IsNotInstalled()
    {
        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Equal("설치 안 됨 — codex 설치 후 codex login", snapshot.StatusMessage);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApiKeyMode_IsUnsupported_WithoutAnyCall(bool hasTokens)
    {
        GivenAuth(CodexTestData.AuthJson(authMode: "apikey", openAiApiKey: "sk-test-key", includeTokens: hasTokens));
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal("API 키 로그인 — 플랜 한도 없음", snapshot.StatusMessage);
        Assert.Empty(_requests);
        Assert.Empty(_startedFileNames);
    }

    [Fact]
    public async Task FreshToken_CallsWham_WithSpecHeaders()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-access-token-without-exp"));
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("codex/wham-plus.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Plus", snapshot.PlanLabel);
        Assert.Null(snapshot.StatusMessage);
        Assert.Equal(CodexTestData.Now, snapshot.FetchedAt);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(42, snapshot.Meters[0].UsedPercent);

        var request = Assert.Single(_requests);
        Assert.Equal(CodexTestData.DefaultUsageUrl, request.Url);
        Assert.Equal("Bearer test-access-token-without-exp", request.Headers["Authorization"]);
        Assert.Equal(CodexTestData.AccountId, request.Headers["chatgpt-account-id"]);
        Assert.Equal(CodexUsageClient.UserAgent, request.Headers["User-Agent"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.False(request.Headers.ContainsKey("X-OpenAI-Fedramp"));
        Assert.False(request.Headers.ContainsKey("x-openai-codex-luna-reserve"));
        Assert.False(request.Headers.ContainsKey("originator"));
        Assert.Empty(_startedFileNames);
    }

    [Fact]
    public async Task FreshToken_FedrampAccount_SendsFedrampHeader()
    {
        GivenAuth(CodexTestData.AuthJson(idToken: CodexTestData.IdJwt(fedramp: true)));
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("codex/wham-plus.json")));

        await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal("true", Assert.Single(_requests).Headers["X-OpenAI-Fedramp"]);
    }

    [Fact]
    public async Task ConfigTomlBaseUrl_ChangesUsageUrl()
    {
        GivenAuth(CodexTestData.AuthJson());
        _files.AddFile(CodexTestData.ConfigPath, "chatgpt_base_url = \"http://localhost:8080\"\n", CodexTestData.FileTime);
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("codex/wham-plus.json")));

        await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal("http://localhost:8080/api/codex/usage", Assert.Single(_requests).Url);
    }

    [Fact]
    public async Task Unauthorized_TokenChangedOnDisk_RetriesOnceWithNewToken()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-old-access-token"));
        GivenWham(request =>
        {
            if (request.Headers["Authorization"] == "Bearer test-old-access-token")
            {
                GivenAuth(CodexTestData.AuthJson(accessToken: "test-new-access-token")); // Codex refreshed meanwhile
                return Json(HttpStatusCode.Unauthorized, "{}");
            }
            return Json(HttpStatusCode.OK, Fixture.Read("codex/wham-plus.json"));
        });

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, _requests.Count);
        Assert.Equal("Bearer test-new-access-token", _requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Unauthorized_TokenUnchanged_NoFallback_WaitsForCli()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-access-token"));
        GivenWham(_ => Json(HttpStatusCode.Unauthorized, "{}"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.WaitingForCliRefresh, snapshot.Status);
        Assert.Equal("Codex 로그인 갱신 대기 — 터미널에서 codex 실행", snapshot.StatusMessage);
        Assert.Single(_requests); // no retry with the same token
    }

    [Fact]
    public async Task Unauthorized_TokenUnchanged_UsesAppServer()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-access-token"));
        GivenWham(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(77, snapshot.Meters[0].UsedPercent);
        Assert.Single(_requests);
        Assert.Single(_startedFileNames);
    }

    [Fact]
    public async Task StaleToken_SkipsWham_AndUsesAppServerThroughCmd()
    {
        GivenAuth(StaleAuth());
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Plus", snapshot.PlanLabel);
        Assert.Null(snapshot.StatusMessage);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Empty(_requests);
        Assert.Equal(new[] { "cmd.exe" }, _startedFileNames);
    }

    [Fact]
    public async Task AppServer_RunsAtMostOncePerFifteenMinutes_ThenRolloutFallback()
    {
        UseKoreanTime();
        GivenAuth(StaleAuth());
        GivenCodexInstalled(InitializeOk, RateLimitsFailed);
        GivenRollout("codex/rollout-d.jsonl");
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));
        await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal("로그 기준 · 11:50", first.StatusMessage);
        Assert.Equal("Pro", first.PlanLabel);
        Assert.Equal(42, first.Meters[0].UsedPercent);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        Assert.Equal(2, _startedFileNames.Count); // t=0 and t=15min, not t=5min
    }

    [Fact]
    public async Task StaleToken_NoCodex_UsesRolloutLogs()
    {
        UseKoreanTime();
        GivenAuth(StaleAuth());
        GivenRollout("codex/rollout-c.jsonl");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("로그 기준 · 09:40", snapshot.StatusMessage);
        Assert.Equal("Plus", snapshot.PlanLabel);
        Assert.Equal(0, snapshot.Meters[0].UsedPercent); // 5h reset passed at 02:00Z
        Assert.Equal(41, snapshot.Meters[1].UsedPercent);
    }

    [Fact]
    public async Task StaleToken_NoCodex_NoLogs_WaitsForCli()
    {
        GivenAuth(StaleAuth());

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.WaitingForCliRefresh, snapshot.Status);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task NoAuthFile_AppServerSaysLoginRequired_IsSignedOut_EvenWhenThrottled()
    {
        GivenCodexInstalled(InitializeOk, RateLimitsAuthRequired);
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, first.Status);
        Assert.Equal("로그인 필요 — 터미널에서 codex login", first.StatusMessage);
        Assert.Equal(ProviderStatus.SignedOut, second.Status);
        Assert.Single(_startedFileNames);
    }

    [Fact]
    public async Task NoAuthFile_KeyringLogin_ReusesAppServerResultWhileThrottled()
    {
        GivenCodexInstalled(InitializeOk, RateLimitsOk);
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        Assert.Equal(first.Meters, second.Meters);
        Assert.Single(_startedFileNames);
    }

    [Fact]
    public async Task TooManyRequests_IsRateLimited_WithRetryAfter()
    {
        UseKoreanTime();
        GivenAuth(CodexTestData.AuthJson());
        GivenWham(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return response;
        });
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(20), snapshot.RetryAfter);
        Assert.Equal("요청 제한 — 12:20 이후 재시도", snapshot.StatusMessage);
        Assert.Empty(_startedFileNames);
    }

    [Fact]
    public async Task ServerError_NoFallback_IsErrorWithStatusCode()
    {
        GivenAuth(CodexTestData.AuthJson());
        GivenWham(_ => Json(HttpStatusCode.ServiceUnavailable, "upstream unavailable"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("Codex 사용량을 불러오지 못했습니다 (HTTP 503)", snapshot.StatusMessage);
    }

    [Fact]
    public async Task HtmlBody_IsTransientFailure_AndFallsBackToLogs()
    {
        UseKoreanTime();
        GivenAuth(CodexTestData.AuthJson());
        GivenWham(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Just a moment...</html>", Encoding.UTF8, "text/html"),
        });
        GivenRollout("codex/rollout-d.jsonl");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("로그 기준 · 11:50", snapshot.StatusMessage);
    }

    [Fact]
    public async Task CorruptAuthFile_IsError_NotSignedOut()
    {
        GivenAuth("{\"auth_mode\":\"chatgpt\",\"tokens\":{");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("auth.json을 읽지 못했습니다 — 잠시 후 다시 시도", snapshot.StatusMessage);
    }

    [Fact]
    public async Task ChatGptLoginWithoutAccessToken_NeverCallsWham_AndFallsBackToAppServer()
    {
        GivenAuth(CodexTestData.AuthJson(includeTokens: false));
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("codex/wham-plus.json")));
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Empty(_requests); // a missing access token must never reach the wham path
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(_startedFileNames);
    }

    [Fact]
    public async Task ChatGptLoginWithoutAccessToken_NoCodex_IsUnsupported_WithoutHttp()
    {
        GivenAuth(CodexTestData.AuthJson(includeTokens: false));
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("codex/wham-plus.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Empty(_requests);
        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal("이 로그인 방식은 codex 설치 후 조회 가능", snapshot.StatusMessage);
    }
}
