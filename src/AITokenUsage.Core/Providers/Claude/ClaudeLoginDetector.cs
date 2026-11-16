using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Decides from the .credentials.json that "claude auth login" writes into the sign-in folder whether the login finished
/// (used while signing an extra Claude account in). Pure parsing: the caller reads the file with shared access.
/// </summary>
public static class ClaudeLoginDetector
{
    /// <summary>
    /// True when claudeAiOauth has an accessToken and a refreshToken, expiresAt &gt; 0 and user:profile in scopes.
    /// False for a missing or partly written file: Claude Code may still be writing it.
    /// </summary>
    public static bool IsSignedIn(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(credentialsJson);
            if (Json.Obj(document.RootElement, "claudeAiOauth") is not { } oauth)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(Json.Str(oauth, "accessToken"))
                && !string.IsNullOrWhiteSpace(Json.Str(oauth, "refreshToken"))
                && Json.Num(oauth, "expiresAt") is > 0
                && Json.Arr(oauth, "scopes") is { } scopes
                && scopes.EnumerateArray().Any(scope =>
                    scope.ValueKind == JsonValueKind.String && scope.GetString() == ClaudeCredentialReader.ProfileScope);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
