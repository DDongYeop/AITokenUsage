using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Calls GET /api/oauth/usage with the login from an <see cref="IClaudeCredentialSource"/>.
/// The built-in panel reads Claude Code's login read-only and never refreshes or writes it; an extra account
/// (<see cref="ForAccount"/>) uses the widget-owned login, which renews itself. Never logs tokens, headers or bodies.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string OAuthBetaHeader = "oauth-2025-04-20";

    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AuthRejectedHold = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromHours(1);

    private static readonly TimeSpan VersionProbeInterval = TimeSpan.FromHours(1);

    private static string MsgNotInstalled => Strings.ClaudeNotInstalled;
    private static string MsgSignedOut => Strings.ClaudeSignedOut;
    private static string MsgNoUsageAccess => Strings.ClaudeNoUsageAccess;
    private static string MsgWaitingForCli => Strings.ClaudeWaitingForCli;
    private static string MsgUnreadable => Strings.MessageCredentialsUnreadable;
    private static string MsgRateLimited => Strings.MessageRateLimitedRetry;
    private static string MsgNetwork => Strings.MessageNetworkRetry;
    private static string MsgBadBody => Strings.ClaudeBadBody;
    private static string MsgNoMeters => Strings.ClaudeNoMeters;

    private static string MsgReauth => Strings.AccountReauth;

    private readonly ProviderContext _ctx;
    private readonly ClaudeVersionProbe _versionProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _version;
    private DateTimeOffset _versionProbedAt;

    // Mirrors Claude Code's per-bearer memory: a rejected token is not sent again for 1 h.
    // Only a SHA-256 hash of the token is kept.
    private string? _heldTokenHash;
    private DateTimeOffset _heldUntil;
    private ProviderStatus _heldStatus;
    private string? _heldMessage;

    /// <summary>The built-in Claude panel: Claude Code's own login, read-only.</summary>
    public ClaudeUsageProvider(ProviderContext ctx)
        : this(ctx, ProviderIds.Claude, CliSource(ctx), null)
    {
    }

    /// <summary>The built-in Claude panel with a given version probe (tests).</summary>
    public ClaudeUsageProvider(ProviderContext ctx, ClaudeVersionProbe versionProbe)
        : this(ctx, ProviderIds.Claude, CliSource(ctx), versionProbe)
    {
    }

    /// <param name="ctx">External dependencies.</param>
    /// <param name="id">Panel id: "claude" or an account id such as "claude#k3x9q2".</param>
    /// <param name="source">Where the login comes from.</param>
    /// <param name="versionProbe">Picks the claude-code/&lt;version&gt; User-Agent; defaults to probing this PC.</param>
    public ClaudeUsageProvider(ProviderContext ctx, string id, IClaudeCredentialSource source, ClaudeVersionProbe? versionProbe = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(source);
        _ctx = ctx;
        Id = id;
        Source = source;
        _versionProbe = versionProbe ?? new ClaudeVersionProbe(ctx.Env);
    }

    /// <summary>An extra Claude account (<paramref name="accountId"/> like "claude#k3x9q2") with its widget-owned login.</summary>
    public static ClaudeUsageProvider ForAccount(ProviderContext ctx, string accountId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!AccountIds.IsAccountId(accountId) || ProviderIds.KindOf(accountId) != ProviderIds.Claude)
        {
            throw new ArgumentException($"Not a Claude account id: '{accountId}'.", nameof(accountId));
        }

        var source = new ClaudeOwnedCredentialSource(
            ctx.Secrets,
            AccountStorage.ClaudeSecretName(accountId),
            new ClaudeTokenRenewer(ctx.Http, ctx.Time, ctx.Log),
            ctx.Time,
            ctx.Log);
        return new ClaudeUsageProvider(ctx, accountId, source);
    }

    public string Id { get; }

    public string DisplayName => "Claude";

    public TimeSpan MinInterval => TimeSpan.FromMinutes(2);

    public IReadOnlyList<string> WatchPaths => Source.WatchPaths;

    /// <summary>Where the login comes from: Claude Code's file or the widget-owned login of an extra account.</summary>
    public IClaudeCredentialSource Source { get; }

    /// <summary>The extra account's email once its login was loaded; null for the built-in panel.</summary>
    public string? AccountEmail => (Source as ClaudeOwnedCredentialSource)?.Email;

    public IReadOnlyList<MeterOption> StaticMeterOptions =>
    [
        new MeterOption(ClaudeUsageParser.SessionKey, ClaudeUsageParser.SessionLongLabel),
        new MeterOption(ClaudeUsageParser.WeeklyKey, ClaudeUsageParser.WeeklyLongLabel),
    ];

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The token endpoint and the usage endpoint get the same User-Agent.
            string userAgent = "claude-code/" + UserAgentVersion(_ctx.Time.GetUtcNow());
            ClaudeCredentialReadResult read = await Source.ReadAsync(userAgent, ct).ConfigureAwait(false);
            if (read.State != ClaudeCredentialState.Ok || read.Credentials is null)
            {
                return FromCredentialState(read.State);
            }

            return await QueryAsync(read.Credentials, userAgent, allowAuthRetry: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UsageSnapshot> QueryAsync(ClaudeCredentials credentials, string userAgent, bool allowAuthRetry, CancellationToken ct)
    {
        DateTimeOffset now = _ctx.Time.GetUtcNow();
        string? plan = ClaudeUsageParser.FormatPlanLabel(credentials.SubscriptionType, credentials.RateLimitTier);
        string tokenHash = HashToken(credentials.AccessToken);

        if (_heldTokenHash == tokenHash && now < _heldUntil)
        {
            return Status(_heldStatus, _heldMessage, plan);
        }

        if (!Source.RenewsItself && credentials.ExpiresAt is { } expiresAt && expiresAt - now < RefreshMargin)
        {
            // Claude Code renews the login under its own lock; the file watcher re-queries when it does.
            return Status(ProviderStatus.WaitingForCliRefresh, MsgWaitingForCli, plan);
        }

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(credentials.AccessToken, userAgent, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _ctx.Log.Warn("Claude usage request failed (network).", ex);
            return Status(ProviderStatus.Error, MsgNetwork, plan);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _ctx.Log.Warn("Claude usage request timed out.", ex);
            return Status(ProviderStatus.Error, MsgNetwork, plan);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ReadUsageAsync(response, plan, ct).ConfigureAwait(false);

                case HttpStatusCode.Unauthorized:
                {
                    _ctx.Log.Info("Claude usage: HTTP 401.");
                    if (allowAuthRetry)
                    {
                        // CLI: re-read the file. Widget-owned: renew once, unless this fetch just did.
                        ClaudeCredentialReadResult? recovered = await Source.RecoverAsync(credentials, userAgent, ct).ConfigureAwait(false);
                        if (recovered is not null)
                        {
                            if (recovered.State != ClaudeCredentialState.Ok || recovered.Credentials is null)
                            {
                                return FromCredentialState(recovered.State);
                            }

                            if (!string.Equals(recovered.Credentials.AccessToken, credentials.AccessToken, StringComparison.Ordinal))
                            {
                                return await QueryAsync(recovered.Credentials, userAgent, allowAuthRetry: false, ct).ConfigureAwait(false);
                            }
                        }
                    }

                    // Same token (or the retry was refused too): do not send it again for an hour.
                    (ProviderStatus heldStatus, string heldMessage) = Source.RenewsItself
                        ? (ProviderStatus.SignedOut, MsgReauth)
                        : (ProviderStatus.WaitingForCliRefresh, MsgWaitingForCli);
                    Hold(tokenHash, now, heldStatus, heldMessage);
                    return Status(heldStatus, heldMessage, plan);
                }

                case HttpStatusCode.Forbidden:
                {
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (ClaudeUsageParser.IsAnthropicErrorBody(body))
                    {
                        _ctx.Log.Info("Claude usage: HTTP 403 with an Anthropic error body.");
                        Hold(tokenHash, now, ProviderStatus.Unsupported, MsgNoUsageAccess);
                        return Status(ProviderStatus.Unsupported, MsgNoUsageAccess, plan);
                    }

                    _ctx.Log.Info("Claude usage: HTTP 403 without an Anthropic error body; treated as a rate limit.");
                    return Status(ProviderStatus.RateLimited, MsgRateLimited, plan, RetryAfterOf(response, now));
                }

                case HttpStatusCode.TooManyRequests:
                    _ctx.Log.Info("Claude usage: HTTP 429.");
                    return Status(ProviderStatus.RateLimited, MsgRateLimited, plan, RetryAfterOf(response, now));

                default:
                {
                    int code = (int)response.StatusCode;
                    _ctx.Log.Warn($"Claude usage: unexpected HTTP {code}.");
                    return Status(ProviderStatus.Error, string.Format(CultureInfo.CurrentCulture, Strings.ClaudeServerErrorFormat, code), plan);
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, string userAgent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _ctx.Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    private async Task<UsageSnapshot> ReadUsageAsync(HttpResponseMessage response, string? plan, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ClaudeUsageData data;
        try
        {
            data = ClaudeUsageParser.Parse(body);
        }
        catch (JsonException)
        {
            // The exception is not logged: its message can echo part of the body.
            _ctx.Log.Warn("Claude usage: HTTP 200 body could not be parsed.");
            return Status(ProviderStatus.Error, MsgBadBody, plan);
        }

        _heldTokenHash = null;
        return new UsageSnapshot(
            Id,
            ProviderStatus.Ok,
            plan,
            data.Meters,
            _ctx.Time.GetUtcNow(),
            data.Meters.Count == 0 ? MsgNoMeters : null,
            null,
            data.ExtraLine);
    }

    private UsageSnapshot FromCredentialState(ClaudeCredentialState state)
    {
        switch (state)
        {
            case ClaudeCredentialState.NotInstalled:
                return Status(ProviderStatus.NotInstalled, MsgNotInstalled, null);
            case ClaudeCredentialState.SignedOut:
                return Status(ProviderStatus.SignedOut, MsgSignedOut, null);
            case ClaudeCredentialState.MissingProfileScope:
                return Status(ProviderStatus.Unsupported, MsgNoUsageAccess, null);
            case ClaudeCredentialState.ReauthRequired:
                return Status(ProviderStatus.SignedOut, MsgReauth, null);
            case ClaudeCredentialState.RenewFailed:
                return Status(ProviderStatus.Error, MsgNetwork, null);
            default:
                _ctx.Log.Warn("Claude credentials file could not be read or parsed after retries.");
                return Status(ProviderStatus.Error, MsgUnreadable, null);
        }
    }

    private UsageSnapshot Status(ProviderStatus status, string? message, string? plan, TimeSpan? retryAfter = null) =>
        UsageSnapshot.WithStatus(Id, status, _ctx.Time.GetUtcNow(), message, retryAfter) with { PlanLabel = plan };

    private void Hold(string tokenHash, DateTimeOffset now, ProviderStatus status, string message)
    {
        _heldTokenHash = tokenHash;
        _heldUntil = now + AuthRejectedHold;
        _heldStatus = status;
        _heldMessage = message;
    }

    private static TimeSpan RetryAfterOf(HttpResponseMessage response, DateTimeOffset now)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta;
        if (wait is null && header?.Date is { } date)
        {
            wait = date - now;
        }

        if (wait is not { } value || value <= TimeSpan.Zero)
        {
            return DefaultRateLimitWait;
        }

        return value > MaxRateLimitWait ? MaxRateLimitWait : value;
    }

    private string UserAgentVersion(DateTimeOffset now)
    {
        if (_version is null || now - _versionProbedAt >= VersionProbeInterval)
        {
            _version = _versionProbe.GetVersion();
            _versionProbedAt = now;
        }

        return _version;
    }

    private static IClaudeCredentialSource CliSource(ProviderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Credentials is MacKeychainCredentialStore keychain)
        {
            return new ClaudeKeychainCredentialSource(keychain, ctx.Files, ctx.Env);
        }

        return new ClaudeCliCredentialSource(ctx.Files, ClaudePaths.ResolveCredentialsPath(ctx.Env));
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
