using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>
/// Codex (ChatGPT plan) usage. Order: apikey → Unsupported; fresh token → wham/usage
/// (401 → re-read auth.json, retry once only if the token changed); otherwise app-server
/// (at most once per 15 min) → rollout logs → status. Never refreshes or writes auth.json.
/// An extra account (<see cref="ForAccount"/>) runs app-server with its own CODEX_HOME, which renews and saves its token.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider
{
    public static readonly TimeSpan AppServerMinimumGap = TimeSpan.FromMinutes(15);

    public static string MessageApiKey => Strings.CodexApiKey;
    public static string MessageNotInstalled => Strings.CodexNotInstalled;
    public static string MessageSignedOut => Strings.CodexSignedOut;
    public static string MessageWaitingForCli => Strings.CodexWaitingForCli;
    public static string MessageUnsupportedLogin => Strings.CodexUnsupportedLogin;
    public static string MessageAuthUnreadable => Strings.CodexAuthUnreadable;
    public static string MessageFailed => Strings.CodexFailed;

    private readonly ProviderContext _ctx;
    private readonly CodexPaths _paths;
    private readonly CodexAuthReader _authReader;
    private readonly CodexUsageClient _client;
    private readonly CodexAppServerClient _appServer;
    private readonly CodexRolloutReader _rollout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset? _lastAppServerAttempt;
    private CodexAppServerOutcome? _lastAppServerOutcome;
    private UsageSnapshot? _lastAppServerSnapshot;
    private string? _authFingerprint;
    private string? _detailUsageUrl;
    private DateTimeOffset _nextDetailRead;
    private ResetPassBalance? _details;

    private string? _accountEmail;

    public CodexUsageProvider(ProviderContext ctx)
        : this(ctx, ProviderIds.Codex, CodexPaths.Resolve(ctx.Env), false)
    {
    }

    /// <param name="ctx">External dependencies.</param>
    /// <param name="id">Panel id: "codex" or an account id such as "codex#a1b2c3".</param>
    /// <param name="paths">Where auth.json, config.toml and sessions live.</param>
    /// <param name="isAccount">True for an extra account: app-server runs with this CODEX_HOME and a missing login means "sign in again".</param>
    public CodexUsageProvider(ProviderContext ctx, string id, CodexPaths paths, bool isAccount)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(paths);
        _ctx = ctx;
        Id = id;
        IsAccount = isAccount;
        _paths = paths;
        _authReader = new CodexAuthReader(ctx.Files);
        _client = new CodexUsageClient(ctx.Http, ctx.Time);
        _appServer = new CodexAppServerClient(ctx.Processes, ctx.Time, ctx.Log);
        _rollout = new CodexRolloutReader(ctx.Files);
        WatchPaths = [_paths.AuthFile];
    }

    /// <summary>An extra Codex account (<paramref name="accountId"/> like "codex#a1b2c3") with its own CODEX_HOME under <paramref name="accountsDirectory"/>.</summary>
    public static CodexUsageProvider ForAccount(ProviderContext ctx, string accountId, string accountsDirectory)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountsDirectory);
        if (!AccountIds.IsAccountId(accountId) || ProviderIds.KindOf(accountId) != ProviderIds.Codex)
        {
            throw new ArgumentException($"Not a Codex account id: '{accountId}'.", nameof(accountId));
        }

        var home = Path.Combine(accountsDirectory, AccountStorage.CodexHomeName(accountId));
        return new CodexUsageProvider(ctx, accountId, new CodexPaths(home), true);
    }

    public string Id { get; }
    public bool IsAccount { get; }

    /// <summary>The email in the last auth.json read; null when it was missing or unreadable. Read from the UI thread.</summary>
    public string? AccountEmail => Volatile.Read(ref _accountEmail);
    public string DisplayName => "Codex";
    public TimeSpan MinInterval => TimeSpan.FromMinutes(1);
    public IReadOnlyList<string> WatchPaths { get; }

    public IReadOnlyList<MeterOption> StaticMeterOptions =>
    [
        new("5h", Strings.CodexWindow5hLong),
        new("weekly", Strings.CodexWindowWeeklyLong),
        new("daily", Strings.CodexWindowDailyLong),
        new("monthly", Strings.CodexWindowMonthlyLong),
    ];

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FetchCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _ctx.Log.Error("Codex fetch failed: " + ex.GetType().Name);
            return UsageSnapshot.WithStatus(Id, ProviderStatus.Error, _ctx.Time.GetUtcNow(), MessageFailed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UsageSnapshot> FetchCoreAsync(CancellationToken ct)
    {
        var now = _ctx.Time.GetUtcNow();
        var read = await _authReader.ReadAsync(_paths.AuthFile, ct).ConfigureAwait(false);
        var auth = read.Auth;
        BindResetCredits(auth);
        Volatile.Write(ref _accountEmail, auth?.Email);
        if (auth?.Mode == CodexAuthMode.ApiKey)
            return UsageSnapshot.WithStatus(Id, ProviderStatus.Unsupported, now, MessageApiKey);

        Reason reason;
        int? httpStatus = null;
        if (read.Status == CodexAuthReadStatus.Unreadable) reason = Reason.AuthUnreadable;
        else if (auth is null)
        {
            // An extra account without a login has nothing to ask: no app-server, no old logs.
            if (IsAccount) return SignedOut(now);
            reason = Reason.NoAuthFile;
        }
        else if (!auth.HasAccessToken) reason = Reason.NoAccessToken;
        else if (!auth.IsFresh(now)) reason = Reason.StaleToken;
        else
        {
            var usageUrl = _paths.ResolveUsageUrl(_ctx.Files);
            var response = await _client.GetUsageAsync(usageUrl, auth, ct).ConfigureAwait(false);
            if (response.Outcome == CodexHttpOutcome.Unauthorized)
            {
                var reread = await _authReader.ReadAsync(_paths.AuthFile, ct).ConfigureAwait(false);
                if (reread.Auth is { HasAccessToken: true, Mode: not CodexAuthMode.ApiKey } changed
                    && !string.Equals(changed.AccessToken, auth.AccessToken, StringComparison.Ordinal))
                {
                    auth = changed;
                    BindResetCredits(changed);
                    response = await _client.GetUsageAsync(usageUrl, changed, ct).ConfigureAwait(false);
                }
            }

            switch (response.Outcome)
            {
                case CodexHttpOutcome.Ok:
                    if (TryParseWham(response.Body ?? "", auth, now) is { } ok)
                        return await WithCreditDetailsAsync(ok, usageUrl, auth, now, ct).ConfigureAwait(false);
                    reason = Reason.WhamFailed;
                    break;
                case CodexHttpOutcome.RateLimited:
                    return RateLimited(response.RetryAfter, now);
                case CodexHttpOutcome.Unauthorized:
                    reason = Reason.Unauthorized;
                    break;
                default:
                    reason = Reason.WhamFailed;
                    httpStatus = response.StatusCode;
                    break;
            }
        }

        return await FallbackAsync(reason, httpStatus, auth, now, ct).ConfigureAwait(false);
    }

    private async Task<UsageSnapshot> FallbackAsync(
        Reason reason, int? httpStatus, CodexAuth? auth, DateTimeOffset now, CancellationToken ct)
    {
        var codexPath = _ctx.Processes.FindOnPath(CodexAppServerClient.CommandName);
        UsageSnapshot? cachedServerSnapshot = null;

        if (codexPath is not null)
        {
            var throttled = _lastAppServerAttempt is { } last && now - last < AppServerMinimumGap;
            if (!throttled)
            {
                _lastAppServerAttempt = now;
                var result = await _appServer.ReadRateLimitsAsync(codexPath, IsAccount ? _paths.Home : null, ct).ConfigureAwait(false);
                _lastAppServerOutcome = result.Outcome;
                if (result.Outcome == CodexAppServerOutcome.Ok)
                {
                    var plan = CodexUsageParser.FormatPlan(result.PlanType ?? auth?.PlanTypeClaim);
                    _lastAppServerSnapshot = new UsageSnapshot(Id, ProviderStatus.Ok, plan, result.Meters, now)
                    { ResetPasses = result.ResetPasses };
                    return _lastAppServerSnapshot;
                }
                _lastAppServerSnapshot = null;
            }
            else
            {
                cachedServerSnapshot = _lastAppServerSnapshot;
            }
        }

        var fromLogs = _rollout.ReadLatest(_paths.SessionsDirectory, now);
        if (fromLogs is not null && (cachedServerSnapshot is null || fromLogs.Timestamp > cachedServerSnapshot.FetchedAt))
            return FromRollout(fromLogs, auth, now);
        if (cachedServerSnapshot is not null)
        {
            // A keyring-only login has no local identity with which to bind cached credit details.
            var cachedBalance = _authFingerprint is null ? null : cachedServerSnapshot.ResetPasses;
            if (cachedBalance is not null && cachedBalance.Passes.Any(p => p.ExpiresAt <= now))
            {
                var live = cachedBalance.Passes.Where(p => !(p.ExpiresAt <= now)).ToArray();
                cachedBalance = new(cachedBalance.AvailableCount - (cachedBalance.Passes.Count - live.Length), live);
            }
            return cachedServerSnapshot with { ResetPasses = cachedBalance };
        }

        if (codexPath is not null && _lastAppServerOutcome == CodexAppServerOutcome.AuthRequired)
            return SignedOut(now);

        return reason switch
        {
            Reason.NoAuthFile when codexPath is null =>
                UsageSnapshot.WithStatus(Id, ProviderStatus.NotInstalled, now, MessageNotInstalled),
            Reason.NoAuthFile =>
                SignedOut(now),
            Reason.StaleToken or Reason.Unauthorized =>
                UsageSnapshot.WithStatus(Id, ProviderStatus.WaitingForCliRefresh, now, MessageWaitingForCli),
            Reason.NoAccessToken when codexPath is null =>
                UsageSnapshot.WithStatus(Id, ProviderStatus.Unsupported, now, MessageUnsupportedLogin),
            Reason.AuthUnreadable =>
                UsageSnapshot.WithStatus(Id, ProviderStatus.Error, now, MessageAuthUnreadable),
            Reason.WhamFailed when httpStatus is int status =>
                UsageSnapshot.WithStatus(Id, ProviderStatus.Error, now, $"{MessageFailed} (HTTP {status})"),
            _ => UsageSnapshot.WithStatus(Id, ProviderStatus.Error, now, MessageFailed),
        };
    }

    // An extra account has no terminal to run `codex login` in: it is signed in again from the settings.
    private UsageSnapshot SignedOut(DateTimeOffset now)
        => UsageSnapshot.WithStatus(Id, ProviderStatus.SignedOut, now, IsAccount ? Strings.AccountReauth : MessageSignedOut);

    private UsageSnapshot? TryParseWham(string body, CodexAuth auth, DateTimeOffset now)
    {
        try
        {
            var parsed = CodexUsageParser.ParseWham(body, now);
            var plan = CodexUsageParser.FormatPlan(parsed.PlanType ?? auth.PlanTypeClaim);
            return new UsageSnapshot(Id, ProviderStatus.Ok, plan, parsed.Meters, now) { ResetPasses = parsed.ResetPasses };
        }
        catch (JsonException)
        {
            _ctx.Log.Warn("Codex wham/usage returned a non-JSON body.");
            return null;
        }
    }

    private void BindResetCredits(CodexAuth? auth)
    {
        // The same account folder can be signed into again; never retain a bearer as a cache key.
        var fingerprint = auth?.AccessToken is { } token
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth.AccountId + "\n" + token)))
            : null;
        if (_authFingerprint == fingerprint) return;
        _authFingerprint = fingerprint;
        _details = null;
        _nextDetailRead = default;
        if (_lastAppServerSnapshot is { } previous)
            _lastAppServerSnapshot = previous with { ResetPasses = null };
    }

    private async Task<UsageSnapshot> WithCreditDetailsAsync(
        UsageSnapshot usage, string usageUrl, CodexAuth auth, DateTimeOffset now, CancellationToken ct)
    {
        // A later fallback must not resurrect an older inventory after a successful usage poll.
        if (_lastAppServerSnapshot is { ResetPasses: not null } older)
            _lastAppServerSnapshot = older with { ResetPasses = null };
        if (_detailUsageUrl != usageUrl)
        {
            _detailUsageUrl = usageUrl;
            _details = null;
            _nextDetailRead = default;
        }
        var summary = usage.ResetPasses;
        if (summary is null || summary.AvailableCount == 0)
        {
            _details = null;
            return usage;
        }
        if (_details?.AvailableCount != summary.AvailableCount) _details = null;
        if (now >= _nextDetailRead)
        {
            _nextDetailRead = now + AppServerMinimumGap;
            _details = null;
            var detailUrl = new Uri(new Uri(usageUrl), "rate-limit-reset-credits").AbsoluteUri;
            var response = await _client.GetUsageAsync(detailUrl, auth, ct).ConfigureAwait(false);
            if (response.RetryAfter is { } wait && wait > AppServerMinimumGap)
                _nextDetailRead = wait < DateTimeOffset.MaxValue - now ? now + wait : DateTimeOffset.MaxValue;
            if (response.Outcome == CodexHttpOutcome.Ok && response.Body is { } body)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    _details = CodexResetCredits.Read(doc.RootElement, _ctx.Time.GetUtcNow());
                }
                catch (JsonException)
                {
                    _ctx.Log.Warn("Codex reset-credit details could not be parsed.");
                }
            }
        }
        if (_details is not { } details) return usage;
        // A fresh /usage count supersedes cached counts. Expiries can also pass within the poll interval.
        var asOf = _ctx.Time.GetUtcNow();
        var live = details.Passes.Where(p => !(p.ExpiresAt <= asOf)).ToArray();
        return usage with { ResetPasses = details with { Passes = live } };
    }

    private UsageSnapshot FromRollout(CodexRolloutSnapshot logs, CodexAuth? auth, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(logs.Timestamp, _ctx.Time.LocalTimeZone);
        var message = string.Format(
            CultureInfo.CurrentCulture, Strings.CodexFromLogsFormat, local.ToString("HH:mm", CultureInfo.InvariantCulture));
        var plan = CodexUsageParser.FormatPlan(logs.PlanType ?? auth?.PlanTypeClaim);
        return new UsageSnapshot(Id, ProviderStatus.Ok, plan, logs.Meters, now, message);
    }

    private UsageSnapshot RateLimited(TimeSpan? retryAfter, DateTimeOffset now)
    {
        var wait = retryAfter ?? TimeSpan.FromMinutes(5);
        var until = TimeZoneInfo.ConvertTime(now + wait, _ctx.Time.LocalTimeZone);
        var message = string.Format(
            CultureInfo.CurrentCulture, Strings.CodexRateLimitedUntilFormat, until.ToString("HH:mm", CultureInfo.InvariantCulture));
        return UsageSnapshot.WithStatus(Id, ProviderStatus.RateLimited, now, message, retryAfter);
    }

    private enum Reason { NoAuthFile, NoAccessToken, StaleToken, Unauthorized, WhamFailed, AuthUnreadable }
}
