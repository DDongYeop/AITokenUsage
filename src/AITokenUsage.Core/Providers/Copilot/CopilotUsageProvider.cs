using System.Net;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>
/// GitHub Copilot monthly usage from copilot_internal/user. Token candidates are validated by the live
/// call itself (401/403/404 → next candidate) and the working one is remembered for the next fetch.
/// </summary>
public sealed class CopilotUsageProvider : IUsageProvider
{
    private readonly ProviderContext _ctx;
    private readonly CopilotTokenSources _sources;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly object _gate = new();
    private CopilotTokenCandidate? _working;
    private string? _signedInLogin;
    private int _signOutGeneration;

    public CopilotUsageProvider(ProviderContext ctx)
    {
        _ctx = ctx;
        _sources = new CopilotTokenSources(ctx);
    }

    public string Id => ProviderIds.Copilot;
    public string DisplayName => "Copilot";
    public TimeSpan MinInterval => TimeSpan.FromMinutes(5);
    public IReadOnlyList<string> WatchPaths { get; } = [];
    public IReadOnlyList<MeterOption> StaticMeterOptions => CopilotUsageParser.MeterOptions;

    /// <summary>Login of the app's own (device-flow) GitHub sign-in; null when none is saved. Updated on each fetch.</summary>
    public string? SignedInLogin
    {
        get
        {
            lock (_gate)
            {
                return _signedInLogin;
            }
        }
    }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FetchCoreAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _ctx.Log.Warn("Copilot: network error", ex);
            return Status(ProviderStatus.Error, CopilotMessages.Network);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _ctx.Log.Warn("Copilot: request timed out");
            return Status(ProviderStatus.Error, CopilotMessages.Network);
        }
        catch (JsonException)
        {
            _ctx.Log.Warn("Copilot: response was not valid JSON");
            return Status(ProviderStatus.Error, CopilotMessages.BadResponse);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Validates a token (e.g. from <see cref="GitHubDeviceFlow"/>) against copilot_internal/user, looks up the
    /// login with GET /user, then saves {"token","login"} through ISecretStore. Throws CopilotSignInException.
    /// </summary>
    public async Task SignInWithTokenAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        token = token.Trim();

        await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? loginFromUsage;
            using (var request = GitHubApi.CreateGet(GitHubApi.CopilotUserUrl, token, GitHubApi.CopilotApiVersion))
            using (var response = await _ctx.Http.SendAsync(request, ct).ConfigureAwait(false))
            {
                if (GitHubApi.IsRateLimited(response))
                {
                    throw new CopilotSignInException(CopilotMessages.RateLimited);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    _ctx.Log.Warn($"Copilot: sign-in token rejected with HTTP {status}");
                    throw new CopilotSignInException(IsRejection(response.StatusCode)
                        ? CopilotMessages.TokenRejected
                        : CopilotMessages.ServerError(status));
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                loginFromUsage = CopilotUsageParser.Parse(body, _ctx.Time.GetUtcNow(), _ctx.Time.LocalTimeZone).Login;
            }

            var login = await FetchLoginAsync(token, ct).ConfigureAwait(false) ?? loginFromUsage;
            var saved = new CopilotSavedToken(token, login);
            _ctx.Secrets.Save(CopilotSavedToken.SecretName, saved.ToJson());
            lock (_gate)
            {
                _working = new CopilotTokenCandidate(CopilotTokenSourceKind.Saved, "saved", token, login);
                _signedInLogin = DisplayLogin(saved);
            }

            _ctx.Log.Info("Copilot: signed in with a device-flow token");
        }
        catch (HttpRequestException ex)
        {
            throw new CopilotSignInException(CopilotMessages.Network, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CopilotSignInException(CopilotMessages.Network, ex);
        }
        catch (JsonException ex)
        {
            throw new CopilotSignInException(CopilotMessages.BadResponse, ex);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>Deletes the saved device-flow token. Tokens from gh/env/CLI are not touched.</summary>
    public void SignOut()
    {
        // Delete first, then bump the generation: a fetch that read the token before the delete sees the
        // bump and must not re-remember the saved token or republish its login.
        _ctx.Secrets.Delete(CopilotSavedToken.SecretName);
        lock (_gate)
        {
            _signOutGeneration++;
            _signedInLogin = null;
            if (_working?.Kind == CopilotTokenSourceKind.Saved)
            {
                _working = null;
            }
        }

        _ctx.Log.Info("Copilot: signed out (saved token removed)");
    }

    private async Task<UsageSnapshot> FetchCoreAsync(CancellationToken ct)
    {
        int generation;
        lock (_gate)
        {
            generation = _signOutGeneration;
        }

        var saved = CopilotSavedToken.Load(_ctx.Secrets, _ctx.Log);
        lock (_gate)
        {
            if (generation == _signOutGeneration)
            {
                _signedInLogin = saved is null ? null : DisplayLogin(saved);
            }
        }

        var tried = new HashSet<string>(StringComparer.Ordinal);
        CopilotTokenCandidate? remembered;
        lock (_gate)
        {
            remembered = _working;
        }

        if (remembered is not null)
        {
            tried.Add(remembered.Token);
            var result = await TryCandidateAsync(remembered, generation, ct).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }

            lock (_gate)
            {
                if (ReferenceEquals(_working, remembered))
                {
                    _working = null;
                }
            }
        }

        await foreach (var candidate in _sources.EnumerateAsync(ct).ConfigureAwait(false))
        {
            if (!tried.Add(candidate.Token))
            {
                continue;
            }

            var result = await TryCandidateAsync(candidate, generation, ct).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        _ctx.Log.Info("Copilot: no usable GitHub token found");
        return Status(ProviderStatus.SignedOut, CopilotMessages.SignInRequired);
    }

    /// <summary>Returns the snapshot to report, or null when this token was rejected and the next one should be tried.</summary>
    private async Task<UsageSnapshot?> TryCandidateAsync(CopilotTokenCandidate candidate, int generation, CancellationToken ct)
    {
        using var request = GitHubApi.CreateGet(GitHubApi.CopilotUserUrl, candidate.Token, GitHubApi.CopilotApiVersion);
        using var response = await _ctx.Http.SendAsync(request, ct).ConfigureAwait(false);
        var now = _ctx.Time.GetUtcNow();
        var status = (int)response.StatusCode;

        if (GitHubApi.IsRateLimited(response))
        {
            _ctx.Log.Warn($"Copilot: rate limited (HTTP {status}) using {candidate}");
            return Status(ProviderStatus.RateLimited, CopilotMessages.RateLimited, GitHubApi.RetryAfter(response, now));
        }

        if (IsRejection(response.StatusCode))
        {
            _ctx.Log.Info($"Copilot: token from {candidate} rejected with HTTP {status}");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _ctx.Log.Warn($"Copilot: copilot_internal/user returned HTTP {status}");
            return Status(ProviderStatus.Error, CopilotMessages.ServerError(status));
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var usage = CopilotUsageParser.Parse(body, now, _ctx.Time.LocalTimeZone);
        lock (_gate)
        {
            // A sign-out during this fetch invalidates the saved token it was using.
            if (candidate.Kind != CopilotTokenSourceKind.Saved || generation == _signOutGeneration)
            {
                _working = candidate;
            }
        }

        if (usage.PlanLabel is null && usage.Meters.Count == 0)
        {
            return Status(ProviderStatus.Unsupported, CopilotMessages.NoSubscription);
        }

        return new UsageSnapshot(Id, ProviderStatus.Ok, usage.PlanLabel, usage.Meters, now);
    }

    /// <summary>
    /// GET /user login, or null on any failure (the caller falls back to the usage response's login).
    /// Cancellation requested by the caller still propagates.
    /// </summary>
    private async Task<string?> FetchLoginAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = GitHubApi.CreateGet(GitHubApi.UserUrl, token, apiVersion: null);
            using var response = await _ctx.Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _ctx.Log.Warn($"Copilot: GET /user returned HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Json.Str(doc.RootElement, "login") : null;
        }
        catch (HttpRequestException ex)
        {
            _ctx.Log.Warn("Copilot: GET /user failed", ex);
            return null;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _ctx.Log.Warn("Copilot: GET /user timed out", ex);
            return null;
        }
        catch (JsonException ex)
        {
            _ctx.Log.Warn("Copilot: GET /user response was not valid JSON", ex);
            return null;
        }
    }

    private static bool IsRejection(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    private static string DisplayLogin(CopilotSavedToken saved) => saved.Login ?? CopilotMessages.UnknownLogin;

    private UsageSnapshot Status(ProviderStatus status, string message, TimeSpan? retryAfter = null) =>
        UsageSnapshot.WithStatus(Id, status, _ctx.Time.GetUtcNow(), message, retryAfter);
}
