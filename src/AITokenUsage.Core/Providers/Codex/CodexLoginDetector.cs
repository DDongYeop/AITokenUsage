namespace AITokenUsage.Core.Providers.Codex;

/// <summary>Decides from an auth.json read whether `codex login` finished (used while adding a Codex account).</summary>
public static class CodexLoginDetector
{
    /// <summary>ChatGPT 로그인이고 access_token이 있으며, previousAccessToken과 다르면 true.</summary>
    public static bool IsSignedIn(CodexAuth? auth, string? previousAccessToken)
        => auth is { Mode: CodexAuthMode.ChatGpt, HasAccessToken: true }
           && !string.Equals(auth.AccessToken, previousAccessToken, StringComparison.Ordinal);
}
