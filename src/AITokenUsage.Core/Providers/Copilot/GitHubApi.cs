using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>Shared GitHub request details. GitHub rejects requests without a User-Agent.</summary>
public static class GitHubApi
{
    public static readonly string UserAgent =
        "AITokenUsage/" + (typeof(GitHubApi).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
    public const string CopilotApiVersion = "2025-04-01";
    public const string CopilotUserUrl = "https://api.github.com/copilot_internal/user";
    public const string UserUrl = "https://api.github.com/user";

    /// <param name="apiVersion">X-GitHub-Api-Version, or null to omit (REST endpoints reject unknown versions).</param>
    public static HttpRequestMessage CreateGet(string url, string token, string? apiVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (apiVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", apiVersion);
        }

        return request;
    }

    /// <summary>429, 403 with x-ratelimit-remaining: 0, or any non-2xx carrying Retry-After.</summary>
    public static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (!response.IsSuccessStatusCode && response.Headers.RetryAfter is not null)
        {
            return true;
        }

        return response.StatusCode == HttpStatusCode.Forbidden
               && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
               && remaining.FirstOrDefault()?.Trim() == "0";
    }

    /// <summary>Retry-After (delta or date), else x-ratelimit-reset (epoch seconds), else null.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            return date > now ? date - now : TimeSpan.Zero;
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            && epoch is > 0 and < 253_402_300_799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return reset > now ? reset - now : TimeSpan.Zero;
        }

        return null;
    }
}
