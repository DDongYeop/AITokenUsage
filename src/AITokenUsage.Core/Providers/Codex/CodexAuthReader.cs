using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>
/// Reads Codex auth.json with FileShare.ReadWrite|Delete (via IFileSystem). Never writes it.
/// A parse failure is retried (Codex rewrites the file in place) and is never treated as a logout.
/// </summary>
public sealed class CodexAuthReader
{
    public const string AuthClaim = "https://api.openai.com/auth";

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    private readonly IFileSystem _files;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public CodexAuthReader(IFileSystem files, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _files = files;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public async Task<CodexAuthReadResult> ReadAsync(string authFilePath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            string? text;
            try
            {
                text = _files.ReadAllTextShared(authFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text = ""; // transient (sharing violation etc.) - retry like a partial file
            }

            if (text is null) return CodexAuthReadResult.Missing;

            var auth = TryParse(text);
            if (auth is not null) return new CodexAuthReadResult(CodexAuthReadStatus.Ok, auth);
            if (attempt >= RetryDelays.Length) return CodexAuthReadResult.Unreadable;
            await _delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    /// <summary>Parses auth.json text. Returns null when the text is not a JSON object.</summary>
    public static CodexAuth? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tokens = Json.Obj(root, "tokens");
            var accessToken = tokens is { } t1 ? NonEmpty(Json.Str(t1, "access_token")) : null;
            var idToken = tokens is { } t2 ? NonEmpty(Json.Str(t2, "id_token")) : null;
            var storedAccountId = tokens is { } t3 ? NonEmpty(Json.Str(t3, "account_id")) : null;

            var rawMode = NonEmpty(Json.Str(root, "auth_mode"));
            var mode = rawMode is null ? ResolveMissingMode(root, accessToken) : MapMode(rawMode);

            var accountId = storedAccountId ?? AccountIdFromJwt(idToken) ?? AccountIdFromJwt(accessToken);
            var idClaims = AuthClaims(idToken);
            var isFedramp = idClaims is { } c1 && Json.Bool(c1, "chatgpt_account_is_fedramp") == true;
            var planClaim = idClaims is { } c2 ? NonEmpty(Json.Str(c2, "chatgpt_plan_type")) : null;

            return new CodexAuth(
                mode,
                accessToken,
                accountId,
                isFedramp,
                planClaim,
                Jwt.GetExpiry(accessToken),
                ParseTimestamp(Json.Str(root, "last_refresh")));
        }
    }

    /// <summary>RFC 3339 UTC timestamp (Codex writes 0/3/6/9 fraction digits).</summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static CodexAuthMode MapMode(string rawMode)
    {
        if (Is(rawMode, "apikey") || Is(rawMode, "bedrockApiKey") || Is(rawMode, "bedrockAccessKeys"))
            return CodexAuthMode.ApiKey;
        if (Is(rawMode, "chatgpt") || Is(rawMode, "chatgptAuthTokens"))
            return CodexAuthMode.ChatGpt;
        return CodexAuthMode.Other;
    }

    // Old files have no auth_mode. Codex resolves: personal_access_token > bedrock_api_key >
    // bedrock_access_keys > OPENAI_API_KEY != null > chatgpt. A ChatGPT login may also store a
    // token-exchanged OPENAI_API_KEY, so an existing access_token wins first.
    private static CodexAuthMode ResolveMissingMode(JsonElement root, string? accessToken)
    {
        if (accessToken is not null) return CodexAuthMode.ChatGpt;
        if (HasValue(root, "personal_access_token")) return CodexAuthMode.Other;
        if (HasValue(root, "bedrock_api_key") || HasValue(root, "bedrock_access_keys")) return CodexAuthMode.ApiKey;
        if (root.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind == JsonValueKind.String)
            return CodexAuthMode.ApiKey;
        return CodexAuthMode.ChatGpt;
    }

    private static string? AccountIdFromJwt(string? jwt)
    {
        if (!Jwt.TryReadPayload(jwt, out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
        if (NonEmpty(Json.Str(payload, "chatgpt_account_id")) is { } direct) return direct;
        if (Json.Obj(payload, AuthClaim) is { } auth && NonEmpty(Json.Str(auth, "chatgpt_account_id")) is { } nested)
            return nested;
        if (Json.Arr(payload, "organizations") is { } organizations)
        {
            foreach (var organization in organizations.EnumerateArray())
            {
                if (organization.ValueKind == JsonValueKind.Object && NonEmpty(Json.Str(organization, "id")) is { } id)
                    return id;
            }
        }
        return null;
    }

    private static JsonElement? AuthClaims(string? jwt)
        => Jwt.TryReadPayload(jwt, out var payload) && payload.ValueKind == JsonValueKind.Object
            ? Json.Obj(payload, AuthClaim)
            : null;

    private static bool HasValue(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
