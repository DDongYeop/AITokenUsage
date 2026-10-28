using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>
/// Codex (ChatGPT plan) usage. Order: apikey → Unsupported; fresh token → wham/usage
/// (401 → re-read auth.json, retry once only if the token changed); otherwise app-server
/// (at most once per 15 min) → rollout logs → status. Never refreshes or writes auth.json.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider
{
    public static readonly TimeSpan AppServerMinimumGap = TimeSpan.FromMinutes(15);

    public const string MessageApiKey = "API 키 로그인 — 플랜 한도 없음";
    public const string MessageNotInstalled = "설치 안 됨 — codex 설치 후 codex login";
    public const string MessageSignedOut = "로그인 필요 — 터미널에서 codex login";
    public const string MessageWaitingForCli = "Codex 로그인 갱신 대기 — 터미널에서 codex 실행";
    public const string MessageUnsupportedLogin = "이 로그인 방식은 codex 설치 후 조회 가능";
    public const string MessageAuthUnreadable = "auth.json을 읽지 못했습니다 — 잠시 후 다시 시도";
    public const string MessageFailed = "Codex 사용량을 불러오지 못했습니다";

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

    public CodexUsageProvider(ProviderContext ctx)
    {
        _ctx = ctx;
        _paths = CodexPaths.Resolve(ctx.Env);
        _authReader = new CodexAuthReader(ctx.Files);
        _client = new CodexUsageClient(ctx.Http, ctx.Time);
        _appServer = new CodexAppServerClient(ctx.Processes, ctx.Time, ctx.Log);
        _rollout = new CodexRolloutReader(ctx.Files);
        WatchPaths = [_paths.AuthFile];
    }

    public string Id => ProviderIds.Codex;
    public string DisplayName => "Codex";
    public TimeSpan MinInterval => TimeSpan.FromMinutes(1);
    public IReadOnlyList<string> WatchPaths { get; }

    public IReadOnlyList<MeterOption> StaticMeterOptions { get; } =
    [
        new("5h", "5시간 한도"),
        new("weekly", "주간 한도"),
        new("daily", "일간 한도"),
        new("monthly", "월간 한도"),
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
        if (auth?.Mode == CodexAuthMode.ApiKey)
            return UsageSnapshot.WithStatus(Id, ProviderStatus.Unsupported, now, MessageApiKey);

        Reason reason;
        int? httpStatus = null;
        if (read.Status == CodexAuthReadStatus.Unreadable) reason = Reason.AuthUnreadable;
        else if (auth is null) reason = Reason.NoAuthFile;
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
                    response = await _client.GetUsageAsync(usageUrl, changed, ct).ConfigureAwait(false);
                }
            }

            switch (response.Outcome)
            {
                case CodexHttpOutcome.Ok:
                    if (TryParseWham(response.Body ?? "", auth, now) is { } ok) return ok;
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
                var result = await _appServer.ReadRateLimitsAsync(codexPath, ct).ConfigureAwait(false);
                _lastAppServerOutcome = result.Outcome;
                if (result.Outcome == CodexAppServerOutcome.Ok)
                {
                    var plan = CodexUsageParser.FormatPlan(result.PlanType ?? auth?.PlanTypeClaim);
                    _lastAppServerSnapshot = new UsageSnapshot(Id, ProviderStatus.Ok, plan, result.Meters, now);
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
            return cachedServerSnapshot;

        if (codexPath is not null && _lastAppServerOutcome == CodexAppServerOutcome.AuthRequired)
            return UsageSnapshot.WithStatus(Id, ProviderStatus.SignedOut, now, MessageSignedOut);

        return reason switch
        {
            Reason.NoAuthFile when codexPath is null =>
                UsageSnapshot.WithStatus(Id, ProviderStatus.NotInstalled, now, MessageNotInstalled),
            Reason.NoAuthFile =>
                UsageSnapshot.WithStatus(Id, ProviderStatus.SignedOut, now, MessageSignedOut),
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

    private UsageSnapshot? TryParseWham(string body, CodexAuth auth, DateTimeOffset now)
    {
        try
        {
            var parsed = CodexUsageParser.ParseWham(body, now);
            var plan = CodexUsageParser.FormatPlan(parsed.PlanType ?? auth.PlanTypeClaim);
            return new UsageSnapshot(Id, ProviderStatus.Ok, plan, parsed.Meters, now);
        }
        catch (JsonException)
        {
            _ctx.Log.Warn("Codex wham/usage returned a non-JSON body.");
            return null;
        }
    }

    private UsageSnapshot FromRollout(CodexRolloutSnapshot logs, CodexAuth? auth, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(logs.Timestamp, _ctx.Time.LocalTimeZone);
        var message = "로그 기준 · " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var plan = CodexUsageParser.FormatPlan(logs.PlanType ?? auth?.PlanTypeClaim);
        return new UsageSnapshot(Id, ProviderStatus.Ok, plan, logs.Meters, now, message);
    }

    private UsageSnapshot RateLimited(TimeSpan? retryAfter, DateTimeOffset now)
    {
        var wait = retryAfter ?? TimeSpan.FromMinutes(5);
        var until = TimeZoneInfo.ConvertTime(now + wait, _ctx.Time.LocalTimeZone);
        var message = "요청 제한 — " + until.ToString("HH:mm", CultureInfo.InvariantCulture) + " 이후 재시도";
        return UsageSnapshot.WithStatus(Id, ProviderStatus.RateLimited, now, message, retryAfter);
    }

    private enum Reason { NoAuthFile, NoAccessToken, StaleToken, Unauthorized, WhamFailed, AuthUnreadable }
}
