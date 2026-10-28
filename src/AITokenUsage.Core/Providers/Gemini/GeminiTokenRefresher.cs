using System.Net.Http.Headers;
using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

public enum GeminiRefreshStatus { Ok, InvalidGrant, Failed, ClientUnavailable }

public sealed record GeminiRefreshResult(
    GeminiRefreshStatus Status,
    string? AccessToken,
    DateTimeOffset? Expiry,
    string? IdToken,
    int HttpStatus)
{
    public override string ToString() => $"GeminiRefreshResult {{ Status = {Status}, HttpStatus = {HttpStatus} }}";
}

/// <summary>
/// Refreshes the Gemini CLI access token in memory with the CLI's public installed-app client,
/// located at runtime by <see cref="GeminiOAuthClientLocator"/>. The result is never written back to oauth_creds.json.
/// </summary>
public sealed class GeminiTokenRefresher(HttpClient http, TimeProvider time, Func<GeminiOAuthClient?> client)
{
    public const string TokenUrl = "https://oauth2.googleapis.com/token";

    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    public async Task<GeminiRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (client() is not { } oauthClient)
        {
            return new GeminiRefreshResult(GeminiRefreshStatus.ClientUnavailable, null, null, null, 0);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = oauthClient.ClientId,
                ["client_secret"] = oauthClient.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var root = TryParseObject(body);

        if (response.IsSuccessStatusCode && root is { } ok && Json.Str(ok, "access_token") is { Length: > 0 } accessToken)
        {
            var lifetime = Json.Num(ok, "expires_in") is double seconds && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultLifetime;
            return new GeminiRefreshResult(GeminiRefreshStatus.Ok, accessToken, time.GetUtcNow() + lifetime,
                Json.Str(ok, "id_token"), status);
        }

        if (root is { } error && Json.Str(error, "error") == "invalid_grant")
        {
            return new GeminiRefreshResult(GeminiRefreshStatus.InvalidGrant, null, null, null, status);
        }

        return new GeminiRefreshResult(GeminiRefreshStatus.Failed, null, null, null, status);
    }

    private static JsonElement? TryParseObject(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
