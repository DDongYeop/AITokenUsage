using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// Gemini CLI quota via Code Assist (cloudcode-pa). Refreshes tokens in memory only,
/// caches loadCodeAssist for one hour and never calls onboardUser.
/// </summary>
public sealed class GeminiUsageProvider : IUsageProvider
{
    public const string LoadCodeAssistUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    public const string RetrieveUserQuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";

    private const string LoadCodeAssistBody =
        """{"metadata":{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}""";

    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoadCacheLifetime = TimeSpan.FromHours(1);

    private readonly ProviderContext _ctx;
    private readonly GeminiPaths _paths;
    private readonly GeminiCredentialReader _reader;
    private readonly GeminiTokenRefresher _refresher;
    private readonly GeminiProjectResolver _projects;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private MemoryToken? _memoryToken;
    private LoadCacheEntry? _loadCache;

    public GeminiUsageProvider(ProviderContext ctx)
    {
        _ctx = ctx;
        _paths = new GeminiPaths(ctx.Env);
        _reader = new GeminiCredentialReader(ctx.Files, ctx.Credentials, _paths);
        var clientLocator = new GeminiOAuthClientLocator(ctx.Env, ctx.Files, ctx.Processes);
        _refresher = new GeminiTokenRefresher(ctx.Http, ctx.Time, clientLocator.Locate);
        _projects = new GeminiProjectResolver(ctx.Env, ctx.Files, _paths);
        WatchPaths = [_paths.OAuthCredsPath, _paths.SettingsPath];
    }

