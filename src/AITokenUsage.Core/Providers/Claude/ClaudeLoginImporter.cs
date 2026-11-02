using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Reads the login that "claude auth login" wrote into the widget's temporary CLAUDE_CONFIG_DIR (spec §2.2).
/// Pure parsing: the caller reads the files (shared access) and deletes the folder.
/// </summary>
public static class ClaudeLoginImporter
{
    /// <summary>
    /// The imported login when the spec §2.2-3 completion condition holds (claudeAiOauth has accessToken and
    /// refreshToken, expiresAt &gt; 0 and user:profile in scopes), else null.
    /// <paramref name="claudeJson"/> (.claude.json, null when missing) supplies oauthAccount.emailAddress and organizationName;
    /// a missing or malformed file leaves both empty.
    /// </summary>
    public static ClaudeOwnedCredentials? TryRead(string? credentialsJson, string? claudeJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return null;
        }

        ClaudeOwnedCredentials? login;
        try
        {
            using JsonDocument document = JsonDocument.Parse(credentialsJson);
            if (Json.Obj(document.RootElement, "claudeAiOauth") is not { } oauth
                || ClaudeOwnedCredentials.TryReadCore(oauth, "accessToken", "refreshToken") is not { } core)
            {
                return null;
            }

            login = new ClaudeOwnedCredentials(
                core.AccessToken,
                core.RefreshToken,
                core.ExpiresAt,
                ClaudeOwnedCredentials.EpochMillisecondsOrNull(oauth, "refreshTokenExpiresAt"),
                core.Scopes,
                Json.Str(oauth, "subscriptionType"),
                Json.Str(oauth, "rateLimitTier"),
                null,
                null);
        }
        catch (JsonException)
        {
            // Claude Code may still be writing the file: not done yet.
            return null;
        }

        (string? email, string? organization) = ReadAccount(claudeJson);
        return login with { Email = email, OrganizationName = organization };
    }

    private static (string? Email, string? Organization) ReadAccount(string? claudeJson)
    {
        if (string.IsNullOrWhiteSpace(claudeJson))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(claudeJson);
            if (Json.Obj(document.RootElement, "oauthAccount") is not { } account)
            {
                return (null, null);
            }

            return (Json.Str(account, "emailAddress"), Json.Str(account, "organizationName"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
