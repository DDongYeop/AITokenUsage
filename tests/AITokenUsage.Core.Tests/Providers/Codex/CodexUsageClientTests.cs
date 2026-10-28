using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexUsageClientTests
{
    private static CodexAuth Auth(string? accountId = CodexTestData.AccountId, bool fedramp = false)
        => new(CodexAuthMode.ChatGpt, "test-access-token", accountId, fedramp, "plus", CodexTestData.Now.AddHours(1), null);

    [Fact]
    public void BuildRequest_SendsRequiredHeaders_AndNothingElse()
    {
        using var request = CodexUsageClient.BuildRequest(CodexTestData.DefaultUsageUrl, Auth());

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(CodexTestData.DefaultUsageUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-access-token", request.Headers.Authorization.Parameter);
        Assert.Equal(CodexTestData.AccountId, Assert.Single(request.Headers.GetValues("chatgpt-account-id")));
        Assert.Equal(CodexUsageClient.UserAgent, string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
        Assert.False(request.Headers.Contains("X-OpenAI-Fedramp"));
        Assert.False(request.Headers.Contains("x-openai-codex-luna-reserve"));
        Assert.False(request.Headers.Contains("originator"));
        Assert.Null(request.Content);
    }

    [Fact]
    public void BuildRequest_AddsFedrampHeader_OnlyForFedrampAccounts()
    {
        using var request = CodexUsageClient.BuildRequest(CodexTestData.DefaultUsageUrl, Auth(fedramp: true));

        Assert.Equal("true", Assert.Single(request.Headers.GetValues("X-OpenAI-Fedramp")));
    }

    [Fact]
    public void BuildRequest_OmitsAccountHeader_WhenAccountUnknown()
    {
        using var request = CodexUsageClient.BuildRequest(CodexTestData.DefaultUsageUrl, Auth(accountId: null));

        Assert.False(request.Headers.Contains("chatgpt-account-id"));
    }

    [Fact]
    public async Task GetUsageAsync_Ok_ReturnsBody()
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ => Json(HttpStatusCode.OK, Fixture.Read("codex/wham-plus.json")));
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(CodexHttpOutcome.Ok, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("\"plan_type\"", result.Body);
        Assert.DoesNotContain("plan_type", result.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CodexHttpOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CodexHttpOutcome.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CodexHttpOutcome.Failed)]
    public async Task GetUsageAsync_MapsErrorStatus(HttpStatusCode status, CodexHttpOutcome expected)
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ => Json(status, "{\"error\":{\"message\":\"test\"}}"));
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task GetUsageAsync_429_ReadsRetryAfter()
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(CodexHttpOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), result.RetryAfter);
    }

    [Fact]
    public async Task GetUsageAsync_NetworkError_IsFailedWithoutStatus()
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ => throw new HttpRequestException("connection refused"));
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(CodexHttpOutcome.Failed, result.Outcome);
        Assert.Null(result.StatusCode);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
