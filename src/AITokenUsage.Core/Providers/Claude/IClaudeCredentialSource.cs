namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Where <see cref="ClaudeUsageProvider"/> gets the login for a usage request (spec §2.4):
/// Claude Code's own file for the built-in panel, the widget's secret store for an extra account.
/// </summary>
public interface IClaudeCredentialSource
{
    /// <summary>Files whose change should trigger a refresh; empty when nothing is watched.</summary>
    IReadOnlyList<string> WatchPaths { get; }

    /// <summary>True: renews a token close to expiry itself (widget-owned). False: waits for the CLI to renew it.</summary>
    bool RenewsItself { get; }

    /// <summary>The login for the next usage request. <paramref name="userAgent"/> is "claude-code/&lt;version&gt;".</summary>
    Task<ClaudeCredentialReadResult> ReadAsync(string userAgent, CancellationToken ct);

    /// <summary>
    /// Called after <paramref name="rejected"/> got HTTP 401. Returns an Ok result to retry with, a failed-state result,
    /// or null when there is nothing newer.
    /// CLI: re-reads the file; Ok when the token changed, else null.
    /// Widget-owned: null when the most recent ReadAsync already renewed this token, else one forced renewal.
    /// </summary>
    Task<ClaudeCredentialReadResult?> RecoverAsync(ClaudeCredentials rejected, string userAgent, CancellationToken ct);
}
