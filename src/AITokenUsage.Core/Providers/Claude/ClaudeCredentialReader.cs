using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Reads Claude Code's .credentials.json with shared access, retrying parse failures
/// (Claude Code rewrites the file in place with O_TRUNC, so a read can see a partial file).
/// A parse failure is never reported as a logout.
/// </summary>
public sealed class ClaudeCredentialReader
{
    public const string ProfileScope = "user:profile";

    private const long MaxEpochMilliseconds = 253_402_300_799_999; // 9999-12-31T23:59:59.999Z

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
    ];

    private readonly IFileSystem _files;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ClaudeCredentialReader(IFileSystem files, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _files = files;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public async Task<ClaudeCredentialReadResult> ReadAsync(string path, CancellationToken ct)
    {
        if (!_files.FileExists(path))
        {
            return new ClaudeCredentialReadResult(ClaudeCredentialState.NotInstalled);
        }

        for (int attempt = 0; ; attempt++)
        {
            string? text;
            try
            {
                text = _files.ReadAllTextShared(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transient lock (e.g. antivirus scan): retry exactly like a parse failure.
                text = string.Empty;
            }

            if (text is null)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.NotInstalled);
            }

            if (TryParse(text, out ClaudeCredentialReadResult result))
            {
                return result;
            }

            if (attempt >= RetryDelays.Length)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.Unreadable);
            }

            await _delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    /// <summary>False only when the text is not a complete JSON object (caller retries).</summary>
    public static bool TryParse(string text, out ClaudeCredentialReadResult result)
    {
        result = new ClaudeCredentialReadResult(ClaudeCredentialState.Unreadable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            result = Interpret(root);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ClaudeCredentialReadResult Interpret(JsonElement root)
    {
        if (Json.Obj(root, "claudeAiOauth") is not { } oauth)
        {
            return new ClaudeCredentialReadResult(ClaudeCredentialState.SignedOut);
        }

        string? accessToken = Json.Str(oauth, "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new ClaudeCredentialReadResult(ClaudeCredentialState.SignedOut);
        }

        DateTimeOffset? expiresAt = null;
        if (Json.Num(oauth, "expiresAt") is { } expiresAtMs)
        {
            // Claude Code blanks the login to expiresAt 0 after a failed refresh (#91571).
            if (expiresAtMs <= 0)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.SignedOut);
            }

            if (expiresAtMs <= MaxEpochMilliseconds)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMs);
            }
        }

        IReadOnlyList<string>? scopes = null;
        if (Json.Arr(oauth, "scopes") is { } scopeArray)
        {
            scopes = scopeArray.EnumerateArray()
                .Where(scope => scope.ValueKind == JsonValueKind.String)
                .Select(scope => scope.GetString()!)
                .ToList();

            if (!scopes.Contains(ProfileScope, StringComparer.Ordinal))
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.MissingProfileScope);
            }
        }

        var credentials = new ClaudeCredentials(
            accessToken,
            expiresAt,
            scopes,
            Json.Str(oauth, "subscriptionType"),
            Json.Str(oauth, "rateLimitTier"));
        return new ClaudeCredentialReadResult(ClaudeCredentialState.Ok, credentials);
    }
}
