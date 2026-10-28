using System.Net;
using System.Net.Http.Headers;

namespace AITokenUsage.Core.Providers.Codex;

public enum CodexHttpOutcome { Ok, Unauthorized, RateLimited, Failed }

/// <summary>Result of one wham/usage call. Body stays in memory only; ToString never prints it.</summary>
public sealed record CodexHttpResult(CodexHttpOutcome Outcome, int? StatusCode, string? Body, TimeSpan? RetryAfter)
{
    public override string ToString() => $"CodexHttpResult {{ Outcome = {Outcome}, StatusCode = {StatusCode} }}";
}

/// <summary>
/// GET {base}/wham/usage with Authorization, chatgpt-account-id, User-Agent, Accept and
/// (FedRAMP accounts only) X-OpenAI-Fedramp. Never sends Codex's reserve opt-in header or an
/// originator header, and never refreshes tokens.
/// </summary>
public sealed class CodexUsageClient
{
    public static readonly string UserAgent =
        "AITokenUsage/" + (typeof(CodexUsageClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public CodexUsageClient(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public static HttpRequestMessage BuildRequest(string usageUrl, CodexAuth auth)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, usageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(auth.AccountId))
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", auth.AccountId);
        if (auth.IsFedramp)
            request.Headers.TryAddWithoutValidation("X-OpenAI-Fedramp", "true");
        return request;
    }

    public async Task<CodexHttpResult> GetUsageAsync(string usageUrl, CodexAuth auth, CancellationToken ct)
    {
        using var request = BuildRequest(usageUrl, auth);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new CodexHttpResult(CodexHttpOutcome.Unauthorized, status, null, null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new CodexHttpResult(CodexHttpOutcome.RateLimited, status, null, RetryAfterOf(response));
            if (!response.IsSuccessStatusCode)
                return new CodexHttpResult(CodexHttpOutcome.Failed, status, null, null);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new CodexHttpResult(CodexHttpOutcome.Ok, status, body, null);
        }
        catch (HttpRequestException)
        {
            return new CodexHttpResult(CodexHttpOutcome.Failed, null, null, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CodexHttpResult(CodexHttpOutcome.Failed, null, null, null); // HttpClient timeout
        }
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            if (wait > TimeSpan.Zero) return wait;
        }
        return null;
    }
}
