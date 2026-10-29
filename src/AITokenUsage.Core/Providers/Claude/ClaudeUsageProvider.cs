using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Reads Claude Code's OAuth login (read-only) and calls GET /api/oauth/usage.
/// Never refreshes or writes tokens, never logs tokens, headers or bodies.
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

    private readonly ProviderContext _ctx;
    private readonly ClaudeVersionProbe _versionProbe;
    private readonly ClaudeCredentialReader _reader;
    private readonly string _credentialsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _version;
    private DateTimeOffset _versionProbedAt;

    // Mirrors Claude Code's per-bearer memory: a rejected token is not sent again for 1 h.
    // Only a SHA-256 hash of the token is kept.
    private string? _heldTokenHash;
    private DateTimeOffset _heldUntil;
    private ProviderStatus _heldStatus;
    private string? _heldMessage;

    public ClaudeUsageProvider(ProviderContext ctx)
        : this(ctx, new ClaudeVersionProbe(ctx.Env))
    {
    }

    public ClaudeUsageProvider(ProviderContext ctx, ClaudeVersionProbe versionProbe)
    {
        _ctx = ctx;
        _versionProbe = versionProbe;
        _reader = new ClaudeCredentialReader(ctx.Files);
        _credentialsPath = ClaudePaths.ResolveCredentialsPath(ctx.Env);
        WatchPaths = [_credentialsPath];
    }

    public string Id => ProviderIds.Claude;

    public string DisplayName => "Claude";

    public TimeSpan MinInterval => TimeSpan.FromMinutes(2);

    public IReadOnlyList<string> WatchPaths { get; }

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
            ClaudeCredentialReadResult read = await _reader.ReadAsync(_credentialsPath, ct).ConfigureAwait(false);
            if (read.State != ClaudeCredentialState.Ok || read.Credentials is null)
            {
                return FromCredentialState(read.State);
            }

            return await QueryAsync(read.Credentials, allowAuthRetry: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UsageSnapshot> QueryAsync(ClaudeCredentials credentials, bool allowAuthRetry, CancellationToken ct)
    {
        DateTimeOffset now = _ctx.Time.GetUtcNow();
        string? plan = ClaudeUsageParser.FormatPlanLabel(credentials.SubscriptionType, credentials.RateLimitTier);
        string tokenHash = HashToken(credentials.AccessToken);

        if (_heldTokenHash == tokenHash && now < _heldUntil)
        {
            return Status(_heldStatus, _heldMessage, plan);
        }

        if (credentials.ExpiresAt is { } expiresAt && expiresAt - now < RefreshMargin)
        {
            // Claude Code renews the login under its own lock; the file watcher re-queries when it does.
            return Status(ProviderStatus.WaitingForCliRefresh, MsgWaitingForCli, plan);
        }

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(credentials.AccessToken, now, ct).ConfigureAwait(false);
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
                        ClaudeCredentialReadResult reread = await _reader.ReadAsync(_credentialsPath, ct).ConfigureAwait(false);
                        if (reread.State != ClaudeCredentialState.Ok || reread.Credentials is null)
                        {
                            return FromCredentialState(reread.State);
                        }

                        if (!string.Equals(reread.Credentials.AccessToken, credentials.AccessToken, StringComparison.Ordinal))
                        {
                            return await QueryAsync(reread.Credentials, allowAuthRetry: false, ct).ConfigureAwait(false);
                        }
                    }

                    Hold(tokenHash, now, ProviderStatus.WaitingForCliRefresh, MsgWaitingForCli);
                    return Status(ProviderStatus.WaitingForCliRefresh, MsgWaitingForCli, plan);
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

    private async Task<HttpResponseMessage> SendAsync(string accessToken, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/" + UserAgentVersion(now));
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

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
