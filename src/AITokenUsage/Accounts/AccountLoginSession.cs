using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;

namespace AITokenUsage.Accounts;

/// <summary>Why an account sign-in ended without a result.</summary>
public enum AccountLoginFailure
{
    /// <summary>The sign-in folder path cannot be passed to the terminal (blank, a control character, not a full path).</summary>
    InvalidPath,

    /// <summary>The sign-in folder could not be prepared (an I/O or permission error).</summary>
    FolderFailed,

    /// <summary>No terminal could be started.</summary>
    TerminalFailed,

    /// <summary>The sign-in did not finish within <see cref="AccountLoginSession.Timeout"/>.</summary>
    TimedOut,

    /// <summary>The finished sign-in could not be put in place (for Claude: moved into the account folder).</summary>
    SaveFailed,
}

/// <summary>
/// One widget-only sign-in of an extra Claude/Codex account (spec §2.2, §3): prepares the folder, runs the CLI login in a
/// terminal and polls the folder until the login appears. A Claude sign-in folder is then moved into place as the account
/// folder (Claude spec 2026-11-16 §2.4). A new account is added to the settings (shown after a restart); after a re-login
/// the running provider reads the new login from its folder.
/// Created and started on the UI thread; <see cref="Completed"/> and <see cref="Failed"/> are raised there.
/// After <see cref="Cancel"/> (or <see cref="Dispose"/>) no event is raised, unless the new account was already added
/// (<see cref="HasAdded"/>): then <see cref="Completed"/> still follows. File work runs on the thread pool.
/// Never logs tokens or file contents.
/// </summary>
public sealed class AccountLoginSession : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>How long a finished Claude sign-in waits for .claude.json to name the account before it moves anyway.</summary>
    public static readonly TimeSpan AccountFileWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tries to move a finished Claude sign-in into place, <see cref="PollInterval"/> apart: Windows refuses while Claude
    /// Code (or a virus scanner) still has a file in the folder open.
    /// </summary>
    private const int MoveAttempts = 10;

    private const string CodexAuthFileName = "auth.json";
    private const string CodexConfigFileName = "config.toml";
    private const string CodexFileStoreConfig = "cli_auth_credentials_store = \"file\"\n";

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

    /// <summary>
    /// True once a new account is in the settings. From then on the sign-in counts as done: <see cref="Completed"/> is
    /// raised even after <see cref="Cancel"/>, and a page cancelling now should show the "added" result.
    /// </summary>
    public bool HasAdded { get; private set; }

    /// <summary>The login is in place (and a new account added to the settings).</summary>
    public event EventHandler? Completed;

    public event EventHandler<AccountLoginFailure>? Failed;

    private IAppLog Log => _host.Log;

    private IFileSystem Files => _host.ProviderContext.Files;

    private TimeProvider Time => _host.ProviderContext.Time;

    /// <summary>The Claude account's CLAUDE_CONFIG_DIR: where a finished sign-in folder is moved.</summary>
    private string ClaudeAccountDirectory => Path.Combine(AppPaths.AccountsDirectory, AccountStorage.ClaudeHomeName(AccountId));

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

    /// <summary>Stops polling and cleans up (the Claude sign-in folder, a new account's folder). No event follows. UI thread.</summary>
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

        // A new account already in the settings stays added: a late Cancel does not hide that (the restart notice must show).
        if (_cancelled && !HasAdded)
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

    /// <summary>Null when the login is in place; otherwise why not. Throws OperationCanceledException after Cancel.</summary>
    private async Task<AccountLoginFailure?> LoginAsync(CancellationToken ct)
    {
        string directory = _kind == ProviderIds.Claude
            ? Path.Combine(AppPaths.LoginDirectory, AccountStorage.ClaudeHomeName(AccountId))
            : Path.Combine(AppPaths.AccountsDirectory, AccountStorage.CodexHomeName(AccountId));

        AccountLoginCommand? command = AccountLoginCommand.Create(_kind, directory);
        if (command is null)
        {
            Log.Warn($"account sign-in: the folder path cannot be passed to the terminal ({AccountId})");
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
            return AccountLoginFailure.FolderFailed;
        }

        ct.ThrowIfCancellationRequested();
        if (!await TerminalLauncher.LaunchAccountLogin(_kind, command, Log))
        {
            return AccountLoginFailure.TerminalFailed;
        }

        long startedAt = Time.GetTimestamp();
        while (true)
        {
            await Task.Delay(PollInterval, Time, ct);

            if (_kind == ProviderIds.Claude)
            {
                if (await Task.Run(() => IsClaudeSignedIn(directory), ct))
                {
                    // Claude Code writes .claude.json (the account email) right after the credentials and then exits:
                    // give it a moment, the folder cannot be moved while its files are open.
                    await Task.Delay(SettleDelay, Time, ct);
                    await WaitForClaudeAccountAsync(directory, ct);
                    return await InstallClaudeLoginAsync(directory, ct);
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

    /// <summary>
    /// Thread pool. Claude: an empty sign-in folder. Codex: the account folder (a new account starts with only a
    /// config.toml that keeps the login in auth.json); returns the
    /// access token already in it so "sign in again" waits for a different one.
    /// </summary>
    private string? PrepareDirectory(string directory)
    {
        if (_kind == ProviderIds.Claude)
        {
            // A ready folder left by an earlier sign-in would be taken for this one's (AccountFolders.MoveIntoPlace).
            AccountFolders.Delete(directory + AccountStorage.ReadySuffix);
        }

        if (_kind == ProviderIds.Claude || IsNew)
        {
            AccountFolders.Delete(directory);
        }

        Directory.CreateDirectory(directory);
        if (_kind != ProviderIds.Codex)
        {
            return null;
        }

        if (IsNew)
        {
            // Keep the login in auth.json even when the user's Codex is set to the OS keyring: the widget reads the file,
            // and a keyring entry would not be tied to this folder (spec §3). Written only into this new, app-owned folder.
            string config = Path.Combine(directory, CodexConfigFileName);
            if (!File.Exists(config))
            {
                File.WriteAllText(config, CodexFileStoreConfig);
            }

            return null;
        }

        string? text = Files.ReadAllTextShared(Path.Combine(directory, CodexAuthFileName));
        return text is null ? null : CodexAuthReader.TryParse(text)?.AccessToken;
    }

    /// <summary>Thread pool. True once the completion condition holds; false while it does not or the file is busy.</summary>
    private bool IsClaudeSignedIn(string directory)
    {
        try
        {
            return ClaudeLoginDetector.IsSignedIn(Files.ReadAllTextShared(Path.Combine(directory, ClaudePaths.CredentialsFileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits up to <see cref="AccountFileWait"/> for the sign-in folder's .claude.json to name the account, so the email moves
    /// along with the login (Claude Code writes it right after the credentials). Moves on without it after that.
    /// </summary>
    private async Task WaitForClaudeAccountAsync(string directory, CancellationToken ct)
    {
        long startedAt = Time.GetTimestamp();
        while (!await Task.Run(() => HasClaudeAccount(directory), ct))
        {
            if (Time.GetElapsedTime(startedAt) >= AccountFileWait)
            {
                Log.Info("account sign-in: .claude.json does not name the account yet; moving the login anyway");
                return;
            }

            await Task.Delay(PollInterval, Time, ct);
        }
    }

    /// <summary>Thread pool. False while .claude.json is missing, partly written or busy.</summary>
    private bool HasClaudeAccount(string directory)
    {
        try
        {
            return ClaudeLoginDetector.HasAccount(
                Files.ReadAllTextShared(Path.Combine(directory, ClaudeDelegatedCredentialSource.AccountFileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

    /// <summary>
    /// Moves the finished sign-in folder into place as the account folder (spec 2026-11-16 §2.4); the files in it stay as
    /// Claude Code wrote them. Each attempt resumes where the last one stopped (<see cref="AccountFolders.MoveIntoPlace"/>).
    /// New account: then adds the settings entry. Again: the running provider's account lock keeps a renewal from running
    /// in the old folder meanwhile, and the provider reads the new login from the folder.
    /// </summary>
    private async Task<AccountLoginFailure?> InstallClaudeLoginAsync(string directory, CancellationToken ct)
    {
        string accountDirectory = ClaudeAccountDirectory;
        bool MoveIntoPlace() => AccountFolders.MoveIntoPlace(
            directory, directory + AccountStorage.ReadySuffix, accountDirectory, directory + AccountStorage.AsideSuffix);

        // A new account has no provider until the restart; neither has an account added since the app started.
        var source = _host.Providers.OfType<ClaudeUsageProvider>()
            .FirstOrDefault(p => string.Equals(p.Id, AccountId, StringComparison.Ordinal))?.Source as ClaudeDelegatedCredentialSource;

        bool oldFolderDeleted;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                oldFolderDeleted = await Task.Run(
                    () => source is null ? Task.FromResult(MoveIntoPlace()) : source.RunExclusiveAsync(MoveIntoPlace, ct),
                    CancellationToken.None);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MoveAttempts)
                {
                    Log.Warn($"account sign-in: moving the login into place failed ({ex.GetType().Name})");
                    return AccountLoginFailure.SaveFailed;
                }

                await Task.Delay(PollInterval, Time, ct);
            }
        }

        if (!oldFolderDeleted)
        {
            Log.Warn("account sign-in: the replaced login folder could not be deleted yet; the clean-up tries again");
        }

        if (!IsNew)
        {
            // The new login is in place: a cancel now would not undo it.
            RefreshUsage();
            return null;
        }

        if (ct.IsCancellationRequested)
        {
            // Cancelled while moving: the account is not added, so its folder goes too (the startup sweep would also catch it).
            await Task.Run(() => TryDeleteAccountFolder(accountDirectory), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
        }

        AddToSettings();
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
        HasAdded = true;
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
    /// Claude: deletes the sign-in folder and its ready copy (both gone once the login was moved into place) and puts back
    /// an old account folder that a failed replacement left aside (<see cref="AccountFolders.CleanUp"/>). Codex: deletes a
    /// new account's folder unless the account was added. An existing account's folder is its store and is never deleted
    /// here. Failures are logged only (the startup sweep removes leftovers and puts an old account folder back).
    /// </summary>
    private async Task CleanUpAsync(bool completed)
    {
        if (_directory is not string directory)
        {
            return;
        }

        // An added Codex account's folder is its store, whatever else happened.
        bool delete = _kind == ProviderIds.Claude || (IsNew && !completed && !HasAdded);
        if (!delete)
        {
            return;
        }

        try
        {
            if (_kind == ProviderIds.Claude)
            {
                string accountDirectory = ClaudeAccountDirectory;
                await Task.Run(() => AccountFolders.CleanUp(
                    directory, directory + AccountStorage.ReadySuffix, accountDirectory, directory + AccountStorage.AsideSuffix));
            }
            else
            {
                await Task.Run(() => AccountFolders.Delete(directory));
            }
        }
        catch (Exception ex)
        {
            // Any failure (I/O, permission, a bad path): log the type only; the startup sweep removes leftovers.
            Log.Warn($"account sign-in: cleaning up the sign-in folder failed ({ex.GetType().Name})");
        }
    }

    private void TryDeleteAccountFolder(string directory)
    {
        try
        {
            AccountFolders.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"account sign-in: deleting the unused account folder failed ({ex.GetType().Name})");
        }
    }
}
