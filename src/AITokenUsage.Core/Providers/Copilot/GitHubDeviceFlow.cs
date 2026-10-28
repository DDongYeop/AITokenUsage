using System.Net.Http.Headers;
using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>
/// GitHub OAuth device flow with the Copilot GitHub App client. Errors arrive in HTTP 200 bodies,
/// so "error" is checked before "access_token". Polling waits with the injected TimeProvider.
/// </summary>
public sealed class GitHubDeviceFlow(HttpClient http, TimeProvider time)
{
    public const string ClientId = "Iv1.b507a08c87ecfe98";
    public const string Scope = "read:user";
    public const string DeviceCodeUrl = "https://github.com/login/device/code";
    public const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    public const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private const int MaxConsecutiveNetworkFailures = 3;
    private static readonly Uri DefaultVerificationUri = new("https://github.com/login/device");
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultExpiresIn = TimeSpan.FromSeconds(900);
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    public async Task<DeviceCodeInfo> StartAsync(CancellationToken ct)
    {
        var root = await PostFormAsync(DeviceCodeUrl,
            new Dictionary<string, string> { ["client_id"] = ClientId, ["scope"] = Scope }, ct).ConfigureAwait(false);

        if (Json.Str(root, "error") is { } error)
        {
            throw new DeviceFlowException(MapError(error));
        }

        var deviceCode = Json.Str(root, "device_code");
        var userCode = Json.Str(root, "user_code");
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
        {
            throw new DeviceFlowException(DeviceFlowError.Unknown);
        }

        var verificationUri = Uri.TryCreate(Json.Str(root, "verification_uri"), UriKind.Absolute, out var uri)
                              && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : DefaultVerificationUri;
        var expiresIn = Json.Num(root, "expires_in") is double e && e > 0 ? TimeSpan.FromSeconds(e) : DefaultExpiresIn;
        var interval = Json.Num(root, "interval") is double i && i > 0 ? TimeSpan.FromSeconds(i) : DefaultInterval;
        return new DeviceCodeInfo(deviceCode, userCode, verificationUri, expiresIn, interval);
    }

    /// <summary>Polls until GitHub returns an access token. Throws DeviceFlowException or OperationCanceledException.</summary>
    public async Task<string> WaitForTokenAsync(DeviceCodeInfo code, CancellationToken ct)
    {
        var interval = code.Interval > TimeSpan.Zero ? code.Interval : DefaultInterval;
        var deadline = time.GetUtcNow() + code.ExpiresIn;
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["device_code"] = code.DeviceCode,
            ["grant_type"] = GrantType,
        };
        var networkFailures = 0;

        while (true)
        {
            await Task.Delay(interval, time, ct).ConfigureAwait(false);
            if (time.GetUtcNow() >= deadline)
            {
                throw new DeviceFlowException(DeviceFlowError.Expired);
            }

            JsonElement root;
            try
            {
                root = await PostFormAsync(AccessTokenUrl, form, ct).ConfigureAwait(false);
                networkFailures = 0;
            }
            catch (DeviceFlowException ex) when (ex.Error == DeviceFlowError.Network)
            {
                networkFailures++;
                if (networkFailures >= MaxConsecutiveNetworkFailures)
                {
                    throw;
                }

                continue;
            }

            switch (Json.Str(root, "error"))
            {
                case null:
                    var token = Json.Str(root, "access_token");
                    if (string.IsNullOrEmpty(token))
                    {
                        throw new DeviceFlowException(DeviceFlowError.Unknown);
                    }

                    return token;
                case "authorization_pending":
                    break;
                case "slow_down":
                    var serverInterval = Json.Num(root, "interval") is double s && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;
                    var increased = interval + SlowDownStep;
                    interval = serverInterval > increased ? serverInterval : increased;
                    break;
                case { } error:
                    throw new DeviceFlowException(MapError(error));
            }
        }
    }

    private static DeviceFlowError MapError(string error) => error switch
    {
        "expired_token" => DeviceFlowError.Expired,
        "access_denied" => DeviceFlowError.Denied,
        "device_flow_disabled" => DeviceFlowError.Disabled,
        _ => DeviceFlowError.Unknown,
    };

    /// <summary>POSTs a form with Accept: application/json and returns the JSON object (cloned).</summary>
    private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(GitHubApi.UserAgent);

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500)
            {
                throw new DeviceFlowException(DeviceFlowError.Network);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DeviceFlowException(DeviceFlowError.Unknown);
            }

            return doc.RootElement.Clone();
        }
        catch (HttpRequestException ex)
        {
            throw new DeviceFlowException(DeviceFlowError.Network, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DeviceFlowException(DeviceFlowError.Network, ex);
        }
        catch (JsonException ex)
        {
            throw new DeviceFlowException(DeviceFlowError.Unknown, ex);
        }
    }
}
