using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Claude Code's own .credentials.json, read-only (the built-in Claude panel). The widget never refreshes or writes
/// this login: Claude Code rotates it under its own lock and the file watcher re-queries when it does.
/// </summary>
public sealed class ClaudeCliCredentialSource : IClaudeCredentialSource
{
    private readonly ClaudeCredentialReader _reader;
    private readonly string _credentialsPath;

    public ClaudeCliCredentialSource(IFileSystem files, string credentialsPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(credentialsPath);
        _reader = new ClaudeCredentialReader(files);
        _credentialsPath = credentialsPath;
        WatchPaths = [credentialsPath];
    }

    public IReadOnlyList<string> WatchPaths { get; }

    public bool RenewsItself => false;

    public Task<ClaudeCredentialReadResult> ReadAsync(string userAgent, CancellationToken ct) =>
        _reader.ReadAsync(_credentialsPath, ct);

    public async Task<ClaudeCredentialReadResult?> RecoverAsync(ClaudeCredentials rejected, string userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejected);

        // Claude Code may have renewed the login while the request was in flight.
        ClaudeCredentialReadResult reread = await _reader.ReadAsync(_credentialsPath, ct).ConfigureAwait(false);
        if (reread.State != ClaudeCredentialState.Ok || reread.Credentials is null)
        {
            return reread;
        }

        return string.Equals(reread.Credentials.AccessToken, rejected.AccessToken, StringComparison.Ordinal) ? null : reread;
    }
}
