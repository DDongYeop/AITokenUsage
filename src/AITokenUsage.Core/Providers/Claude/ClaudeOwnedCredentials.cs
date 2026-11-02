using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// The login of an extra Claude account that the widget owns and renews itself (spec §2.1).
/// Stored through ISecretStore (DPAPI) as JSON; times are Unix milliseconds. Never logged: ToString omits both tokens.
/// </summary>
public sealed record ClaudeOwnedCredentials(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RefreshTokenExpiresAt,
    IReadOnlyList<string> Scopes,
    string? SubscriptionType,
    string? RateLimitTier,
    string? Email,
    string? OrganizationName)
{
    private const long MaxEpochMilliseconds = 253_402_300_799_999; // 9999-12-31T23:59:59.999Z

    /// <summary>The stored form: accessToken, refreshToken, expiresAt, refreshTokenExpiresAt, scopes, subscriptionType, rateLimitTier, email, organizationName.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("accessToken", AccessToken);
            writer.WriteString("refreshToken", RefreshToken);
            writer.WriteNumber("expiresAt", ExpiresAt.ToUnixTimeMilliseconds());
            if (RefreshTokenExpiresAt is { } refreshExpiry)
            {
                writer.WriteNumber("refreshTokenExpiresAt", refreshExpiry.ToUnixTimeMilliseconds());
            }
            else
            {
                writer.WriteNull("refreshTokenExpiresAt");
            }

            writer.WriteStartArray("scopes");
            foreach (string scope in Scopes)
            {
                writer.WriteStringValue(scope);
            }

            writer.WriteEndArray();
            WriteNullable(writer, "subscriptionType", SubscriptionType);
            WriteNullable(writer, "rateLimitTier", RateLimitTier);
            WriteNullable(writer, "email", Email);
            WriteNullable(writer, "organizationName", OrganizationName);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parses <see cref="ToJson"/> output. Null for malformed JSON, a missing token, a missing or non-positive
    /// expiresAt, or scopes without user:profile (such a login cannot read usage).
    /// </summary>
    public static ClaudeOwnedCredentials? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || TryReadCore(root, "accessToken", "refreshToken") is not { } core)
            {
                return null;
            }

            return new ClaudeOwnedCredentials(
                core.AccessToken,
                core.RefreshToken,
                core.ExpiresAt,
                EpochMillisecondsOrNull(root, "refreshTokenExpiresAt"),
                core.Scopes,
                Json.Str(root, "subscriptionType"),
                Json.Str(root, "rateLimitTier"),
                Json.Str(root, "email"),
                Json.Str(root, "organizationName"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The values a usage request needs.</summary>
    public ClaudeCredentials ToCredentials() => new(AccessToken, ExpiresAt, Scopes, SubscriptionType, RateLimitTier);

    // Records print every member by default; never let a token reach a log line.
    public override string ToString() =>
        $"ClaudeOwnedCredentials {{ ExpiresAt = {ExpiresAt:O}, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O}, " +
        $"SubscriptionType = {SubscriptionType}, RateLimitTier = {RateLimitTier}, Email = {Email}, OrganizationName = {OrganizationName} }}";

    /// <summary>
    /// The required part shared by the stored form and Claude Code's claudeAiOauth block: both tokens non-blank,
    /// expiresAt &gt; 0 (ms) and a scopes array containing user:profile. Null when any of them is missing.
    /// </summary>
    internal static (string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes)? TryReadCore(
        JsonElement obj, string accessTokenKey, string refreshTokenKey)
    {
        string? accessToken = Json.Str(obj, accessTokenKey);
        string? refreshToken = Json.Str(obj, refreshTokenKey);
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        if (Json.Num(obj, "expiresAt") is not { } expiresAtMs || expiresAtMs <= 0 || expiresAtMs > MaxEpochMilliseconds)
        {
            return null;
        }

        if (Json.Arr(obj, "scopes") is not { } scopeArray)
        {
            return null;
        }

        List<string> scopes = scopeArray.EnumerateArray()
            .Where(scope => scope.ValueKind == JsonValueKind.String)
            .Select(scope => scope.GetString()!)
            .ToList();
        if (!scopes.Contains(ClaudeCredentialReader.ProfileScope, StringComparer.Ordinal))
        {
            return null;
        }

        return (accessToken, refreshToken, DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMs), scopes);
    }

    /// <summary>A positive Unix-millisecond value in range, else null.</summary>
    internal static DateTimeOffset? EpochMillisecondsOrNull(JsonElement obj, string name) =>
        Json.Num(obj, name) is { } ms && ms > 0 && ms <= MaxEpochMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : null;

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