    public string Id => ProviderIds.Gemini;
    public string DisplayName => "Gemini";
    public TimeSpan MinInterval => TimeSpan.FromMinutes(2);
    public IReadOnlyList<string> WatchPaths { get; }
    public IReadOnlyList<MeterOption> StaticMeterOptions => GeminiQuotaParser.MeterOptions;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FetchCoreAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _ctx.Log.Warn("Gemini: network error", ex);
            return Status(ProviderStatus.Error, GeminiMessages.Network);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _ctx.Log.Warn("Gemini: request timed out");
            return Status(ProviderStatus.Error, GeminiMessages.Network);
        }
        catch (JsonException)
        {
            _ctx.Log.Warn("Gemini: response was not valid JSON");
            return Status(ProviderStatus.Error, GeminiMessages.BadResponse);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private async Task<UsageSnapshot> FetchCoreAsync(CancellationToken ct)
    {
        if (GeminiAuthType.IsUnsupported(GeminiAuthType.Read(_ctx.Files, _paths.SettingsPath)))
        {
            return Status(ProviderStatus.Unsupported, GeminiMessages.ApiKeyMode);
        }

        var read = await _reader.ReadAsync(ct).ConfigureAwait(false);
        if (read.Status == GeminiCredentialStatus.NotFound)
        {
            return Status(ProviderStatus.NotInstalled, GeminiMessages.NotInstalled);
        }

        if (read.Status == GeminiCredentialStatus.Unreadable)
        {
            _ctx.Log.Warn("Gemini: credentials could not be parsed after retries");
            return Status(ProviderStatus.Error, GeminiMessages.CredentialsUnreadable);
        }

        var auth = CreateAuthState(read.Credentials!);
        var now = _ctx.Time.GetUtcNow();
        if (string.IsNullOrEmpty(auth.AccessToken) || (auth.Expiry is { } expiry && expiry <= now + RefreshSkew))
        {
            var refreshFailure = await RefreshAsync(auth, ct).ConfigureAwait(false);
            if (refreshFailure is not null)
            {
                return refreshFailure;
            }
        }

        var hasHostedDomain = GeminiCodeAssistParser.HasHostedDomain(auth.IdToken);
        var (info, loadFailure) = await LoadCodeAssistAsync(auth, hasHostedDomain, ct).ConfigureAwait(false);
        if (loadFailure is not null)
        {
            return loadFailure;
        }

        if (info!.IsConsumerShutdown(hasHostedDomain))
        {
            return Status(ProviderStatus.Unsupported, GeminiMessages.ConsumerShutdown);
        }

        var project = info.Project ?? _projects.Resolve();
        if (project is null)
        {
            // Never call onboardUser: a missing tier means the CLI itself has not finished setup.
            return info.HasCurrentTier
                ? Status(ProviderStatus.Error, GeminiMessages.ProjectRequired)
                : Status(ProviderStatus.SignedOut, GeminiMessages.NotOnboarded);
        }

        var quotaBody = $$"""{"project":"{{JsonEncodedText.Encode(project)}}"}""";
        var sent = await SendAuthorizedAsync(auth, () => JsonPost(RetrieveUserQuotaUrl, quotaBody), ct).ConfigureAwait(false);
        if (sent.Failure is not null)
        {
            return sent.Failure;
        }

        using var response = sent.Response!;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return MapHttpFailure("retrieveUserQuota", response, body, info, hasHostedDomain);
        }

        var meters = GeminiQuotaParser.Parse(body);
        if (meters.Count == 0)
        {
            return Status(ProviderStatus.Error, GeminiMessages.NoQuotaData);
        }

        return new UsageSnapshot(Id, ProviderStatus.Ok, GeminiCodeAssistParser.PlanLabel(info, hasHostedDomain),
            meters, _ctx.Time.GetUtcNow());
    }

    private AuthState CreateAuthState(GeminiCredentials credentials)
    {
        var auth = new AuthState(credentials.RefreshToken)
        {
            AccessToken = credentials.AccessToken,
            Expiry = credentials.Expiry,
            IdToken = credentials.IdToken,
        };

        // Prefer our in-memory refreshed token while the CLI has not written a newer one.
        var memory = _memoryToken;
        if (memory is not null
            && memory.SourceRefreshToken == credentials.RefreshToken
            && (credentials.Expiry is null || memory.Expiry > credentials.Expiry))
        {
            auth.AccessToken = memory.AccessToken;
            auth.Expiry = memory.Expiry;
            auth.IdToken = memory.IdToken ?? credentials.IdToken;
        }

        return auth;
    }

    /// <summary>Returns null on success, otherwise the snapshot to report.</summary>
    private async Task<UsageSnapshot?> RefreshAsync(AuthState auth, CancellationToken ct)
    {
        auth.RefreshedThisFetch = true;
        if (string.IsNullOrEmpty(auth.RefreshToken))
        {
            return Status(ProviderStatus.SignedOut, GeminiMessages.SignInAgain);
        }

        var result = await _refresher.RefreshAsync(auth.RefreshToken, ct).ConfigureAwait(false);
        if (result.Status == GeminiRefreshStatus.InvalidGrant)
        {
            _memoryToken = null;
            _ctx.Log.Info("Gemini: refresh token rejected (invalid_grant)");
            return Status(ProviderStatus.SignedOut, GeminiMessages.SignInAgain);
        }

        if (result.Status == GeminiRefreshStatus.ClientUnavailable)
        {
            _ctx.Log.Warn("Gemini: gemini-cli OAuth client not found; cannot refresh the access token");
            return Status(ProviderStatus.Error, GeminiMessages.ClientUnavailable);
        }

        if (result.Status == GeminiRefreshStatus.Failed)
        {
            _ctx.Log.Warn($"Gemini: token refresh failed with HTTP {result.HttpStatus}");
            return Status(ProviderStatus.Error, GeminiMessages.RefreshFailed);
        }

        auth.AccessToken = result.AccessToken;
        auth.Expiry = result.Expiry;
        auth.IdToken = result.IdToken ?? auth.IdToken;
        _memoryToken = new MemoryToken(auth.RefreshToken, result.AccessToken!, result.Expiry!.Value, auth.IdToken);
        return null;
    }

    private async Task<(GeminiCodeAssistInfo? Info, UsageSnapshot? Failure)> LoadCodeAssistAsync(
        AuthState auth, bool hasHostedDomain, CancellationToken ct)
    {
        var accountKey = auth.RefreshToken ?? auth.AccessToken ?? "";
        var now = _ctx.Time.GetUtcNow();
        var cached = _loadCache;
        if (cached is not null && cached.AccountKey == accountKey && now - cached.FetchedAt < LoadCacheLifetime)
        {
            return (cached.Info, null);
        }

        var sent = await SendAuthorizedAsync(auth, () => JsonPost(LoadCodeAssistUrl, LoadCodeAssistBody), ct).ConfigureAwait(false);
        if (sent.Failure is not null)
        {
            return (null, sent.Failure);
        }

        using var response = sent.Response!;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, MapHttpFailure("loadCodeAssist", response, body, info: null, hasHostedDomain));
        }

        var info = GeminiCodeAssistParser.Parse(body);
        _loadCache = new LoadCacheEntry(accountKey, info, now);
        return (info, null);
    }

    /// <summary>Sends with the current token; on 401 refreshes once (if not already refreshed) and retries.</summary>
    private async Task<SendResult> SendAuthorizedAsync(AuthState auth, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var response = await SendWithTokenAsync(build, auth.AccessToken!, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return new SendResult(response, null);
        }

        response.Dispose();
        if (auth.RefreshedThisFetch)
        {
            return new SendResult(null, SignedOutAfterUnauthorized());
        }

        var refreshFailure = await RefreshAsync(auth, ct).ConfigureAwait(false);
        if (refreshFailure is not null)
        {
            return new SendResult(null, refreshFailure);
        }

        response = await SendWithTokenAsync(build, auth.AccessToken!, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return new SendResult(response, null);
        }

        response.Dispose();
        return new SendResult(null, SignedOutAfterUnauthorized());
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(Func<HttpRequestMessage> build, string accessToken, CancellationToken ct)
    {
        using var request = build();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _ctx.Http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private UsageSnapshot SignedOutAfterUnauthorized()
    {
        _memoryToken = null;
        _ctx.Log.Info("Gemini: still unauthorized after one refresh");
        return Status(ProviderStatus.SignedOut, GeminiMessages.SignInAgain);
    }

    private UsageSnapshot MapHttpFailure(string stage, HttpResponseMessage response, string body,
        GeminiCodeAssistInfo? info, bool hasHostedDomain)
    {
        var status = (int)response.StatusCode;
        _ctx.Log.Warn($"Gemini: {stage} returned HTTP {status}");

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return Status(ProviderStatus.RateLimited, GeminiMessages.RateLimited, ReadRetryAfter(response));
        }

        if (response.StatusCode == HttpStatusCode.Forbidden
            && GeminiCodeAssistParser.HasErrorReason(body, "SUBSCRIPTION_REQUIRED"))
        {
            return info is not null && info.IsShutdownLicenseDenial(hasHostedDomain)
                ? Status(ProviderStatus.Unsupported, GeminiMessages.ConsumerShutdown)
                : Status(ProviderStatus.Unsupported, GeminiMessages.NoLicense);
        }

        if (GeminiCodeAssistParser.LooksLikeShutdown(body))
        {
            return Status(ProviderStatus.Unsupported, GeminiMessages.ConsumerShutdown);
        }

        return Status(ProviderStatus.Error, GeminiMessages.ServerError(status));
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _ctx.Time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static HttpRequestMessage JsonPost(string url, string json) =>
        new(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private UsageSnapshot Status(ProviderStatus status, string message, TimeSpan? retryAfter = null) =>
        UsageSnapshot.WithStatus(Id, status, _ctx.Time.GetUtcNow(), message, retryAfter);

    private sealed class AuthState(string? refreshToken)
    {
        public string? RefreshToken { get; } = refreshToken;
        public string? AccessToken { get; set; }
        public DateTimeOffset? Expiry { get; set; }
        public string? IdToken { get; set; }
        public bool RefreshedThisFetch { get; set; }
    }

    private sealed record MemoryToken(string? SourceRefreshToken, string AccessToken, DateTimeOffset Expiry, string? IdToken)
    {
        public override string ToString() => $"MemoryToken {{ Expiry = {Expiry:O} }}";
    }

    private sealed record LoadCacheEntry(string AccountKey, GeminiCodeAssistInfo Info, DateTimeOffset FetchedAt)
    {
        public override string ToString() => $"LoadCacheEntry {{ FetchedAt = {FetchedAt:O} }}";
    }

    private sealed record SendResult(HttpResponseMessage? Response, UsageSnapshot? Failure);
}
