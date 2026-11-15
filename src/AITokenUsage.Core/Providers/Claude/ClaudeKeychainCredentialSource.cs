using System.Security.Cryptography;
using System.Text;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Claude Code's own login on macOS, read-only. Claude Code keeps it in the login keychain (generic password,
/// service "Claude Code-credentials", plus "-&lt;first 8 hex of sha256(config dir)&gt;" when CLAUDE_CONFIG_DIR is set)
/// and only falls back to .credentials.json when the keychain is unavailable, so the keychain is read first and
/// the file second. The keychain cannot be watched; the regular refresh picks up a renewed login.
/// </summary>
public sealed class ClaudeKeychainCredentialSource : IClaudeCredentialSource
{
    public const string ServiceName = "Claude Code-credentials";

    private readonly ICredentialStore _keychain;
    private readonly string _service;
    private readonly ClaudeCliCredentialSource _file;

    public ClaudeKeychainCredentialSource(ICredentialStore keychain, IFileSystem files, IEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(keychain);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(env);
        _keychain = keychain;
        _service = ResolveServiceName(env);
        _file = new ClaudeCliCredentialSource(files, ClaudePaths.ResolveCredentialsPath(env));
    }

    public IReadOnlyList<string> WatchPaths => _file.WatchPaths;

    public bool RenewsItself => false;

    public static string ResolveServiceName(IEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        string? configDir = env.GetVariable(ClaudePaths.ConfigDirVariable);
        if (string.IsNullOrEmpty(configDir))
        {
            return ServiceName;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(configDir.Normalize(NormalizationForm.FormC)));
        return ServiceName + "-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    public async Task<ClaudeCredentialReadResult> ReadAsync(string userAgent, CancellationToken ct)
    {
        string? text = await Task.Run(() => _keychain.ReadGeneric(_service), ct).ConfigureAwait(false);
        if (text is not null && ClaudeCredentialReader.TryParse(text, out ClaudeCredentialReadResult result))
        {
            return result;
        }

        return await _file.ReadAsync(userAgent, ct).ConfigureAwait(false);
    }

    public async Task<ClaudeCredentialReadResult?> RecoverAsync(ClaudeCredentials rejected, string userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejected);

        // Claude Code may have renewed the login while the request was in flight.
        ClaudeCredentialReadResult reread = await ReadAsync(userAgent, ct).ConfigureAwait(false);
        if (reread.State != ClaudeCredentialState.Ok || reread.Credentials is null)
        {
            return reread;
        }

        return string.Equals(reread.Credentials.AccessToken, rejected.AccessToken, StringComparison.Ordinal) ? null : reread;
    }
}
