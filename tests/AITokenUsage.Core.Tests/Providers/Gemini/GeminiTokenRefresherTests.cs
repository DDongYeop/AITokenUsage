using System.Net;
using System.Text;
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiTokenRefresherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _http = new();
    private string? _body;
    private string? _contentType;

    [Fact]
    public async Task Posts_form_encoded_public_client_and_returns_new_token()
    {
        GivenTokenEndpoint(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json"));

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.Ok, result.Status);
        Assert.Equal("test-refreshed-access-token", result.AccessToken);
        Assert.Equal(Now.AddSeconds(3599), result.Expiry);
        Assert.Null(result.IdToken);
        Assert.Equal("application/x-www-form-urlencoded", _contentType);
        Assert.Contains("client_id=test-client-id.apps.googleusercontent.com", _body);
        Assert.Contains("client_secret=test-client-secret", _body);
        Assert.Contains("refresh_token=test-refresh-token", _body);
        Assert.Contains("grant_type=refresh_token", _body);
    }

    [Fact]
    public async Task Invalid_grant_is_reported_separately()
    {
        GivenTokenEndpoint(HttpStatusCode.BadRequest, Fixture.Read("gemini/refresh-invalid-grant.json"));

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.InvalidGrant, result.Status);
        Assert.Null(result.AccessToken);
    }

    [Fact]
    public async Task Server_error_is_Failed_with_status()
    {
        GivenTokenEndpoint(HttpStatusCode.InternalServerError, "oops");

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.Failed, result.Status);
        Assert.Equal(500, result.HttpStatus);
    }

    [Fact]
    public async Task Result_ToString_hides_the_token()
    {
        GivenTokenEndpoint(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json"));

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.DoesNotContain("test-refreshed-access-token", result.ToString());
    }

    [Fact]
    public async Task Missing_client_returns_ClientUnavailable_without_calling_the_network()
    {
        var refresher = new GeminiTokenRefresher(new HttpClient(_http), _time, () => null);

        var result = await refresher.RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.ClientUnavailable, result.Status);
        Assert.Empty(_http.Requests);
    }

    private static readonly GeminiOAuthClient TestClient =
        new("test-client-id.apps.googleusercontent.com", "test-client-secret");

    private GeminiTokenRefresher CreateRefresher() => new(new HttpClient(_http), _time, () => TestClient);

    private void GivenTokenEndpoint(HttpStatusCode status, string body)
    {
        _http.When(
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri == GeminiTokenRefresher.TokenUrl,
            r =>
            {
                _body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                _contentType = r.Content.Headers.ContentType?.MediaType;
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
    }
}
