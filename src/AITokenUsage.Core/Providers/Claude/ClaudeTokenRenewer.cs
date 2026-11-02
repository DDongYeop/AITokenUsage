using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

public enum ClaudeRenewOutcome
{
    Renewed,

    /// <summary>The token endpoint refused the refresh token (HTTP 400/401) or it has expired: sign in again.</summary>
    ReauthRequired,

    /// <summary>Temporary failure (network, timeout, 429, 5xx, bad body). Keep the tokens and try again later.</summary>
    Failed,
}

/// <summary><see cref="Credentials"/> is the renewed login for <see cref="ClaudeRenewOutcome.Renewed"/>, else null.</summary>
public sealed record ClaudeRenewResult(ClaudeRenewOutcome Outcome, ClaudeOwnedCredentials? Credentials)
{
    public override string ToString() => $"ClaudeRenewResult {{ Outcome = {Outcome} }}";
}

/// <summary>
/// Renews a widget-owned Claude login with Claude Code's public OAuth client (spec §2.3).
/// Only <see cref="ClaudeOwnedCredentialSource"/> may use it: Claude Code's own login is never refreshed by the widget,
/// because refresh tokens rotate and are single-use. Logs only the outcome and HTTP status code.
/// </summary>
public sealed class ClaudeTokenRenewer
{
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string DefaultScope = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;

    public ClaudeTokenRenewer(HttpClient http, TimeProvider time, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _http = http;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Spec §2.3. HTTP 400/401, or a refresh token whose RefreshTokenExpiresAt has passed (no request is sent),
    /// give <see cref="ClaudeRenewOutcome.ReauthRequired"/>; any other failure gives <see cref="ClaudeRenewOutcome.Failed"/>.
    /// Never throws except when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<ClaudeRenewResult> RenewAsync(ClaudeOwnedCredentials current, string userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(userAgent);

        if (current.RefreshTokenExpiresAt is { } refreshExpiry && refreshExpiry <= _time.GetUtcNow())
        {
            _log.Info("Claude token renewal: refresh token expired; sign-in required.");
            return new ClaudeRenewResult(ClaudeRenewOutcome.ReauthRequired, null);
        }

        using var timeout = new CancellationTokenSource(Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            using HttpRequestMessage request = CreateRequest(current, userAgent);
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
            int code = (int)response.StatusCode;

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                _log.Warn($"Claude token renewal: refused (HTTP {code}); sign-in required.");
                return new ClaudeRenewResult(ClaudeRenewOutcome.ReauthRequired, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Claude token renewal: failed (HTTP {code}).");
                return new ClaudeRenewResult(ClaudeRenewOutcome.Failed, null);
            }

            string body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (Interpret(body, current, _time.GetUtcNow()) is not { } renewed)
            {
                _log.Warn($"Claude token renewal: unusable response body (HTTP {code}).");
                return new ClaudeRenewResult(ClaudeRenewOutcome.Failed, null);
            }

            _log.Info($"Claude token renewal: renewed (HTTP {code}).");
            return new ClaudeRenewResult(ClaudeRenewOutcome.Renewed, renewed);
        }
        catch (HttpRequestException)
        {
            // The exception is not logged: keep the log to the outcome only.
            _log.Warn("Claude token renewal: failed (network).");
            return new ClaudeRenewResult(ClaudeRenewOutcome.Failed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn("Claude token renewal: failed (timeout).");
            return new ClaudeRenewResult(ClaudeRenewOutcome.Failed, null);
        }
    }

    private static HttpRequestMessage CreateRequest(ClaudeOwnedCredentials current, string userAgent)
    {
        string scope = current.Scopes.Count > 0 ? string.Join(' ', current.Scopes) : DefaultScope;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("grant_type", "refresh_token");
            writer.WriteString("refresh_token", current.RefreshToken);
            writer.WriteString("client_id", ClientId);
            writer.WriteString("scope", scope);
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>
    /// access_token and expires_in (seconds, &gt; 0) are required. refresh_token, refresh_token_expires_in and scope
    /// replace the stored values only when present. Null when the body is unusable.
    /// </summary>
    private static ClaudeOwnedCredentials? Interpret(string body, ClaudeOwnedCredentials current, DateTimeOffset now)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? accessToken = Json.Str(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken)
                || Json.Num(root, "expires_in") is not { } expiresIn
                || expiresIn <= 0
                || Json.AddSecondsOrNull(now, expiresIn) is not { } expiresAt)
            {
                return null;
            }

            string? refreshToken = Json.Str(root, "refresh_token");
            DateTimeOffset? refreshExpiresAt = current.RefreshTokenExpiresAt;
            if (Json.Num(root, "refresh_token_expires_in") is { } refreshExpiresIn
                && refreshExpiresIn > 0
                && Json.AddSecondsOrNull(now, refreshExpiresIn) is { } newRefreshExpiresAt)
            {
                refreshExpiresAt = newRefreshExpiresAt;
            }

            IReadOnlyList<string> scopes = current.Scopes;
            if (Json.Str(root, "scope") is { } scopeText
                && scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } newScopes)
            {
                scopes = newScopes;
            }

            return current with
            {
                AccessToken = accessToken,
                RefreshToken = string.IsNullOrWhiteSpace(refreshToken) ? current.RefreshToken : refreshToken,
                ExpiresAt = expiresAt,
                RefreshTokenExpiresAt = refreshExpiresAt,
                Scopes = scopes,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
