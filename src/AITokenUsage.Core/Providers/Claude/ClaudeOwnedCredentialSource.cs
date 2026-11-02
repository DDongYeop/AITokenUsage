using System.Security.Cryptography;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// The widget-owned login of an extra Claude account, kept in <see cref="ISecretStore"/> (spec §2.1, §2.3).
/// No other program uses this refresh-token chain, so the widget renews it itself: a token within
/// <see cref="ClaudeUsageProvider.RefreshMargin"/> of expiry is renewed, and the new tokens are saved before they are used.
/// Reads, renewals and <see cref="ReplaceAsync"/> run one at a time under one lock.
/// </summary>
public sealed class ClaudeOwnedCredentialSource : IClaudeCredentialSource
{
    private readonly ISecretStore _secrets;
    private readonly string _secretName;
    private readonly ClaudeTokenRenewer _renewer;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _loaded;
    private ClaudeOwnedCredentials? _current;

    // Set when the login is missing or the token endpoint refused it; only ReplaceAsync clears it.
    private bool _reauthRequired;

    // The access token obtained by renewing during the most recent ReadAsync, so a 401 right after does not renew twice.
    private string? _renewedInLastRead;

    private volatile string? _email;

    public ClaudeOwnedCredentialSource(ISecretStore secrets, string secretName, ClaudeTokenRenewer renewer, TimeProvider time, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(renewer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _secrets = secrets;
        _secretName = secretName;
        _renewer = renewer;
        _time = time;
        _log = log;
    }

    public IReadOnlyList<string> WatchPaths => [];

    public bool RenewsItself => true;

    /// <summary>The account email after the login was first loaded (for the settings card). Null before that.</summary>
    public string? Email => _email;

    public async Task<ClaudeCredentialReadResult> ReadAsync(string userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userAgent);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _renewedInLastRead = null;
            if (!TryEnsureLoaded())
            {
                // The secret store could not be read right now: try again on the next read.
                return new ClaudeCredentialReadResult(ClaudeCredentialState.RenewFailed);
            }

            if (_reauthRequired || _current is null)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.ReauthRequired);
            }

            if (_current.ExpiresAt - _time.GetUtcNow() < ClaudeUsageProvider.RefreshMargin)
            {
                ClaudeCredentialReadResult renewed = await RenewLockedAsync(userAgent, ct).ConfigureAwait(false);
                if (renewed.State == ClaudeCredentialState.Ok)
                {
                    _renewedInLastRead = _current.AccessToken;
                }
                else if (renewed.State == ClaudeCredentialState.RenewFailed && _current.ExpiresAt > _time.GetUtcNow())
                {
                    // A transient failure inside the refresh margin: the current token still works, so use it; the next
                    // read tries to renew again.
                    return new ClaudeCredentialReadResult(ClaudeCredentialState.Ok, _current.ToCredentials());
                }

                return renewed;
            }

            return new ClaudeCredentialReadResult(ClaudeCredentialState.Ok, _current.ToCredentials());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ClaudeCredentialReadResult?> RecoverAsync(ClaudeCredentials rejected, string userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        ArgumentNullException.ThrowIfNull(userAgent);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryEnsureLoaded())
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.RenewFailed);
            }

            if (_reauthRequired || _current is null)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.ReauthRequired);
            }

            if (string.Equals(_renewedInLastRead, rejected.AccessToken, StringComparison.Ordinal))
            {
                return null;
            }

            if (!string.Equals(_current.AccessToken, rejected.AccessToken, StringComparison.Ordinal))
            {
                // Replaced by a new sign-in while the request was in flight.
                return new ClaudeCredentialReadResult(ClaudeCredentialState.Ok, _current.ToCredentials());
            }

            return await RenewLockedAsync(userAgent, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sign in again: saves the new login, uses it from now on and clears the sign-in-required state.
    /// Throws (CryptographicException, IOException, UnauthorizedAccessException) when the secret cannot be saved;
    /// the login in memory and the sign-in-required state are then left unchanged.
    /// </summary>
    public async Task ReplaceAsync(ClaudeOwnedCredentials credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                _secrets.Save(_secretName, credentials.ToJson());
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                // Only the exception type is logged; the caller shows the failure.
                _log.Warn($"Claude account: new sign-in could not be saved ({ex.GetType().Name}).");
                throw;
            }

            SetCurrent(credentials);
            _loaded = true;
            _reauthRequired = false;
            _renewedInLastRead = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the saved login on first use. A missing, undecryptable or broken secret means "sign in again" (sticky).
    /// False when the store threw (a transient I/O or access error): nothing is cached, so the next call tries again.
    /// </summary>
    private bool TryEnsureLoaded()
    {
        if (_loaded)
        {
            return true;
        }

        string? json;
        try
        {
            json = _secrets.Load(_secretName);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Claude account: saved login could not be read ({ex.GetType().Name}); will retry.");
            return false;
        }

        _loaded = true;
        ClaudeOwnedCredentials? login = ClaudeOwnedCredentials.FromJson(json);
        if (login is null)
        {
            _log.Info("Claude account: no usable saved login; sign-in required.");
            _reauthRequired = true;
            return true;
        }

        SetCurrent(login);
        return true;
    }

    /// <summary>Renews <see cref="_current"/>. Caller holds the lock and has checked that it is loaded.</summary>
    private async Task<ClaudeCredentialReadResult> RenewLockedAsync(string userAgent, CancellationToken ct)
    {
        ClaudeRenewResult result = await _renewer.RenewAsync(_current!, userAgent, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ClaudeRenewOutcome.Renewed when result.Credentials is { } renewed:
                // The old refresh token is spent now: keep the new one in memory first, then save it.
                // Both happen before the usage request, which is sent only after this method returns.
                SetCurrent(renewed);
                Save(renewed, "renewed login");
                return new ClaudeCredentialReadResult(ClaudeCredentialState.Ok, renewed.ToCredentials());

            case ClaudeRenewOutcome.ReauthRequired:
                _reauthRequired = true;
                return new ClaudeCredentialReadResult(ClaudeCredentialState.ReauthRequired);

            default:
                return new ClaudeCredentialReadResult(ClaudeCredentialState.RenewFailed);
        }
    }

    private void Save(ClaudeOwnedCredentials credentials, string what)
    {
        try
        {
            _secrets.Save(_secretName, credentials.ToJson());
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Keep going with the tokens in memory; only the exception type is logged.
            _log.Warn($"Claude account: {what} could not be saved ({ex.GetType().Name}).");
        }
    }

    private void SetCurrent(ClaudeOwnedCredentials credentials)
    {
        _current = credentials;
        _email = credentials.Email;
    }
}
