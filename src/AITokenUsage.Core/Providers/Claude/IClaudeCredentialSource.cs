namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Where <see cref="ClaudeUsageProvider"/> gets the login for a usage request: Claude Code's own file for the built-in
/// panel, Claude Code's file in the account folder for an extra account (spec 2026-11-16 §2.2).
/// </summary>
public interface IClaudeCredentialSource
{
    /// <summary>Files whose change should trigger a refresh; empty when nothing is watched.</summary>
    IReadOnlyList<string> WatchPaths { get; }

    /// <summary>
    /// True: the source has a token close to expiry renewed before handing it out (an extra account, through Claude Code),
    /// so the provider uses what it gets and a rejected token means "sign in again". False: waits for the CLI to renew it.
    /// </summary>
    bool RenewsItself { get; }

    /// <summary>The login for the next usage request.</summary>
    Task<ClaudeCredentialReadResult> ReadAsync(CancellationToken ct);

    /// <summary>
    /// Called after <paramref name="rejected"/> got HTTP 401. Re-reads the file: an Ok result when the token changed,
    /// else null. CLI: a failed read is returned as it is. Extra account: null for a failed read too.
    /// </summary>
    Task<ClaudeCredentialReadResult?> RecoverAsync(ClaudeCredentials rejected, CancellationToken ct);
}
