using System.Security.Cryptography;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;

namespace AITokenUsage.Accounts;

/// <summary>Why an account sign-in ended without a result.</summary>
public enum AccountLoginFailure
{
    /// <summary>The sign-in folder path cannot go into the terminal command, or the folder could not be prepared.</summary>
    InvalidPath,

    /// <summary>No terminal could be started.</summary>
    TerminalFailed,

    /// <summary>The sign-in did not finish within <see cref="AccountLoginSession.Timeout"/>.</summary>
    TimedOut,

    /// <summary>The finished sign-in could not be read or saved.</summary>
    SaveFailed,
}

/// <summary>
/// One widget-only sign-in of an extra Claude/Codex account (spec §2.2, §3): prepares the folder, runs the CLI login in a
/// terminal and polls the folder until the login appears. A new account is then added to the settings (shown after a
/// restart); a re-login hands the new login to the running provider.
/// Created and started on the UI thread; <see cref="Completed"/> and <see cref="Failed"/> are raised there.
/// After <see cref="Cancel"/> (or <see cref="Dispose"/>) no event is raised. File work runs on the thread pool.
/// Never logs tokens or file contents.
/// </summary>
public sealed class AccountLoginSession : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private const string ClaudeCredentialsFileName = ".credentials.json";
    private const string ClaudeAccountFileName = ".claude.json";
    private const string CodexAuthFileName = "auth.json";

    /// <summary>
    /// The previous session's whole run, cleanup included. A new session waits for it so a re-login of the same account
    /// never prepares the folder the previous one is still deleting. UI thread only.
    /// </summary>
    private static Task s_lastRun = Task.CompletedTask;

    private readonly AppHost _host;
    private readonly string _kind;
    private readonly CancellationTokenSource _cts = new();
    private string? _directory;
    private bool _started;
    private bool _cancelled;
    private bool _finished;

    public AccountLoginSession(AppHost host, string accountId, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!AccountIds.IsAccountId(accountId))
        {
            throw new ArgumentException($"Not an account id: '{accountId}'.", nameof(accountId));
        }

        _host = host;
        AccountId = accountId;
        IsNew = isNew;
        _kind = ProviderIds.KindOf(accountId);
    }

    public string AccountId { get; }

    /// <summary>True when the sign-in adds a new account; false for "sign in again".</summary>
    public bool IsNew { get; }

    /// <summary>The login was saved (and a new account added to the settings).</summary>
    public event EventHandler? Completed;

    public event EventHandler<AccountLoginFailure>? Failed;

    private IAppLog Log => _host.Log;

    private IFileSystem Files => _host.ProviderContext.Files;

    private TimeProvider Time => _host.ProviderContext.Time;

    /// <summary>Folder → command → terminal → polling. Once only. UI thread.</summary>
    public void Start()
    {
        _host.Dispatcher.VerifyAccess();
        if (_started)
        {
            throw new InvalidOperationException("The sign-in has already been started.");
        }

        _started = true;
        Task previous = s_lastRun;
        s_lastRun = RunAsync(previous, _cts.Token);
    }

    /// <summary>Stops polling and cleans up (the Claude sign-in folder, a new Codex account's folder). No event follows. UI thread.</summary>
    public void Cancel()
    {
        _host.Dispatcher.VerifyAccess();
        if (_cancelled)
        {
            return;
        }

        _cancelled = true;
        if (!_finished)
        {
            _cts.Cancel();
        }
    }

    public void Dispose() => Cancel();

    /// <summary>Never throws: every failure is logged and turned into an event (or nothing after Cancel).</summary>
    private async Task RunAsync(Task previous, CancellationToken ct)
    {
        try
        {
            await previous;
        }
        catch (Exception ex)
        {
            Log.Warn("account sign-in: the previous sign-in ended with an error", ex);
        }

        string mode = IsNew ? "new" : "again";
        Log.Info($"account sign-in started ({AccountId}, {mode})");

        AccountLoginFailure? failure = null;
        bool completed = false;
        try
        {
            failure = await LoginAsync(ct);
            completed = failure is null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info($"account sign-in cancelled ({AccountId})");
        }
        catch (Exception ex)
        {
            Log.Error($"account sign-in failed unexpectedly ({AccountId})", ex);
            failure = AccountLoginFailure.SaveFailed;
        }

        await CleanUpAsync(completed);
        _finished = true;
        _cts.Dispose();

        if (completed)
        {
            Log.Info($"account sign-in completed ({AccountId}, {mode})");
        }
        else if (failure is AccountLoginFailure reason)
        {
            Log.Warn($"account sign-in ended: {reason} ({AccountId})");
        }

        if (_cancelled)
        {
            return;
        }

        try
        {
            if (completed)
            {
                Completed?.Invoke(this, EventArgs.Empty);
            }
            else if (failure is AccountLoginFailure reason)
            {
                Failed?.Invoke(this, reason);
            }
        }
        catch (Exception ex)
        {
            Log.Error("account sign-in: a result handler failed", ex);
        }
    }

    /// <summary>Null when the login was saved; otherwise why not. Throws OperationCanceledException after Cancel.</summary>
    private async Task<AccountLoginFailure?> LoginAsync(CancellationToken ct)
    {
        string directory = _kind == ProviderIds.Claude
            ? Path.Combine(AppPaths.LoginDirectory, ClaudeLoginFolderName())
            : Path.Combine(AppPaths.AccountsDirectory, AccountStorage.CodexHomeName(AccountId));

        string? command = Path.IsPathFullyQualified(directory) ? AccountLoginCommand.Build(_kind, directory) : null;
        if (command is null)
        {
            Log.Warn($"account sign-in: the folder path cannot be used in a terminal command ({AccountId})");
            return AccountLoginFailure.InvalidPath;
        }

        // From here the folder may exist: clean it up whatever happens next.
        _directory = directory;
        string? previousAccessToken;
        try
        {
            previousAccessToken = await Task.Run(() => PrepareDirectory(directory), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"account sign-in: preparing the folder failed ({ex.GetType().Name})");
            return AccountLoginFailure.InvalidPath;
        }

        ct.ThrowIfCancellationRequested();
        if (!await TerminalLauncher.Launch(command, Log))
        {
            return AccountLoginFailure.TerminalFailed;
        }

        long startedAt = Time.GetTimestamp();
        while (true)
        {
            await Task.Delay(PollInterval, Time, ct);

            if (_kind == ProviderIds.Claude)
            {
                ClaudeOwnedCredentials? login = await Task.Run(() => ReadClaudeLogin(directory), ct);
                if (login is not null)
                {
                    // Claude Code writes .claude.json (email, organization) right after the credentials: give it a moment.
                    await Task.Delay(SettleDelay, Time, ct);
                    ClaudeOwnedCredentials final = await Task.Run(() => ReadClaudeLogin(directory), ct) ?? login;
                    return await SaveClaudeLoginAsync(final, ct);
                }
            }
            else if (await Task.Run(() => IsCodexSignedIn(directory, previousAccessToken), ct))
            {
                ct.ThrowIfCancellationRequested();
                FinishCodexLogin();
                return null;
            }

            if (Time.GetElapsedTime(startedAt) >= Timeout)
            {
                return AccountLoginFailure.TimedOut;
            }
        }
    }

    /// <summary>"claude-&lt;key&gt;" under the login folder (spec §2.2).</summary>
    private string ClaudeLoginFolderName() => "claude-" + AccountIds.KeyOf(AccountId);

    /// <summary>
    /// Thread pool. Claude: an empty sign-in folder. Codex: the account folder (a new account starts empty); returns the
    /// access token already in it so "sign in again" waits for a different one.
    /// </summary>
    private string? PrepareDirectory(string directory)
    {
        if (_kind == ProviderIds.Claude || IsNew)
        {
            DeleteDirectory(directory);
        }

        Directory.CreateDirectory(directory);
        if (_kind != ProviderIds.Codex || IsNew)
        {
            return null;
        }

        string? text = Files.ReadAllTextShared(Path.Combine(directory, CodexAuthFileName));
        return text is null ? null : CodexAuthReader.TryParse(text)?.AccessToken;
    }

    /// <summary>Thread pool. The login once the completion condition holds; null while it does not or the files are busy.</summary>
    private ClaudeOwnedCredentials? ReadClaudeLogin(string directory)
    {
        try
        {
            string? credentials = Files.ReadAllTextShared(Path.Combine(directory, ClaudeCredentialsFileName));
            if (credentials is null)
            {
                return null;
            }

            string? account = Files.ReadAllTextShared(Path.Combine(directory, ClaudeAccountFileName));
            return ClaudeLoginImporter.TryRead(credentials, account);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Thread pool.</summary>
    private bool IsCodexSignedIn(string directory, string? previousAccessToken)
    {
        try
        {
            string? text = Files.ReadAllTextShared(Path.Combine(directory, CodexAuthFileName));
            return text is not null && CodexLoginDetector.IsSignedIn(CodexAuthReader.TryParse(text), previousAccessToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>New account: save the secret, then add the settings entry. Again: hand the login to the running provider.</summary>
    private async Task<AccountLoginFailure?> SaveClaudeLoginAsync(ClaudeOwnedCredentials login, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsNew)
        {
            string secretName = AccountStorage.ClaudeSecretName(AccountId);
            ISecretStore secrets = _host.ProviderContext.Secrets;
            try
            {
                await Task.Run(() => secrets.Save(secretName, login.ToJson()), CancellationToken.None);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"account sign-in: saving the login failed ({ex.GetType().Name})");
                return AccountLoginFailure.SaveFailed;
            }

            if (ct.IsCancellationRequested)
            {
                // Cancelled while saving: the account is not added, so its secret goes too (the startup sweep would also catch it).
                await Task.Run(() => TryDeleteSecret(secrets, secretName), CancellationToken.None);
                ct.ThrowIfCancellationRequested();
            }

            AddToSettings();
            return null;
        }

        ClaudeUsageProvider? provider = _host.Providers.OfType<ClaudeUsageProvider>()
            .FirstOrDefault(p => string.Equals(p.Id, AccountId, StringComparison.Ordinal));
        if (provider?.Source is not ClaudeOwnedCredentialSource source)
        {
            Log.Warn($"account sign-in: no running provider for {AccountId}");
            return AccountLoginFailure.SaveFailed;
        }

        try
        {
            // ReplaceAsync logs the failure itself (type only); a cancel now would not undo a saved login, so it is not passed.
            await Task.Run(() => source.ReplaceAsync(login, CancellationToken.None), CancellationToken.None);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return AccountLoginFailure.SaveFailed;
        }

        RefreshUsage();
        return null;
    }

    /// <summary>UI thread. Codex keeps the folder as the account's store; the running provider also follows its auth.json.</summary>
    private void FinishCodexLogin()
    {
        if (IsNew)
        {
            AddToSettings();
        }
        else
        {
            RefreshUsage();
        }
    }

    /// <summary>UI thread. The panel itself appears after a restart (providers are created at startup).</summary>
    private void AddToSettings()
    {
        AccountSettingsEditor.Add(_host.Settings.Providers, AccountId);
        try
        {
            _host.ApplySettings();
        }
        catch (Exception ex)
        {
            Log.Error("account sign-in: applying settings failed", ex);
        }
    }

    private void RefreshUsage()
    {
        _ = RefreshUsageAsync();

        async Task RefreshUsageAsync()
        {
            try
            {
                await _host.Usage.RefreshNowAsync(AccountId);
            }
            catch (Exception ex)
            {
                Log.Warn($"refresh after signing in again failed ({AccountId})", ex);
            }
        }
    }

    /// <summary>
    /// Deletes the Claude sign-in folder always, and a new Codex account's folder unless the account was added.
    /// An existing Codex account's folder is its store and is never deleted here. Failures are logged only (the startup
    /// sweep removes leftovers).
    /// </summary>
    private async Task CleanUpAsync(bool completed)
    {
        if (_directory is not string directory)
        {
            return;
        }

        bool delete = _kind == ProviderIds.Claude || (IsNew && !completed);
        if (!delete)
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                DeleteDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"account sign-in: deleting the sign-in folder failed ({ex.GetType().Name})");
            }
        });
    }

    /// <summary>A link (junction or symlink) is removed itself, never followed.</summary>
    private static void DeleteDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return;
        }

        if (info.LinkTarget is not null)
        {
            info.Delete();
        }
        else
        {
            info.Delete(recursive: true);
        }
    }

    private void TryDeleteSecret(ISecretStore secrets, string name)
    {
        try
        {
            secrets.Delete(name);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"account sign-in: deleting the unused login failed ({ex.GetType().Name})");
        }
    }
}
