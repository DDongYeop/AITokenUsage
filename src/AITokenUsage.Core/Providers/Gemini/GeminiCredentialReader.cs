using System.ComponentModel;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// Reads Gemini CLI OAuth credentials: oauth_creds.json first, then the Credential Manager
/// entry written by keytar in encrypted-storage mode. Read-only: never writes CLI files.
/// </summary>
public sealed class GeminiCredentialReader(IFileSystem files, ICredentialStore credentialStore, GeminiPaths paths)
{
    public const string CredentialTarget = "gemini-cli-oauth/main-account";

    // Real-time pauses between re-reads while the CLI is replacing the file (temp file + rename).
    // Deliberately not tied to TimeProvider: they are tiny and must not stall under a fake clock.
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)];

    public async Task<GeminiCredentialResult> ReadAsync(CancellationToken ct)
    {
        var fromFile = await ReadOAuthFileAsync(ct).ConfigureAwait(false);
        if (fromFile.Status == GeminiCredentialStatus.Found)
        {
            return fromFile;
        }

        var fromCredentialManager = ReadCredentialManager();
        if (fromCredentialManager is not null)
        {
            return new GeminiCredentialResult(GeminiCredentialStatus.Found, fromCredentialManager);
        }

        return fromFile;
    }

    public static GeminiCredentials? ParseOAuthFile(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new GeminiCredentials(
                Json.Str(root, "access_token"),
                Json.Str(root, "refresh_token"),
                FromEpochMilliseconds(Json.Num(root, "expiry_date")),
                Json.Str(root, "id_token"),
                GeminiCredentialSource.OAuthFile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static GeminiCredentials? ParseCredentialManagerBlob(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Json.Obj(root, "token") is not { } token)
            {
                return null;
            }

            return new GeminiCredentials(
                Json.Str(token, "accessToken"),
                Json.Str(token, "refreshToken"),
                FromEpochMilliseconds(Json.Num(token, "expiresAt")),
                IdToken: null,
                GeminiCredentialSource.CredentialManager);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<GeminiCredentialResult> ReadOAuthFileAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = files.ReadAllTextShared(paths.OAuthCredsPath);
                if (text is null)
                {
                    return new GeminiCredentialResult(GeminiCredentialStatus.NotFound, null);
                }

                var parsed = ParseOAuthFile(text);
                if (parsed is not null)
                {
                    return new GeminiCredentialResult(GeminiCredentialStatus.Found, parsed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transient sharing/rename race: retry below.
            }

            if (attempt >= RetryDelays.Length)
            {
                // A parse failure is NOT a sign-out; the provider reports Error and keeps the last good value.
                return new GeminiCredentialResult(GeminiCredentialStatus.Unreadable, null);
            }

            await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    private GeminiCredentials? ReadCredentialManager()
    {
        string? blob;
        try
        {
            blob = credentialStore.ReadGeneric(CredentialTarget);
        }
        catch (Win32Exception)
        {
            return null;
        }

        return blob is null ? null : ParseCredentialManagerBlob(blob);
    }

    private static DateTimeOffset? FromEpochMilliseconds(double? milliseconds)
    {
        const double maxMilliseconds = 253_402_300_799_999; // 9999-12-31T23:59:59.999Z
        return milliseconds is > 0 and < maxMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds.Value)
            : null;
    }
}
