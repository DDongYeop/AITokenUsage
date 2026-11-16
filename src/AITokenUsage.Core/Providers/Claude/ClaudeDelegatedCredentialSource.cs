using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// The login of an extra Claude account (spec 2026-11-16 §2.2): Claude Code's own .credentials.json in the account folder,
/// which is that account's CLAUDE_CONFIG_DIR. The widget only reads the folder. A token that has expired or is within
/// <see cref="ClaudeUsageProvider.RefreshMargin"/> of expiry is handed to Claude Code to renew (<see cref="ClaudeCliRenewer"/>),
/// at most once per <see cref="RenewalGap"/>; <see cref="FailuresBeforeReauth"/> renewals in a row that leave an expired
/// token mean "sign in again", and from then on the command runs at most once per <see cref="ReauthRenewalGap"/>.
/// Renewals and <see cref="RunExclusiveAsync"/> run one at a time under the account lock.
/// </summary>
public sealed class ClaudeDelegatedCredentialSource : IClaudeCredentialSource
{
    /// <summary>The file in the account folder that names the account (oauthAccount.emailAddress).</summary>
    public const string AccountFileName = ".claude.json";

    public const int FailuresBeforeReauth = 3;

    public static readonly TimeSpan RenewalGap = TimeSpan.FromMinutes(2);

    /// <summary>The gap once sign-in is required: a later renewal may still work, but it is unlikely.</summary>
    public static readonly TimeSpan ReauthRenewalGap = TimeSpan.FromMinutes(30);

    private readonly string _directory;
    private readonly string _credentialsPath;
    private readonly string _accountPath;
    private readonly IFileSystem _files;
    private readonly ClaudeCredentialReader _reader;
    private readonly ClaudeCliRenewer _renewer;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // When the last renewal attempt ended, as TimeProvider.GetTimestamp (unaffected by changes to the wall clock). Under _gate.
    private long? _lastRenewal;

    // Renewals in a row that left an expired token; cleared whenever a fresh token is read (and with it the longer gap).
    private int _failures;

    private volatile string? _email;

    /// <param name="accountDirectory">The account folder (CLAUDE_CONFIG_DIR), e.g. %LOCALAPPDATA%\AITokenUsage\accounts\claude-k3x9q2.</param>
    /// <param name="files">Reads the folder with shared access.</param>
    /// <param name="renewer">Runs Claude Code for the folder.</param>
    /// <param name="time">The clock for expiry and the renewal gap.</param>
    public ClaudeDelegatedCredentialSource(string accountDirectory, IFileSystem files, ClaudeCliRenewer renewer, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDirectory);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(renewer);
        ArgumentNullException.ThrowIfNull(time);
        _directory = accountDirectory;
        _credentialsPath = Path.Combine(accountDirectory, ClaudePaths.CredentialsFileName);
        _accountPath = Path.Combine(accountDirectory, AccountFileName);
        _files = files;
        _reader = new ClaudeCredentialReader(files);
        _renewer = renewer;
        _time = time;
        WatchPaths = [_credentialsPath];
    }

    public IReadOnlyList<string> WatchPaths { get; }

    public bool RenewsItself => true;

    /// <summary>The account email from the folder's .claude.json at the last read (for the settings card). Null before that.</summary>
    public string? Email => _email;

    public async Task<ClaudeCredentialReadResult> ReadAsync(CancellationToken ct)
    {
        ReadEmail();

        ClaudeCredentialReadResult read = await _reader.ReadAsync(_credentialsPath, ct).ConfigureAwait(false);
        if (Unusable(read) is { } unusable)
        {
            return unusable;
        }

        if (TakeFresh(read))
        {
            return read;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another read may have had it renewed while this one waited for the lock.
            read = await _reader.ReadAsync(_credentialsPath, ct).ConfigureAwait(false);
            if (Unusable(read) is { } unusableNow)
            {
                return unusableNow;
            }

            if (TakeFresh(read))
            {
                return read;
            }

            TimeSpan gap = Volatile.Read(ref _failures) >= FailuresBeforeReauth ? ReauthRenewalGap : RenewalGap;
            if (_lastRenewal is { } last && _time.GetElapsedTime(last) < gap)
            {
                // Claude Code ran a moment ago: answer as that attempt did, without counting another failure.
                return NotRenewed(read, countFailure: false);
            }

            ClaudeCredentialReadResult? after = await _renewer.RenewAsync(_directory, ct).ConfigureAwait(false);
            _lastRenewal = _time.GetTimestamp();
            if (after is not null && TakeFresh(after))
            {
                return after;
            }

            return NotRenewed(after is { State: ClaudeCredentialState.Ok, Credentials: not null } ? after : read, countFailure: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Claude Code renews only from 5 minutes before expiry, so a rejected token cannot be renewed on demand: it is
    /// replaced only when the file changed meanwhile (renewed, or signed in again). Null otherwise.
    /// </summary>
    public async Task<ClaudeCredentialReadResult?> RecoverAsync(ClaudeCredentials rejected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejected);

        ClaudeCredentialReadResult reread = await _reader.ReadAsync(_credentialsPath, ct).ConfigureAwait(false);
        return reread is { State: ClaudeCredentialState.Ok, Credentials: { } credentials }
            && !string.Equals(credentials.AccessToken, rejected.AccessToken, StringComparison.Ordinal)
                ? reread
                : null;
    }

    /// <summary>
    /// Runs <paramref name="work"/> while no renewal runs for this account (spec §2.4: signing in again replaces the
    /// folder). The renewal command runs inside the folder, and Windows does not rename a folder a process runs in.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return work();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A missing file or a login without a token means "sign in again"; any other failed read passes through. Null for Ok.</summary>
    private static ClaudeCredentialReadResult? Unusable(ClaudeCredentialReadResult read) => read.State switch
    {
        ClaudeCredentialState.Ok when read.Credentials is not null => null,
        ClaudeCredentialState.NotInstalled or ClaudeCredentialState.SignedOut => new ClaudeCredentialReadResult(ClaudeCredentialState.ReauthRequired),
        _ => read,
    };

    /// <summary>True when the token needs no renewal; that also clears the failure count.</summary>
    private bool TakeFresh(ClaudeCredentialReadResult read)
    {
        if (!ClaudeCliRenewer.IsFresh(read, _time.GetUtcNow()))
        {
            return false;
        }

        Interlocked.Exchange(ref _failures, 0);
        return true;
    }

    /// <summary>A token that has not expired yet is still used (the next read tries again); an expired one is a failure.</summary>
    private ClaudeCredentialReadResult NotRenewed(ClaudeCredentialReadResult current, bool countFailure)
    {
        if (current.Credentials!.ExpiresAt is { } expiresAt && expiresAt > _time.GetUtcNow())
        {
            return current;
        }

        int failures = countFailure ? Interlocked.Increment(ref _failures) : Volatile.Read(ref _failures);
        return new ClaudeCredentialReadResult(
            failures >= FailuresBeforeReauth ? ClaudeCredentialState.ReauthRequired : ClaudeCredentialState.RenewFailed);
    }

    /// <summary>A missing file clears the email; a file Claude Code is still writing keeps the last one.</summary>
    private void ReadEmail()
    {
        string? text;
        try
        {
            text = _files.ReadAllTextShared(_accountPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (text is null)
        {
            _email = null;
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                _email = Json.Obj(document.RootElement, "oauthAccount") is { } account ? Json.Str(account, "emailAddress") : null;
            }
        }
        catch (JsonException)
        {
            // Partly written: keep the last email.
        }
    }
}
