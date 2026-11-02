using System.Net;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static AITokenUsage.Core.Tests.Providers.Claude.ClaudeOwnedTestData;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeCliCredentialSourceTests
{
    private const string CredentialsPath = @"C:\Users\test\.claude\.credentials.json";

    private readonly FakeFileSystem _files = new();

    private void Write(string json)
    {
        _files.Remove(CredentialsPath);
        _files.AddFile(CredentialsPath, json, ClaudeTestData.Now.UtcDateTime);
    }

    [Fact]
    public void Watches_the_credentials_file_and_never_renews()
    {
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);

        Assert.Equal(new[] { CredentialsPath }, source.WatchPaths);
        Assert.False(source.RenewsItself);
    }

    [Fact]
    public async Task Read_returns_file_credentials()
    {
        Write(ClaudeTestData.Credentials());

        ClaudeCredentialReadResult result = await new ClaudeCliCredentialSource(_files, CredentialsPath).ReadAsync("claude-code/1.0.0", CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(ClaudeTestData.TokenA, result.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Recover_returns_null_when_the_token_did_not_change()
    {
        Write(ClaudeTestData.Credentials());
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);
        ClaudeCredentialReadResult first = await source.ReadAsync("ua", CancellationToken.None);

        Assert.Null(await source.RecoverAsync(first.Credentials!, "ua", CancellationToken.None));
    }

    [Fact]
    public async Task Recover_returns_the_changed_token()
    {
        Write(ClaudeTestData.Credentials());
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);
        ClaudeCredentialReadResult first = await source.ReadAsync("ua", CancellationToken.None);
        Write(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(first.Credentials!, "ua", CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.Ok, recovered.State);
        Assert.Equal(ClaudeTestData.TokenB, recovered.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Recover_passes_a_failed_state_through()
    {
        Write(ClaudeTestData.Credentials());
        var source = new ClaudeCliCredentialSource(_files, CredentialsPath);
        ClaudeCredentialReadResult first = await source.ReadAsync("ua", CancellationToken.None);
        Write(ClaudeTestData.Credentials(accessToken: ""));

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(first.Credentials!, "ua", CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.SignedOut, recovered.State);
    }
}

public class ClaudeOwnedCredentialSourceTests
{
    private const string UserAgent = "claude-code/2.1.400";

    private readonly StubHttpHandler _http = new();
    private readonly FakeTimeProvider _time = new(ClaudeTestData.Now);
    private readonly RecordingLog _log = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly Queue<Func<HttpResponseMessage>> _tokenResponses = new();

    public ClaudeOwnedCredentialSourceTests()
    {
        _http.When(StubHttpHandler.Post(ClaudeTokenRenewer.TokenUrl), _ => _tokenResponses.Dequeue()());
    }

    private ClaudeOwnedCredentialSource CreateSource(ISecretStore? secrets = null) => new(
        secrets ?? _secrets,
        SecretName,
        new ClaudeTokenRenewer(new HttpClient(_http, disposeHandler: false), _time, _log),
        _time,
        _log);

    private void Store(ClaudeOwnedCredentials login) => _secrets.Save(SecretName, login.ToJson());

    private ClaudeOwnedCredentials? Stored() => ClaudeOwnedCredentials.FromJson(_secrets.Load(SecretName));

    [Fact]
    public void Has_no_watch_paths_renews_itself_and_no_email_before_loading()
    {
        Store(Login());
        ClaudeOwnedCredentialSource source = CreateSource();

        Assert.Empty(source.WatchPaths);
        Assert.True(source.RenewsItself);
        Assert.Null(source.Email);
    }

    [Fact]
    public async Task Read_loads_the_stored_login_once()
    {
        Store(Login());
        ClaudeOwnedCredentialSource source = CreateSource();

        ClaudeCredentialReadResult result = await source.ReadAsync(UserAgent, CancellationToken.None);
        _secrets.Delete(SecretName);
        ClaudeCredentialReadResult again = await source.ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(AccessToken1, result.Credentials!.AccessToken);
        Assert.Equal(ClaudeCredentialState.Ok, again.State);
        Assert.Equal("me@example.com", source.Email);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("""{"accessToken":"a","refreshToken":"r","expiresAt":1793185200000,"scopes":["user:inference"]}""")]
    public async Task Missing_or_broken_secret_needs_sign_in(string? stored)
    {
        if (stored is not null)
        {
            _secrets.Save(SecretName, stored);
        }

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.ReauthRequired, result.State);
        Assert.Null(result.Credentials);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Secret_store_read_error_is_temporary_and_retried_on_the_next_read()
    {
        var secrets = new ThrowingSecretStore();
        secrets.Save(SecretName, Login().ToJson());
        secrets.ThrowOnLoad = true;
        ClaudeOwnedCredentialSource source = CreateSource(secrets);

        ClaudeCredentialReadResult failed = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.RenewFailed, failed.State);
        Assert.Null(failed.Credentials);
        Assert.Null(source.Email);
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains("UnauthorizedAccessException", StringComparison.Ordinal));

        secrets.ThrowOnLoad = false;
        ClaudeCredentialReadResult next = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.Ok, next.State);
        Assert.Equal(AccessToken1, next.Credentials!.AccessToken);
        Assert.Equal("me@example.com", source.Email);
        Assert.Empty(_http.Requests);
        foreach (string token in AllTokens)
        {
            Assert.DoesNotContain(token, _log.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Replace_rethrows_a_save_failure_and_keeps_the_previous_state()
    {
        var secrets = new ThrowingSecretStore();
        secrets.Save(SecretName, Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)).ToJson());
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        ClaudeOwnedCredentialSource source = CreateSource(secrets);
        Assert.Equal(ClaudeCredentialState.ReauthRequired, (await source.ReadAsync(UserAgent, CancellationToken.None)).State);
        secrets.ThrowOnSave = true;

        await Assert.ThrowsAsync<IOException>(
            () => source.ReplaceAsync(Login(AccessToken3, RefreshToken3) with { Email = "new@example.com" }, CancellationToken.None));

        Assert.Equal(ClaudeCredentialState.ReauthRequired, (await source.ReadAsync(UserAgent, CancellationToken.None)).State);
        Assert.Equal("me@example.com", source.Email);
        Assert.Equal(AccessToken1, ClaudeOwnedCredentials.FromJson(secrets.Load(SecretName))!.AccessToken);
        Assert.Single(_http.Requests);
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains("IOException", StringComparison.Ordinal));
        foreach (string token in AllTokens)
        {
            Assert.DoesNotContain(token, _log.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Replace_save_failure_keeps_the_current_token_in_use()
    {
        var secrets = new ThrowingSecretStore();
        secrets.Save(SecretName, Login().ToJson());
        ClaudeOwnedCredentialSource source = CreateSource(secrets);
        await source.ReadAsync(UserAgent, CancellationToken.None);
        secrets.ThrowOnSave = true;

        await Assert.ThrowsAsync<IOException>(() => source.ReplaceAsync(Login(AccessToken3, RefreshToken3), CancellationToken.None));

        ClaudeCredentialReadResult result = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(AccessToken1, result.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Expiring_token_is_renewed_and_saved()
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(4)));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        ClaudeOwnedCredentialSource source = CreateSource();

        ClaudeCredentialReadResult result = await source.ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(AccessToken2, result.Credentials!.AccessToken);
        Assert.Equal(AccessToken2, Stored()!.AccessToken);
        Assert.Equal(RefreshToken2, Stored()!.RefreshToken);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Token_with_more_than_the_margin_left_is_not_renewed()
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(5).AddSeconds(1)));

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Save_failure_is_logged_and_the_new_token_is_used()
    {
        var secrets = new ThrowingSecretStore();
        secrets.Save(SecretName, Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)).ToJson());
        secrets.ThrowOnSave = true;
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));

        ClaudeCredentialReadResult result = await CreateSource(secrets).ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(AccessToken2, result.Credentials!.AccessToken);
        Assert.Contains(_log.Entries, e => e.Level == "WARN");
        foreach (string token in AllTokens)
        {
            Assert.DoesNotContain(token, _log.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Failed_renewal_of_an_expired_token_keeps_the_stored_tokens_and_tries_again_next_time()
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(-1)));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        ClaudeOwnedCredentialSource source = CreateSource();

        ClaudeCredentialReadResult failed = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.RenewFailed, failed.State);
        Assert.Equal(AccessToken1, Stored()!.AccessToken);

        ClaudeCredentialReadResult next = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.Ok, next.State);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Contains($"\"refresh_token\":\"{RefreshToken1}\"", _http.RequestBodies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Failed_renewal_before_expiry_returns_the_current_token_and_renews_again_next_time(HttpStatusCode status)
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(status, "{}"));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        ClaudeOwnedCredentialSource source = CreateSource();

        ClaudeCredentialReadResult current = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.Ok, current.State);
        Assert.Equal(AccessToken1, current.Credentials!.AccessToken);
        Assert.Equal(AccessToken1, Stored()!.AccessToken);
        Assert.Single(_http.Requests);

        ClaudeCredentialReadResult next = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.Ok, next.State);
        Assert.Equal(AccessToken2, next.Credentials!.AccessToken);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Failed_renewal_before_expiry_is_not_counted_as_a_renewal_for_recover()
    {
        // No sticky flag: the token handed out was not renewed, so a 401 for it still forces one renewal.
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        ClaudeOwnedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(UserAgent, CancellationToken.None);

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(read.Credentials!, UserAgent, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.Ok, recovered.State);
        Assert.Equal(AccessToken2, recovered.Credentials!.AccessToken);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Failed_renewal_at_the_exact_expiry_is_a_temporary_failure()
    {
        Store(Login(expiresAt: ClaudeTestData.Now));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.RenewFailed, result.State);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public async Task Rejected_renewal_stays_signed_out_until_replaced()
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        ClaudeOwnedCredentialSource source = CreateSource();

        Assert.Equal(ClaudeCredentialState.ReauthRequired, (await source.ReadAsync(UserAgent, CancellationToken.None)).State);
        _time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(ClaudeCredentialState.ReauthRequired, (await source.ReadAsync(UserAgent, CancellationToken.None)).State);
        Assert.Single(_http.Requests);
        Assert.NotNull(Stored());

        await source.ReplaceAsync(Login(AccessToken3, RefreshToken3, _time.GetUtcNow().AddHours(8)) with { Email = "new@example.com" }, CancellationToken.None);

        ClaudeCredentialReadResult replaced = await source.ReadAsync(UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.Ok, replaced.State);
        Assert.Equal(AccessToken3, replaced.Credentials!.AccessToken);
        Assert.Equal(AccessToken3, Stored()!.AccessToken);
        Assert.Equal("new@example.com", source.Email);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Expired_refresh_token_needs_sign_in_without_request()
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1), refreshTokenExpiresAt: ClaudeTestData.Now.AddSeconds(-1)));

        ClaudeCredentialReadResult result = await CreateSource().ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.ReauthRequired, result.State);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Replace_before_first_read_is_used_without_loading()
    {
        ClaudeOwnedCredentialSource source = CreateSource();

        await source.ReplaceAsync(Login(), CancellationToken.None);
        ClaudeCredentialReadResult result = await source.ReadAsync(UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(AccessToken1, Stored()!.AccessToken);
    }

    [Fact]
    public async Task Recover_after_a_renewal_in_the_same_read_returns_null()
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        ClaudeOwnedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(UserAgent, CancellationToken.None);

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(read.Credentials!, UserAgent, CancellationToken.None);

        Assert.Null(recovered);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Recover_forces_one_renewal()
    {
        Store(Login());
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        ClaudeOwnedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(UserAgent, CancellationToken.None);

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(read.Credentials!, UserAgent, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.Ok, recovered.State);
        Assert.Equal(AccessToken2, recovered.Credentials!.AccessToken);
        Assert.Equal(AccessToken2, Stored()!.AccessToken);
    }

    [Fact]
    public async Task Recover_in_a_later_read_renews_again()
    {
        Store(Login(expiresAt: ClaudeTestData.Now.AddMinutes(1)));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse()));
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.OK, TokenResponse(AccessToken3, RefreshToken3)));
        ClaudeOwnedCredentialSource source = CreateSource();
        await source.ReadAsync(UserAgent, CancellationToken.None);
        ClaudeCredentialReadResult second = await source.ReadAsync(UserAgent, CancellationToken.None);

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(second.Credentials!, UserAgent, CancellationToken.None);

        Assert.Equal(AccessToken3, recovered!.Credentials!.AccessToken);
        Assert.Contains($"\"refresh_token\":\"{RefreshToken2}\"", _http.RequestBodies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ClaudeCredentialState.ReauthRequired)]
    [InlineData(HttpStatusCode.InternalServerError, ClaudeCredentialState.RenewFailed)]
    public async Task Recover_reports_a_failed_renewal(HttpStatusCode status, ClaudeCredentialState expected)
    {
        Store(Login());
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(status, "{}"));
        ClaudeOwnedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(UserAgent, CancellationToken.None);

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(read.Credentials!, UserAgent, CancellationToken.None);

        Assert.Equal(expected, recovered!.State);
    }

    [Fact]
    public async Task Recover_after_a_replace_returns_the_new_token_without_renewing()
    {
        Store(Login());
        ClaudeOwnedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(UserAgent, CancellationToken.None);
        await source.ReplaceAsync(Login(AccessToken3, RefreshToken3), CancellationToken.None);

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(read.Credentials!, UserAgent, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ClaudeCredentialState.Ok, recovered.State);
        Assert.Equal(AccessToken3, recovered.Credentials!.AccessToken);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Recover_while_sign_in_is_required_does_not_renew()
    {
        Store(Login());
        _tokenResponses.Enqueue(() => StubHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        ClaudeOwnedCredentialSource source = CreateSource();
        ClaudeCredentialReadResult read = await source.ReadAsync(UserAgent, CancellationToken.None);
        ClaudeCredentialReadResult? refused = await source.RecoverAsync(read.Credentials!, UserAgent, CancellationToken.None);
        Assert.Equal(ClaudeCredentialState.ReauthRequired, refused!.State);
        Assert.Single(_http.Requests);

        ClaudeCredentialReadResult? again = await source.RecoverAsync(read.Credentials!, UserAgent, CancellationToken.None);

        Assert.NotNull(again);
        Assert.Equal(ClaudeCredentialState.ReauthRequired, again.State);
        Assert.Null(again.Credentials);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Recover_with_no_saved_login_needs_sign_in_without_http()
    {
        ClaudeOwnedCredentialSource source = CreateSource();

        ClaudeCredentialReadResult? recovered = await source.RecoverAsync(Login().ToCredentials(), UserAgent, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.ReauthRequired, recovered!.State);
        Assert.Empty(_http.Requests);
    }

    /// <summary>An in-memory secret store that can be told to fail.</summary>
    private sealed class ThrowingSecretStore : ISecretStore
    {
        private readonly FakeSecretStore _inner = new();

        public bool ThrowOnSave { get; set; }

        public bool ThrowOnLoad { get; set; }

        public void Save(string name, string secret)
        {
            if (ThrowOnSave)
            {
                throw new IOException("disk full");
            }

            _inner.Save(name, secret);
        }

        public string? Load(string name) => ThrowOnLoad ? throw new UnauthorizedAccessException("denied") : _inner.Load(name);

        public void Delete(string name) => _inner.Delete(name);
    }
}
