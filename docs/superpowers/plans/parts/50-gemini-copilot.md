## Phase 3 — 공급자

> **Task 11–13 공통 전제.** Task 2·3의 산출물이 아래처럼 동작한다고 가정한다. 이 파트의 테스트는 이 가정에만 기댄다.
> - `FakeFileSystem.AddFile(path, content, lastWriteUtc)`의 세 번째 인자는 선택 인자(기본 `null`)다. 경로 비교는 대소문자를 무시한다. 없는 파일은 `ReadAllTextShared`가 `null`을 돌려준다.
> - `FakeEnvironment`는 설정하지 않은 변수에 `null`을 돌려준다. 테스트는 `UserProfile`·`LocalAppData`를 하드코딩하지 않고 속성으로 읽는다.
> - `FakeProcessRunner.FindOnPath`는 `SetPath`하지 않은 명령에 `null`을 돌려준다. `OnRun`은 등록한 처리기로 `RunAsync` 결과를 만든다.
> - `StubHttpHandler`의 응답 함수가 던진 `HttpRequestException`은 그대로 `HttpClient.SendAsync` 밖으로 나온다(네트워크 실패 흉내). 매칭되지 않은 요청은 예외라서 테스트가 실패한다. 이 파트의 테스트는 URL마다 처리기를 하나만 등록하므로 매칭 우선순위에 기대지 않는다.
> - `Json.Str/Num/Bool/Obj/Arr/Iso`는 속성이 없거나 형식이 다르면 `null`을 돌려준다. `Json.Num`은 숫자 문자열(`"300"`)도 읽는다. 코드는 `Json.*`를 부르기 전에 대상이 JSON 객체인지 직접 확인한다.
> - 공급자 HTTP는 모두 `ctx.Http`로 보낸다(Task 2 `HttpClientFactory`: 15초 타임아웃, 리디렉트 끔). 로그에는 상태 코드와 출처 설명만 남기고 **토큰·Authorization·응답 원문은 남기지 않는다.** 토큰을 담는 record는 모두 `ToString()`을 재정의해 토큰을 숨긴다.
> - 각 공급자의 `FetchAsync`는 `SemaphoreSlim(1,1)`로 직렬화한다(주기 조회와 "지금 새로고침"이 겹쳐도 메모리 상태가 꼬이지 않게).

---

### Task 11: Gemini 공급자 (`GeminiPaths`, `GeminiCredentialReader`, `GeminiTokenRefresher`, `GeminiQuotaParser`, `GeminiUsageProvider`)

Gemini CLI가 남긴 OAuth 자격 증명으로 Code Assist(`cloudcode-pa`) 일간 한도를 읽는다. 근거는 spec §5.3, `docs/research/gemini-usage-api.md` §1–§3, §5다. spec 구조도의 `GeminiQuotaClient`(HTTP 호출)는 `GeminiUsageProvider` 안의 비공개 메서드로 합쳤다. 호출이 두 개뿐이고 401 재시도·캐시 상태를 공급자가 쥐고 있어야 하기 때문이다.

**`FetchAsync` 동작 순서**

| 순서 | 조건 | 결과 |
|---|---|---|
| 1 | `settings.json`(JSONC)의 `security.auth.selectedType`(없으면 최상위 `selectedAuthType`)이 `gemini-api-key`·`api-key`·`vertex-ai`·`gateway` | `Unsupported`, "API 키/Vertex — 사용량 조회 불가". 네트워크 호출 없음 |
| 2 | `<home>\.gemini\oauth_creds.json` → 없으면 Credential Manager `gemini-cli-oauth/main-account` → 둘 다 없음 | `NotInstalled` |
| 2' | 파일은 있는데 50/150/300ms 간격으로 다시 읽어도 JSON이 깨져 있고 CredMan도 없음 | `Error`, "자격 증명 파일을 읽지 못함 — 잠시 후 다시 시도". **로그아웃으로 취급하지 않는다** |
| 3 | access token이 비었거나 `expiry ≤ now + 5분` | 공개 클라이언트로 `POST https://oauth2.googleapis.com/token`(form) 갱신. 결과는 **메모리에만** 둔다. `invalid_grant` → `SignedOut`, "터미널에서 gemini 실행해 다시 로그인" |
| 4 | `POST …/v1internal:loadCodeAssist` | 계정(refresh token)별로 **1시간 캐시** |
| 5 | `currentTier` 없음 ∧ `ineligibleTiers[].reasonCode/reasonMessage`에 `UNSUPPORTED_CLIENT` ∧ `paidTier.name` 없음 ∧ id_token에 `hd` 없음 | `Unsupported`, "개인 계정 지원 종료 (2026-06-18) — Antigravity 사용" |
| 6 | 프로젝트 = 응답 `cloudaicompanionProject`(문자열 또는 `{id}`/`{projectId}`) → 환경 변수 `GOOGLE_CLOUD_PROJECT`/`GOOGLE_CLOUD_PROJECT_ID` → `<home>\.gemini\.env` → `<home>\.env`. 그래도 없으면 | `currentTier`가 있으면 `Error`, "GOOGLE_CLOUD_PROJECT 설정 필요". 없으면 `SignedOut`, "터미널에서 gemini를 한 번 실행해 설정을 마치세요". **`onboardUser`는 절대 호출하지 않는다** |
| 7 | `POST …/v1internal:retrieveUserQuota`, 본문 `{"project":"<id>"}` | 403 + `error.details[].reason == "SUBSCRIPTION_REQUIRED"`: (UNSUPPORTED_CLIENT ∧ tier ≠ `standard-tier` ∧ paid name 없음 ∧ hd 없음)이면 종료 메시지, 아니면 "Code Assist 라이선스 없음". 429 → `RateLimited`(Retry-After). 그 밖의 비정상 본문에 종료 문구가 있으면 종료 메시지 |
| 8 | 두 호출 어디서든 401 | 이번 조회에서 아직 갱신하지 않았으면 한 번 갱신하고 재시도. 또 401이면 `SignedOut` |
| 9 | 200 | 버킷을 `modelId` 소문자의 부분 문자열 `flash-lite` → `flash` → `pro`로 묶고 묶음별 최소 `remainingFraction`, 사용 % = `(1 − f) × 100`. 미터 `pro`(Pro), `flash`(Fl), `flash-lite`(Lt). 플랜 = `paidTier.name` → `standard-tier` Standard / `free-tier`+hd Workspace / `free-tier` Free / `legacy-tier` Legacy |

**Files:**
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiPaths.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiAuthType.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiCredentials.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiCredentialReader.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiTokenRefresher.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiOAuthClientLocator.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiDotEnv.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiProjectResolver.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiCodeAssistInfo.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiCodeAssistParser.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiQuotaParser.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiMessages.cs`
- Create: `src/AITokenUsage.Core/Providers/Gemini/GeminiUsageProvider.cs`
- Create (fixtures): `tests/AITokenUsage.Core.Tests/Fixtures/gemini/` 아래 `settings-oauth-jsonc.json`, `settings-api-key.json`, `settings-legacy-vertex.json`, `oauth_creds.json`, `oauth_creds-workspace.json`, `credman-main-account.json`, `refresh-ok.json`, `refresh-invalid-grant.json`, `load-standard.json`, `load-paid-object-project.json`, `load-shutdown.json`, `load-no-project.json`, `load-free-unsupported-client.json`, `quota-buckets.json`, `quota-subscription-required.json`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiCredentialReaderTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiTokenRefresherTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiOAuthClientLocatorTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiProjectResolverTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiCodeAssistParserTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiQuotaParserTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiUsageProviderTests.cs`
- Modify (Step 1에서 규칙이 없을 때만): `tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj`

**Interfaces:**
- Consumes (Task 2): `ProviderContext(HttpClient Http, TimeProvider Time, IFileSystem Files, IEnvironment Env, IProcessRunner Processes, ICredentialStore Credentials, ISecretStore Secrets, IAppLog Log)`, `IFileSystem.ReadAllTextShared(string path) : string?`, `IEnvironment.GetVariable(string) : string?`, `IEnvironment.UserProfile`, `ICredentialStore.ReadGeneric(string targetName) : string?`, `IAppLog.Info/Warn`, `IUsageProvider`, `UsageMeter(...)`, `UsageSnapshot(...)`, `UsageSnapshot.WithStatus(string, ProviderStatus, DateTimeOffset, string?, TimeSpan?)`, `ProviderStatus`, `ProviderIds.Gemini`, `MeterOption(string Key, string Label)`. 테스트: `FakeFileSystem`, `FakeEnvironment`, `FakeCredentialStore`, `StubHttpHandler`, `Fixture.Read`, `TestContexts.Create`.
- Consumes (Task 3): `Json.Str/Num/Obj/Arr/Iso`, `Jwt.TryReadPayload(string?, out JsonElement)`, `SharedJson.Lenient`.
- Produces (Task 13 `ProviderCatalog`, Task 17–19 UI가 사용):
  - `GeminiUsageProvider(ProviderContext ctx) : IUsageProvider` — `Id = "gemini"`, `DisplayName = "Gemini"`, `MinInterval = 2분`, `WatchPaths = [<home>\.gemini\oauth_creds.json, <home>\.gemini\settings.json]`, `StaticMeterOptions = [pro "Pro 일간", flash "Flash 일간", flash-lite "Flash-Lite 일간"]`, `const string LoadCodeAssistUrl`, `const string RetrieveUserQuotaUrl`.
  - `GeminiPaths(IEnvironment env)` — `Home`, `GeminiDir`, `OAuthCredsPath`, `SettingsPath`, `GeminiDotEnvPath`, `HomeDotEnvPath`.
  - `static GeminiAuthType` — `string? Read(IFileSystem files, string settingsPath)`, `bool IsUnsupported(string? authType)`.
  - `GeminiCredentialReader(IFileSystem files, ICredentialStore credentialStore, GeminiPaths paths)` — `const string CredentialTarget = "gemini-cli-oauth/main-account"`, `Task<GeminiCredentialResult> ReadAsync(CancellationToken ct)`, `static GeminiCredentials? ParseOAuthFile(string json)`, `static GeminiCredentials? ParseCredentialManagerBlob(string json)`.
  - `record GeminiCredentials(string? AccessToken, string? RefreshToken, DateTimeOffset? Expiry, string? IdToken, GeminiCredentialSource Source)`, `enum GeminiCredentialSource { OAuthFile, CredentialManager }`, `enum GeminiCredentialStatus { Found, NotFound, Unreadable }`, `record GeminiCredentialResult(GeminiCredentialStatus Status, GeminiCredentials? Credentials)`.
  - `GeminiTokenRefresher(HttpClient http, TimeProvider time, Func<GeminiOAuthClient?> client)` — `const TokenUrl`, `Task<GeminiRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)`; `record GeminiRefreshResult(GeminiRefreshStatus Status, string? AccessToken, DateTimeOffset? Expiry, string? IdToken, int HttpStatus)`; `enum GeminiRefreshStatus { Ok, InvalidGrant, Failed, ClientUnavailable }`.
  - `record GeminiOAuthClient(string ClientId, string ClientSecret)`(ToString은 secret을 숨김); `GeminiOAuthClientLocator(IEnvironment env, IFileSystem files, IProcessRunner processes)` — `GeminiOAuthClient? Locate()`(찾은 값은 캐시), `static GeminiOAuthClient? ParseScript(string js)`, `const EnvClientId = "GEMINI_OAUTH_CLIENT_ID"`, `const EnvClientSecret = "GEMINI_OAUTH_CLIENT_SECRET"`. **클라이언트 값은 저장소·소스에 넣지 않는다**(GitHub Push Protection이 차단). 실행 시 설치된 gemini-cli 패키지에서 읽는다.
  - `static GeminiDotEnv.Parse(string content) : IReadOnlyDictionary<string, string>`; `GeminiProjectResolver(IEnvironment env, IFileSystem files, GeminiPaths paths).Resolve() : string?`.
  - `record GeminiCodeAssistInfo(bool HasCurrentTier, string? TierId, string? PaidTierName, string? Project, bool HasUnsupportedClient)` — `bool IsConsumerShutdown(bool hasHostedDomain)`, `bool IsShutdownLicenseDenial(bool hasHostedDomain)`.
  - `static GeminiCodeAssistParser` — `Parse(string json)`, `PlanLabel(GeminiCodeAssistInfo, bool hasHostedDomain) : string?`, `HasHostedDomain(string? idToken) : bool`, `HasErrorReason(string body, string reason) : bool`, `LooksLikeShutdown(string body) : bool`.
  - `static GeminiQuotaParser` — `IReadOnlyList<MeterOption> MeterOptions`, `string? Classify(string modelId)`, `IReadOnlyList<UsageMeter> Parse(string json)`.
  - `static GeminiMessages` — 한국어 상태 메시지 상수(`ApiKeyMode`, `NotInstalled`, `SignInAgain`, `CredentialsUnreadable`, `RefreshFailed`, `ConsumerShutdown`, `NoLicense`, `ProjectRequired`, `NotOnboarded`, `RateLimited`, `Network`, `BadResponse`, `NoQuotaData`, `ServerError(int)`).

- [ ] **Step 1: 픽스처 복사 규칙 확인**

Run (PowerShell, 저장소 루트): `Select-String -Path tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj -Pattern 'Fixtures'`

Expected: `<None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />`가 들어 있는 줄이 출력된다. 아무 줄도 나오지 않으면 csproj의 `</Project>` 바로 앞에 다음을 넣는다.

```xml
  <ItemGroup>
    <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: 설정·자격 증명 픽스처 작성**

모두 가짜 값이다. `id_token`은 직접 만든 무서명 JWT다: 헤더 `{"alg":"none","typ":"JWT"}`, 페이로드 `{"iss":"https://accounts.google.com","email":"user@example.com"}`(개인) / `{"iss":"https://accounts.google.com","email":"dev@example.com","hd":"example.com"}`(Workspace), 서명 자리는 `fake-signature`의 base64url. `expiry_date`/`expiresAt` `1793160000000`은 2026-10-28T04:00:00Z(ms)다.

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/settings-oauth-jsonc.json` (주석과 끝 쉼표가 있는 JSONC):

```jsonc
{
  // Gemini CLI settings are JSONC: comments and trailing commas are allowed.
  "general": {
    "vimMode": false,
  },
  /* auth block */
  "security": {
    "auth": {
      "selectedType": "oauth-personal",
    },
  },
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/settings-api-key.json`:

```json
{
  "security": {
    "auth": {
      "selectedType": "gemini-api-key"
    }
  }
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/settings-legacy-vertex.json` (레거시 최상위 키):

```json
{
  "selectedAuthType": "vertex-ai",
  "theme": "Default"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/oauth_creds.json` (개인 계정, `hd` 없음):

```json
{
  "access_token": "test-access-token",
  "scope": "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile",
  "token_type": "Bearer",
  "id_token": "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJpc3MiOiJodHRwczovL2FjY291bnRzLmdvb2dsZS5jb20iLCJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20ifQ.ZmFrZS1zaWduYXR1cmU",
  "expiry_date": 1793160000000,
  "refresh_token": "test-refresh-token"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/oauth_creds-workspace.json` (`hd` 클레임 있음):

```json
{
  "access_token": "test-workspace-access-token",
  "scope": "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile",
  "token_type": "Bearer",
  "id_token": "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJpc3MiOiJodHRwczovL2FjY291bnRzLmdvb2dsZS5jb20iLCJlbWFpbCI6ImRldkBleGFtcGxlLmNvbSIsImhkIjoiZXhhbXBsZS5jb20ifQ.ZmFrZS1zaWduYXR1cmU",
  "expiry_date": 1793160000000,
  "refresh_token": "test-workspace-refresh-token"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/credman-main-account.json` (keytar가 CredMan blob에 쓰는 UTF-8 JSON, `id_token` 없음):

```json
{"serverName":"main-account","token":{"accessToken":"test-credman-access-token","refreshToken":"test-credman-refresh-token","tokenType":"Bearer","scope":"https://www.googleapis.com/auth/cloud-platform","expiresAt":1793160000000},"updatedAt":1793150000000}
```

- [ ] **Step 3: 실패하는 테스트 작성 — 경로·인증 방식·자격 증명 읽기**

`tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiCredentialReaderTests.cs`:

```csharp
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiCredentialReaderTests
{
    private static readonly DateTimeOffset FixtureExpiry = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeCredentialStore _credentials = new();

    [Fact]
    public void Paths_default_to_user_profile()
    {
        var paths = new GeminiPaths(_env);

        Assert.Equal(Path.Combine(_env.UserProfile, ".gemini"), paths.GeminiDir);
        Assert.Equal(Path.Combine(_env.UserProfile, ".gemini", "oauth_creds.json"), paths.OAuthCredsPath);
        Assert.Equal(Path.Combine(_env.UserProfile, ".gemini", "settings.json"), paths.SettingsPath);
    }

    [Fact]
    public void Paths_treat_GEMINI_CLI_HOME_as_the_home_directory()
    {
        _env.Set("GEMINI_CLI_HOME", @"D:\alt-home");

        var paths = new GeminiPaths(_env);

        Assert.Equal(@"D:\alt-home\.gemini\oauth_creds.json", paths.OAuthCredsPath);
        Assert.Equal(@"D:\alt-home\.gemini\settings.json", paths.SettingsPath);
        Assert.Equal(@"D:\alt-home\.gemini\.env", paths.GeminiDotEnvPath);
        Assert.Equal(@"D:\alt-home\.env", paths.HomeDotEnvPath);
    }

    [Fact]
    public void Paths_ignore_empty_GEMINI_CLI_HOME()
    {
        _env.Set("GEMINI_CLI_HOME", "");

        var paths = new GeminiPaths(_env);

        Assert.Equal(_env.UserProfile, paths.Home);
    }

    [Fact]
    public void AuthType_reads_jsonc_settings_with_comments_and_trailing_commas()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.SettingsPath, Fixture.Read("gemini/settings-oauth-jsonc.json"));

        var authType = GeminiAuthType.Read(_files, paths.SettingsPath);

        Assert.Equal("oauth-personal", authType);
        Assert.False(GeminiAuthType.IsUnsupported(authType));
    }

    [Theory]
    [InlineData("gemini/settings-api-key.json", "gemini-api-key")]
    [InlineData("gemini/settings-legacy-vertex.json", "vertex-ai")]
    public void AuthType_detects_api_key_and_vertex_modes(string fixture, string expected)
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.SettingsPath, Fixture.Read(fixture));

        var authType = GeminiAuthType.Read(_files, paths.SettingsPath);

        Assert.Equal(expected, authType);
        Assert.True(GeminiAuthType.IsUnsupported(authType));
    }

    [Theory]
    [InlineData("gemini-api-key", true)]
    [InlineData("api-key", true)]
    [InlineData("vertex-ai", true)]
    [InlineData("gateway", true)]
    [InlineData("oauth-personal", false)]
    [InlineData("cloud-shell", false)]
    [InlineData(null, false)]
    public void IsUnsupported_matches_the_spec_list(string? authType, bool expected)
    {
        Assert.Equal(expected, GeminiAuthType.IsUnsupported(authType));
    }

    [Fact]
    public void AuthType_is_null_when_settings_are_missing_or_broken()
    {
        var paths = new GeminiPaths(_env);
        Assert.Null(GeminiAuthType.Read(_files, paths.SettingsPath));

        _files.AddFile(paths.SettingsPath, "{ \"security\": ");
        Assert.Null(GeminiAuthType.Read(_files, paths.SettingsPath));
    }

    [Fact]
    public async Task Reads_oauth_creds_file()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.OAuthCredsPath, Fixture.Read("gemini/oauth_creds.json"));

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Found, result.Status);
        var creds = Assert.IsType<GeminiCredentials>(result.Credentials);
        Assert.Equal("test-access-token", creds.AccessToken);
        Assert.Equal("test-refresh-token", creds.RefreshToken);
        Assert.Equal(FixtureExpiry, creds.Expiry);
        Assert.NotNull(creds.IdToken);
        Assert.Equal(GeminiCredentialSource.OAuthFile, creds.Source);
    }

    [Fact]
    public async Task Falls_back_to_credential_manager_json()
    {
        var paths = new GeminiPaths(_env);
        _credentials.Add(GeminiCredentialReader.CredentialTarget, "main-account", Fixture.Read("gemini/credman-main-account.json"));

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Found, result.Status);
        var creds = Assert.IsType<GeminiCredentials>(result.Credentials);
        Assert.Equal("test-credman-access-token", creds.AccessToken);
        Assert.Equal("test-credman-refresh-token", creds.RefreshToken);
        Assert.Equal(FixtureExpiry, creds.Expiry);
        Assert.Null(creds.IdToken);
        Assert.Equal(GeminiCredentialSource.CredentialManager, creds.Source);
    }

    [Fact]
    public async Task Returns_NotFound_when_no_source_exists()
    {
        var result = await CreateReader(new GeminiPaths(_env)).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.NotFound, result.Status);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public async Task Corrupt_file_is_Unreadable_not_NotFound()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.OAuthCredsPath, "{ \"access_token\": \"test-acc");

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Unreadable, result.Status);
    }

    [Fact]
    public async Task Corrupt_file_still_falls_back_to_credential_manager()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.OAuthCredsPath, "not json");
        _credentials.Add(GeminiCredentialReader.CredentialTarget, "main-account", Fixture.Read("gemini/credman-main-account.json"));

        var result = await CreateReader(paths).ReadAsync(CancellationToken.None);

        Assert.Equal(GeminiCredentialStatus.Found, result.Status);
        Assert.Equal(GeminiCredentialSource.CredentialManager, result.Credentials!.Source);
    }

    [Fact]
    public void ToString_never_contains_tokens()
    {
        var creds = GeminiCredentialReader.ParseOAuthFile(Fixture.Read("gemini/oauth_creds.json"))!;

        var text = creds.ToString();

        Assert.DoesNotContain("test-access-token", text);
        Assert.DoesNotContain("test-refresh-token", text);
    }

    private GeminiCredentialReader CreateReader(GeminiPaths paths) => new(_files, _credentials, paths);
}
```

- [ ] **Step 4: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GeminiCredentialReaderTests"`

Expected: 빌드 실패. `error CS0234: The type or namespace name 'Gemini' does not exist in the namespace 'AITokenUsage.Core.Providers'`

- [ ] **Step 5: 구현 — 경로, 인증 방식, 자격 증명 형식, 읽기**

재시도 대기(50/150/300ms)는 일부러 `TimeProvider`가 아닌 실제 시간이다. CLI가 임시 파일 + rename으로 교체하는 순간을 비켜 가는 짧은 대기라서, 가짜 시계에 묶으면 테스트가 멈춘다. 그래서 `Corrupt_file_is_Unreadable_not_NotFound`는 약 0.5초 걸린다.

`src/AITokenUsage.Core/Providers/Gemini/GeminiPaths.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// Gemini CLI file locations. GEMINI_CLI_HOME replaces the home directory itself
/// (it does not point at the .gemini folder).
/// </summary>
public sealed class GeminiPaths
{
    public GeminiPaths(IEnvironment env)
    {
        var overrideHome = env.GetVariable("GEMINI_CLI_HOME");
        Home = string.IsNullOrWhiteSpace(overrideHome) ? env.UserProfile : overrideHome.Trim();
        GeminiDir = Path.Combine(Home, ".gemini");
        OAuthCredsPath = Path.Combine(GeminiDir, "oauth_creds.json");
        SettingsPath = Path.Combine(GeminiDir, "settings.json");
        GeminiDotEnvPath = Path.Combine(GeminiDir, ".env");
        HomeDotEnvPath = Path.Combine(Home, ".env");
    }

    public string Home { get; }
    public string GeminiDir { get; }
    public string OAuthCredsPath { get; }
    public string SettingsPath { get; }
    public string GeminiDotEnvPath { get; }
    public string HomeDotEnvPath { get; }
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiAuthType.cs`:

```csharp
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>Reads the auth type selected in the Gemini CLI settings.json (JSONC).</summary>
public static class GeminiAuthType
{
    private static readonly HashSet<string> UnsupportedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "gemini-api-key", "api-key", "vertex-ai", "gateway" };

    /// <summary>security.auth.selectedType, else legacy top-level selectedAuthType. Missing/broken file → null.</summary>
    public static string? Read(IFileSystem files, string settingsPath)
    {
        string? text;
        try
        {
            text = files.ReadAllTextShared(settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, SharedJson.Lenient);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (Json.Obj(root, "security") is { } security
                && Json.Obj(security, "auth") is { } auth
                && Json.Str(auth, "selectedType") is { Length: > 0 } selected)
            {
                return selected;
            }

            return Json.Str(root, "selectedAuthType");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsUnsupported(string? authType) => authType is not null && UnsupportedTypes.Contains(authType);
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiCredentials.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Gemini;

public enum GeminiCredentialSource { OAuthFile, CredentialManager }

public enum GeminiCredentialStatus { Found, NotFound, Unreadable }

public sealed record GeminiCredentials(
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? Expiry,
    string? IdToken,
    GeminiCredentialSource Source)
{
    // Never print tokens (records generate a ToString that would include them).
    public override string ToString() => $"GeminiCredentials {{ Source = {Source}, Expiry = {Expiry:O} }}";
}

public sealed record GeminiCredentialResult(GeminiCredentialStatus Status, GeminiCredentials? Credentials);
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiCredentialReader.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// Reads Gemini CLI OAuth credentials: oauth_creds.json first, then the Credential Manager
/// entry written by keytar in encrypted-storage mode. Read-only: never writes CLI files.
/// </summary>
public sealed class GeminiCredentialReader(IFileSystem files, ICredentialStore credentialStore, GeminiPaths paths)
{
    public const string CredentialTarget = "gemini-cli-oauth/main-account";

    // Real-time pauses between re-reads while the CLI is replacing the file (temp file + rename).
    // Deliberately not tied to TimeProvider: they are tiny and must not stall under a fake clock.
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)];

    public async Task<GeminiCredentialResult> ReadAsync(CancellationToken ct)
    {
        var fromFile = await ReadOAuthFileAsync(ct).ConfigureAwait(false);
        if (fromFile.Status == GeminiCredentialStatus.Found)
        {
            return fromFile;
        }

        var fromCredentialManager = ReadCredentialManager();
        if (fromCredentialManager is not null)
        {
            return new GeminiCredentialResult(GeminiCredentialStatus.Found, fromCredentialManager);
        }

        return fromFile;
    }

    public static GeminiCredentials? ParseOAuthFile(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new GeminiCredentials(
                Json.Str(root, "access_token"),
                Json.Str(root, "refresh_token"),
                FromEpochMilliseconds(Json.Num(root, "expiry_date")),
                Json.Str(root, "id_token"),
                GeminiCredentialSource.OAuthFile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static GeminiCredentials? ParseCredentialManagerBlob(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Json.Obj(root, "token") is not { } token)
            {
                return null;
            }

            return new GeminiCredentials(
                Json.Str(token, "accessToken"),
                Json.Str(token, "refreshToken"),
                FromEpochMilliseconds(Json.Num(token, "expiresAt")),
                IdToken: null,
                GeminiCredentialSource.CredentialManager);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<GeminiCredentialResult> ReadOAuthFileAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = files.ReadAllTextShared(paths.OAuthCredsPath);
                if (text is null)
                {
                    return new GeminiCredentialResult(GeminiCredentialStatus.NotFound, null);
                }

                var parsed = ParseOAuthFile(text);
                if (parsed is not null)
                {
                    return new GeminiCredentialResult(GeminiCredentialStatus.Found, parsed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transient sharing/rename race: retry below.
            }

            if (attempt >= RetryDelays.Length)
            {
                // A parse failure is NOT a sign-out; the provider reports Error and keeps the last good value.
                return new GeminiCredentialResult(GeminiCredentialStatus.Unreadable, null);
            }

            await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    private GeminiCredentials? ReadCredentialManager()
    {
        string? blob;
        try
        {
            blob = credentialStore.ReadGeneric(CredentialTarget);
        }
        catch (Win32Exception)
        {
            return null;
        }

        return blob is null ? null : ParseCredentialManagerBlob(blob);
    }

    private static DateTimeOffset? FromEpochMilliseconds(double? milliseconds)
    {
        const double maxMilliseconds = 253_402_300_799_999; // 9999-12-31T23:59:59.999Z
        return milliseconds is > 0 and < maxMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds.Value)
            : null;
    }
}
```

- [ ] **Step 6: 테스트 실행 → 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GeminiCredentialReaderTests"`

Expected: `Passed!  - Failed:     0, Passed:    20`

- [ ] **Step 7: 토큰 갱신 응답 픽스처 작성**

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/refresh-ok.json` (구글 문서의 응답 형태, `id_token`·`refresh_token` 없음):

```json
{
  "access_token": "test-refreshed-access-token",
  "expires_in": 3599,
  "scope": "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile",
  "token_type": "Bearer"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/refresh-invalid-grant.json` (HTTP 400 본문):

```json
{
  "error": "invalid_grant",
  "error_description": "Token has been expired or revoked."
}
```

- [ ] **Step 8: 실패하는 테스트 작성 — 토큰 갱신과 프로젝트 해석**

`tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiTokenRefresherTests.cs`:

```csharp
using System.Net;
using System.Text;
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiTokenRefresherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _http = new();
    private string? _body;
    private string? _contentType;

    [Fact]
    public async Task Posts_form_encoded_public_client_and_returns_new_token()
    {
        GivenTokenEndpoint(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json"));

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.Ok, result.Status);
        Assert.Equal("test-refreshed-access-token", result.AccessToken);
        Assert.Equal(Now.AddSeconds(3599), result.Expiry);
        Assert.Null(result.IdToken);
        Assert.Equal("application/x-www-form-urlencoded", _contentType);
        Assert.Contains("client_id=test-client-id.apps.googleusercontent.com", _body);
        Assert.Contains("client_secret=test-client-secret", _body);
        Assert.Contains("refresh_token=test-refresh-token", _body);
        Assert.Contains("grant_type=refresh_token", _body);
    }

    [Fact]
    public async Task Invalid_grant_is_reported_separately()
    {
        GivenTokenEndpoint(HttpStatusCode.BadRequest, Fixture.Read("gemini/refresh-invalid-grant.json"));

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.InvalidGrant, result.Status);
        Assert.Null(result.AccessToken);
    }

    [Fact]
    public async Task Server_error_is_Failed_with_status()
    {
        GivenTokenEndpoint(HttpStatusCode.InternalServerError, "oops");

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.Failed, result.Status);
        Assert.Equal(500, result.HttpStatus);
    }

    [Fact]
    public async Task Result_ToString_hides_the_token()
    {
        GivenTokenEndpoint(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json"));

        var result = await CreateRefresher().RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.DoesNotContain("test-refreshed-access-token", result.ToString());
    }

    [Fact]
    public async Task Missing_client_returns_ClientUnavailable_without_calling_the_network()
    {
        var refresher = new GeminiTokenRefresher(new HttpClient(_http), _time, () => null);

        var result = await refresher.RefreshAsync("test-refresh-token", CancellationToken.None);

        Assert.Equal(GeminiRefreshStatus.ClientUnavailable, result.Status);
        Assert.Empty(_http.Requests);
    }

    private static readonly GeminiOAuthClient TestClient =
        new("test-client-id.apps.googleusercontent.com", "test-client-secret");

    private GeminiTokenRefresher CreateRefresher() => new(new HttpClient(_http), _time, () => TestClient);

    private void GivenTokenEndpoint(HttpStatusCode status, string body)
    {
        _http.When(
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri == GeminiTokenRefresher.TokenUrl,
            r =>
            {
                _body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                _contentType = r.Content.Headers.ContentType?.MediaType;
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
    }
}
```

`tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiOAuthClientLocatorTests.cs`:

클라이언트 값은 저장소에 넣지 않는다. 테스트는 가짜 값을 쓰고, 설치된 gemini-cli 번들 파일을 `FakeFileSystem`으로 흉내 낸다.

```csharp
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiOAuthClientLocatorTests
{
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();

    private string NpmPackageRoot => Path.Combine(_env.RoamingAppData, "npm", "node_modules", "@google", "gemini-cli");

    [Fact]
    public void Environment_variables_win()
    {
        _env.Set(GeminiOAuthClientLocator.EnvClientId, "env-id.apps.googleusercontent.com");
        _env.Set(GeminiOAuthClientLocator.EnvClientSecret, "env-secret");

        var client = CreateLocator().Locate();

        Assert.Equal(new GeminiOAuthClient("env-id.apps.googleusercontent.com", "env-secret"), client);
    }

    [Fact]
    public void Reads_double_quoted_constants_from_the_npm_bundle()
    {
        _files.AddFile(Path.Combine(NpmPackageRoot, "bundle", "chunk-AAAA.js"), "export const unrelated = 1;");
        _files.AddFile(Path.Combine(NpmPackageRoot, "bundle", "chunk-BBBB.js"),
            "var OAUTH_CLIENT_ID = \"bundle-id.apps.googleusercontent.com\";\nvar OAUTH_CLIENT_SECRET = \"bundle-secret\";");

        var client = CreateLocator().Locate();

        Assert.Equal(new GeminiOAuthClient("bundle-id.apps.googleusercontent.com", "bundle-secret"), client);
    }

    [Fact]
    public void Finds_the_package_next_to_gemini_on_PATH()
    {
        var npmDir = @"D:\tools\npm";
        _processes.SetPath("gemini", Path.Combine(npmDir, "gemini.cmd"));
        _files.AddFile(
            Path.Combine(npmDir, "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"),
            "const OAUTH_CLIENT_ID = 'core-id.apps.googleusercontent.com';\nconst OAUTH_CLIENT_SECRET = 'core-secret';");

        var client = CreateLocator().Locate();

        Assert.Equal(new GeminiOAuthClient("core-id.apps.googleusercontent.com", "core-secret"), client);
    }

    [Fact]
    public void Returns_null_when_gemini_cli_is_not_installed()
    {
        Assert.Null(CreateLocator().Locate());
    }

    [Fact]
    public void ParseScript_needs_both_values()
    {
        Assert.Null(GeminiOAuthClientLocator.ParseScript("const OAUTH_CLIENT_ID = \"only-id.apps.googleusercontent.com\";"));
    }

    [Fact]
    public void ToString_hides_the_secret()
    {
        var client = new GeminiOAuthClient("id.apps.googleusercontent.com", "hidden-secret");

        Assert.DoesNotContain("hidden-secret", client.ToString());
    }

    private GeminiOAuthClientLocator CreateLocator() => new(_env, _files, _processes);
}
```

`tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiProjectResolverTests.cs`:

```csharp
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiProjectResolverTests
{
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();

    [Fact]
    public void DotEnv_parses_comments_export_quotes_and_crlf()
    {
        var content = "# comment\r\nexport GOOGLE_CLOUD_PROJECT=\"quoted-project\"\r\nGOOGLE_CLOUD_PROJECT_ID='single' \r\nOTHER=value # trailing comment\r\nNOEQUALS\r\n=novalue\r\n";

        var values = GeminiDotEnv.Parse(content);

        Assert.Equal("quoted-project", values["GOOGLE_CLOUD_PROJECT"]);
        Assert.Equal("single", values["GOOGLE_CLOUD_PROJECT_ID"]);
        Assert.Equal("value", values["OTHER"]);
        Assert.Equal(3, values.Count);
    }

    [Fact]
    public void Environment_variable_wins_over_files()
    {
        var paths = new GeminiPaths(_env);
        _env.Set("GOOGLE_CLOUD_PROJECT", "env-project");
        _files.AddFile(paths.GeminiDotEnvPath, "GOOGLE_CLOUD_PROJECT=file-project");

        Assert.Equal("env-project", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Project_id_variable_is_used_when_project_is_unset()
    {
        var paths = new GeminiPaths(_env);
        _env.Set("GOOGLE_CLOUD_PROJECT", "");
        _env.Set("GOOGLE_CLOUD_PROJECT_ID", "env-project-id");

        Assert.Equal("env-project-id", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Gemini_dotenv_is_read_before_home_dotenv()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.GeminiDotEnvPath, "GOOGLE_CLOUD_PROJECT=gemini-dir-project");
        _files.AddFile(paths.HomeDotEnvPath, "GOOGLE_CLOUD_PROJECT=home-project");

        Assert.Equal("gemini-dir-project", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Home_dotenv_is_the_last_fallback()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.GeminiDotEnvPath, "UNRELATED=1");
        _files.AddFile(paths.HomeDotEnvPath, "GOOGLE_CLOUD_PROJECT_ID=home-project-id");

        Assert.Equal("home-project-id", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Dotenv_files_follow_GEMINI_CLI_HOME()
    {
        _env.Set("GEMINI_CLI_HOME", @"D:\alt-home");
        var paths = new GeminiPaths(_env);
        _files.AddFile(@"D:\alt-home\.gemini\.env", "GOOGLE_CLOUD_PROJECT=alt-project");

        Assert.Equal("alt-project", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Returns_null_when_nothing_is_configured()
    {
        Assert.Null(CreateResolver(new GeminiPaths(_env)).Resolve());
    }

    private GeminiProjectResolver CreateResolver(GeminiPaths paths) => new(_env, _files, paths);
}
```

- [ ] **Step 9: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GeminiTokenRefresherTests|FullyQualifiedName~GeminiOAuthClientLocatorTests|FullyQualifiedName~GeminiProjectResolverTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'GeminiTokenRefresher' could not be found`, `'GeminiOAuthClientLocator' could not be found`, `'GeminiOAuthClient' could not be found`, `'GeminiProjectResolver' could not be found`, 그리고 `GeminiRefreshStatus`·`GeminiDotEnv`에 대한 `error CS0103: The name '…' does not exist in the current context`.

- [ ] **Step 10: 구현 — 메모리 내 토큰 갱신, .env 파서, 프로젝트 해석**

공개 설치형 클라이언트 id/secret은 gemini-cli 소스(`packages/core/src/code_assist/oauth2.ts`)의 `OAUTH_CLIENT_ID`/`OAUTH_CLIENT_SECRET` 값이다. 구글 문서상 설치형 앱의 secret은 비밀로 취급되지 않지만, **GitHub Push Protection이 이 값을 비밀로 차단하므로 저장소·소스에 넣지 않는다.** 실행할 때 `GeminiOAuthClientLocator`가 다음 순서로 찾는다: 환경 변수 `GEMINI_OAUTH_CLIENT_ID`+`GEMINI_OAUTH_CLIENT_SECRET` → PATH의 `gemini` 옆 `node_modules\@google\gemini-cli`(및 `gemini-cli-core`) → `%APPDATA%\npm\node_modules\@google\gemini-cli`(및 `gemini-cli-core`). 각 패키지에서 `bundle\*.js`와 `dist\src\code_assist\oauth2.js`를 정규식으로 읽는다(작은따옴표·큰따옴표 모두). 갱신이 필요할 때만 찾고, 찾은 값은 캐시한다. **이 클라이언트로 자체 OAuth 로그인을 하지 않는다**(계정당 refresh token 100개 제한으로 CLI 토큰이 무효화될 수 있다). 갱신 결과는 파일에 쓰지 않는다.

`src/AITokenUsage.Core/Providers/Gemini/GeminiOAuthClientLocator.cs`:

```csharp
using System.Text.RegularExpressions;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Gemini;

public sealed record GeminiOAuthClient(string ClientId, string ClientSecret)
{
    public override string ToString() => $"GeminiOAuthClient {{ ClientId = {ClientId} }}";
}

/// <summary>
/// Finds gemini-cli's public installed-app OAuth client at runtime so the values never live in this repository.
/// </summary>
public sealed partial class GeminiOAuthClientLocator(IEnvironment env, IFileSystem files, IProcessRunner processes)
{
    public const string EnvClientId = "GEMINI_OAUTH_CLIENT_ID";
    public const string EnvClientSecret = "GEMINI_OAUTH_CLIENT_SECRET";

    private static readonly string[] PackageNames = ["gemini-cli", "gemini-cli-core"];

    private readonly object _gate = new();
    private GeminiOAuthClient? _cached;

    public GeminiOAuthClient? Locate()
    {
        lock (_gate)
        {
            return _cached ??= LocateCore();
        }
    }

    public static GeminiOAuthClient? ParseScript(string js)
    {
        var id = ClientIdPattern().Match(js);
        var secret = ClientSecretPattern().Match(js);
        return id.Success && secret.Success ? new GeminiOAuthClient(id.Groups[1].Value, secret.Groups[1].Value) : null;
    }

    private GeminiOAuthClient? LocateCore()
    {
        var envId = env.GetVariable(EnvClientId);
        var envSecret = env.GetVariable(EnvClientSecret);
        if (!string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret))
        {
            return new GeminiOAuthClient(envId.Trim(), envSecret.Trim());
        }

        foreach (var script in CandidateScripts())
        {
            try
            {
                if (files.ReadAllTextShared(script) is { } js && ParseScript(js) is { } client)
                {
                    return client;
                }
            }
            catch (IOException)
            {
                // Unreadable bundle chunk: try the next candidate.
            }
        }

        return null;
    }

    private IEnumerable<string> CandidateScripts()
    {
        foreach (var nodeModules in NodeModulesRoots())
        {
            foreach (var package in PackageNames)
            {
                var root = Path.Combine(nodeModules, "@google", package);
                foreach (var chunk in files.EnumerateFiles(Path.Combine(root, "bundle"), "*.js", recursive: false))
                {
                    yield return chunk;
                }

                var oauth2 = Path.Combine(root, "dist", "src", "code_assist", "oauth2.js");
                if (files.FileExists(oauth2))
                {
                    yield return oauth2;
                }
            }
        }
    }

    private IEnumerable<string> NodeModulesRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (processes.FindOnPath("gemini") is { } shim && Path.GetDirectoryName(shim) is { } shimDir)
        {
            var fromPath = Path.Combine(shimDir, "node_modules");
            if (seen.Add(fromPath))
            {
                yield return fromPath;
            }
        }

        var npmGlobal = Path.Combine(env.RoamingAppData, "npm", "node_modules");
        if (seen.Add(npmGlobal))
        {
            yield return npmGlobal;
        }
    }

    [GeneratedRegex("""OAUTH_CLIENT_ID\s*=\s*['"]([\w\-\.]+)['"]""")]
    private static partial Regex ClientIdPattern();

    [GeneratedRegex("""OAUTH_CLIENT_SECRET\s*=\s*['"]([\w\-]+)['"]""")]
    private static partial Regex ClientSecretPattern();
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiTokenRefresher.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

public enum GeminiRefreshStatus { Ok, InvalidGrant, Failed, ClientUnavailable }

public sealed record GeminiRefreshResult(
    GeminiRefreshStatus Status,
    string? AccessToken,
    DateTimeOffset? Expiry,
    string? IdToken,
    int HttpStatus)
{
    public override string ToString() => $"GeminiRefreshResult {{ Status = {Status}, HttpStatus = {HttpStatus} }}";
}

/// <summary>
/// Refreshes the Gemini CLI access token in memory with the CLI's public installed-app client,
/// located at runtime by <see cref="GeminiOAuthClientLocator"/>. The result is never written back to oauth_creds.json.
/// </summary>
public sealed class GeminiTokenRefresher(HttpClient http, TimeProvider time, Func<GeminiOAuthClient?> client)
{
    public const string TokenUrl = "https://oauth2.googleapis.com/token";

    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    public async Task<GeminiRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (client() is not { } oauthClient)
        {
            return new GeminiRefreshResult(GeminiRefreshStatus.ClientUnavailable, null, null, null, 0);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = oauthClient.ClientId,
                ["client_secret"] = oauthClient.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var root = TryParseObject(body);

        if (response.IsSuccessStatusCode && root is { } ok && Json.Str(ok, "access_token") is { Length: > 0 } accessToken)
        {
            var lifetime = Json.Num(ok, "expires_in") is double seconds && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultLifetime;
            return new GeminiRefreshResult(GeminiRefreshStatus.Ok, accessToken, time.GetUtcNow() + lifetime,
                Json.Str(ok, "id_token"), status);
        }

        if (root is { } error && Json.Str(error, "error") == "invalid_grant")
        {
            return new GeminiRefreshResult(GeminiRefreshStatus.InvalidGrant, null, null, null, status);
        }

        return new GeminiRefreshResult(GeminiRefreshStatus.Failed, null, null, null, status);
    }

    private static JsonElement? TryParseObject(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiDotEnv.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>Minimal KEY=VALUE parser for the .env files gemini-cli loads.</summary>
public static class GeminiDotEnv
{
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length > 0 && (value[0] == '"' || value[0] == '\''))
            {
                var close = value.IndexOf(value[0], 1);
                value = close > 0 ? value[1..close] : value[1..];
            }
            else
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    value = value[..comment].TrimEnd();
                }
            }

            result[key] = value;
        }

        return result;
    }
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiProjectResolver.cs`:

```csharp
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// Project fallback when loadCodeAssist returns none:
/// process env → &lt;home&gt;\.gemini\.env → &lt;home&gt;\.env, each checking GOOGLE_CLOUD_PROJECT then GOOGLE_CLOUD_PROJECT_ID.
/// </summary>
public sealed class GeminiProjectResolver(IEnvironment env, IFileSystem files, GeminiPaths paths)
{
    private static readonly string[] Keys = ["GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_PROJECT_ID"];

    public string? Resolve()
    {
        foreach (var key in Keys)
        {
            var value = env.GetVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        foreach (var path in new[] { paths.GeminiDotEnvPath, paths.HomeDotEnvPath })
        {
            string? content;
            try
            {
                content = files.ReadAllTextShared(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (content is null)
            {
                continue;
            }

            var values = GeminiDotEnv.Parse(content);
            foreach (var key in Keys)
            {
                if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }
}
```

- [ ] **Step 11: 테스트 실행 → 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GeminiTokenRefresherTests|FullyQualifiedName~GeminiOAuthClientLocatorTests|FullyQualifiedName~GeminiProjectResolverTests"`

Expected: `Passed!  - Failed:     0, Passed:    18`

- [ ] **Step 12: loadCodeAssist·retrieveUserQuota 픽스처 작성**

형태는 연구 보고서 §2.2–§2.5(gemini-cli 소스 타입과 CodexBar 픽스처)를 따랐고 값은 가공했다.

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/load-standard.json` (Standard, 프로젝트 문자열):

```json
{
  "currentTier": {
    "id": "standard-tier",
    "name": "Gemini Code Assist",
    "hasOnboardedPreviously": true
  },
  "allowedTiers": [
    { "id": "standard-tier", "name": "Gemini Code Assist", "isDefault": true }
  ],
  "cloudaicompanionProject": "fake-project-123"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/load-paid-object-project.json` (`paidTier.name`, 프로젝트가 `{id}` 객체):

```json
{
  "currentTier": { "id": "standard-tier", "name": "Gemini Code Assist", "hasOnboardedPreviously": true },
  "cloudaicompanionProject": { "id": "fake-project-object", "name": "projects/fake-project-object" },
  "paidTier": {
    "id": "g1-pro-tier",
    "name": "Gemini Code Assist in Google One AI Pro",
    "availableCredits": [ { "creditType": "GOOGLE_ONE_AI", "creditAmount": "1000" } ]
  }
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/load-shutdown.json` (개인 계정 종료 응답: `currentTier` 없음 + `UNSUPPORTED_CLIENT`):

```json
{
  "allowedTiers": [
    { "id": "standard-tier", "name": "Gemini Code Assist", "userDefinedCloudaicompanionProject": true, "isDefault": true }
  ],
  "ineligibleTiers": [
    {
      "reasonCode": "UNSUPPORTED_CLIENT",
      "reasonMessage": "This client is no longer supported for Gemini Code Assist for individuals. To continue using Gemini, please migrate to the Antigravity suite of products: https://antigravity.google",
      "tierId": "free-tier",
      "tierName": "Gemini Code Assist for individuals"
    }
  ]
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/load-no-project.json` (`currentTier`는 있고 프로젝트 없음 → ProjectIdRequired 경우):

```json
{
  "currentTier": {
    "id": "standard-tier",
    "name": "Gemini Code Assist",
    "userDefinedCloudaicompanionProject": true,
    "hasOnboardedPreviously": true
  }
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/load-free-unsupported-client.json` (free-tier + `UNSUPPORTED_CLIENT`, 프로젝트 있음 → quota에서 `SUBSCRIPTION_REQUIRED`를 받는 경우):

```json
{
  "currentTier": { "id": "free-tier", "name": "Gemini Code Assist for individuals" },
  "cloudaicompanionProject": "fake-project-free",
  "ineligibleTiers": [
    {
      "reasonCode": "UNSUPPORTED_CLIENT",
      "reasonMessage": "This client is no longer supported for Gemini Code Assist for individuals.",
      "tierId": "free-tier",
      "tierName": "Gemini Code Assist for individuals"
    }
  ]
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/quota-buckets.json` (같은 묶음의 여러 버킷, 알 수 없는 모델, `remainingFraction` 없는 버킷, `modelId` 없는 버킷, 객체가 아닌 항목 포함):

```json
{
  "buckets": [
    { "modelId": "gemini-2.5-pro", "tokenType": "REQUESTS", "remainingFraction": 0.75, "resetTime": "2026-10-29T00:00:00Z" },
    { "modelId": "gemini-3.1-pro-preview", "tokenType": "REQUESTS", "remainingFraction": 0.6, "resetTime": "2026-10-28T20:00:00Z" },
    { "modelId": "gemini-2.5-flash", "tokenType": "REQUESTS", "remainingFraction": 0.98, "resetTime": "2026-10-29T00:00:00Z" },
    { "modelId": "gemini-3-flash-preview", "tokenType": "REQUESTS", "remainingFraction": 0.9, "resetTime": "2026-10-28T22:30:00Z" },
    { "modelId": "gemini-2.5-flash-lite", "tokenType": "REQUESTS", "remainingFraction": 1, "resetTime": "2026-10-29T00:00:00Z" },
    { "modelId": "gemma-3-27b-it", "tokenType": "REQUESTS", "remainingFraction": 0.1, "resetTime": "2026-10-29T00:00:00Z" },
    { "modelId": "gemini-2.5-pro", "tokenType": "INPUT_TOKENS", "resetTime": "2026-10-29T00:00:00Z" },
    { "tokenType": "REQUESTS", "remainingFraction": 0.01 },
    "not-an-object"
  ]
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/gemini/quota-subscription-required.json` (HTTP 403 본문):

```json
{
  "error": {
    "code": 403,
    "message": "You do not have a valid license of this product. (#3501)",
    "status": "PERMISSION_DENIED",
    "details": [
      {
        "@type": "type.googleapis.com/google.rpc.ErrorInfo",
        "reason": "SUBSCRIPTION_REQUIRED",
        "domain": "cloudaicompanion.googleapis.com"
      }
    ]
  }
}
```

- [ ] **Step 13: 실패하는 테스트 작성 — loadCodeAssist 파서와 버킷 묶기**

`tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiCodeAssistParserTests.cs`:

```csharp
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiCodeAssistParserTests
{
    private const string ConsumerIdToken =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJpc3MiOiJodHRwczovL2FjY291bnRzLmdvb2dsZS5jb20iLCJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20ifQ.ZmFrZS1zaWduYXR1cmU";
    private const string WorkspaceIdToken =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJpc3MiOiJodHRwczovL2FjY291bnRzLmdvb2dsZS5jb20iLCJlbWFpbCI6ImRldkBleGFtcGxlLmNvbSIsImhkIjoiZXhhbXBsZS5jb20ifQ.ZmFrZS1zaWduYXR1cmU";

    [Fact]
    public void Standard_tier_with_string_project()
    {
        var info = GeminiCodeAssistParser.Parse(Fixture.Read("gemini/load-standard.json"));

        Assert.True(info.HasCurrentTier);
        Assert.Equal("standard-tier", info.TierId);
        Assert.Equal("fake-project-123", info.Project);
        Assert.False(info.HasUnsupportedClient);
        Assert.False(info.IsConsumerShutdown(hasHostedDomain: false));
        Assert.Equal("Standard", GeminiCodeAssistParser.PlanLabel(info, hasHostedDomain: false));
    }

    [Fact]
    public void Paid_tier_name_and_object_project_id()
    {
        var info = GeminiCodeAssistParser.Parse(Fixture.Read("gemini/load-paid-object-project.json"));

        Assert.Equal("fake-project-object", info.Project);
        Assert.Equal("g1-pro-tier", info.TierId);
        Assert.Equal("Gemini Code Assist in Google One AI Pro", GeminiCodeAssistParser.PlanLabel(info, hasHostedDomain: false));
    }

    [Fact]
    public void Object_project_with_projectId_field()
    {
        var info = GeminiCodeAssistParser.Parse(
            """{"currentTier":{"id":"standard-tier"},"cloudaicompanionProject":{"projectId":"fake-project-from-projectId"}}""");

        Assert.Equal("fake-project-from-projectId", info.Project);
    }

    [Fact]
    public void Consumer_shutdown_requires_no_tier_no_paid_name_and_no_hd()
    {
        var info = GeminiCodeAssistParser.Parse(Fixture.Read("gemini/load-shutdown.json"));

        Assert.False(info.HasCurrentTier);
        Assert.True(info.HasUnsupportedClient);
        Assert.Null(info.Project);
        Assert.True(info.IsConsumerShutdown(hasHostedDomain: false));
        Assert.False(info.IsConsumerShutdown(hasHostedDomain: true));
    }

    [Fact]
    public void Paid_tier_name_prevents_shutdown()
    {
        var info = GeminiCodeAssistParser.Parse(
            """{"paidTier":{"name":"Gemini Code Assist in Google One AI Ultra"},"ineligibleTiers":[{"reasonCode":"UNSUPPORTED_CLIENT"}]}""");

        Assert.False(info.IsConsumerShutdown(hasHostedDomain: false));
    }

    [Fact]
    public void Unsupported_client_in_reason_message_is_detected()
    {
        var info = GeminiCodeAssistParser.Parse(
            """{"ineligibleTiers":[{"reasonCode":"UNKNOWN","reasonMessage":"server said unsupported_client"}]}""");

        Assert.True(info.HasUnsupportedClient);
    }

    [Theory]
    [InlineData("free-tier", false, "Free")]
    [InlineData("free-tier", true, "Workspace")]
    [InlineData("standard-tier", false, "Standard")]
    [InlineData("legacy-tier", false, "Legacy")]
    [InlineData("mystery-tier", false, null)]
    public void Plan_label_from_tier_id(string tierId, bool hasHostedDomain, string? expected)
    {
        var info = new GeminiCodeAssistInfo(true, tierId, null, "p", false);

        Assert.Equal(expected, GeminiCodeAssistParser.PlanLabel(info, hasHostedDomain));
    }

    [Theory]
    [InlineData(ConsumerIdToken, false)]
    [InlineData(WorkspaceIdToken, true)]
    [InlineData(null, false)]
    [InlineData("not-a-jwt", false)]
    public void Hosted_domain_claim_comes_from_id_token(string? idToken, bool expected)
    {
        Assert.Equal(expected, GeminiCodeAssistParser.HasHostedDomain(idToken));
    }

    [Fact]
    public void Subscription_required_reason_is_found_in_error_details()
    {
        Assert.True(GeminiCodeAssistParser.HasErrorReason(Fixture.Read("gemini/quota-subscription-required.json"), "SUBSCRIPTION_REQUIRED"));
        Assert.False(GeminiCodeAssistParser.HasErrorReason("""{"error":{"code":403,"details":[]}}""", "SUBSCRIPTION_REQUIRED"));
        Assert.False(GeminiCodeAssistParser.HasErrorReason("<html>forbidden</html>", "SUBSCRIPTION_REQUIRED"));
    }

    [Theory]
    [InlineData("""{"error":{"message":"UNSUPPORTED_CLIENT"}}""", true)]
    [InlineData("Please migrate to Antigravity to keep using Gemini", true)]
    [InlineData("This client is no longer supported for Gemini Code Assist", true)]
    [InlineData("""{"error":{"code":500,"message":"backend error"}}""", false)]
    public void Shutdown_text_markers(string body, bool expected)
    {
        Assert.Equal(expected, GeminiCodeAssistParser.LooksLikeShutdown(body));
    }
}
```

`tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiQuotaParserTests.cs`:

```csharp
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiQuotaParserTests
{
    [Fact]
    public void Groups_buckets_and_keeps_minimum_fraction_per_group()
    {
        var meters = GeminiQuotaParser.Parse(Fixture.Read("gemini/quota-buckets.json"));

        Assert.Equal(["pro", "flash", "flash-lite"], meters.Select(m => m.Key).ToArray());

        var pro = meters[0];
        Assert.Equal("Pro", pro.ShortLabel);
        Assert.Equal("Pro 일간", pro.LongLabel);
        Assert.Equal(40, pro.UsedPercent!.Value, 6);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero), pro.ResetsAt);

        var flash = meters[1];
        Assert.Equal("Fl", flash.ShortLabel);
        Assert.Equal(10, flash.UsedPercent!.Value, 6);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 22, 30, 0, TimeSpan.Zero), flash.ResetsAt);

        var lite = meters[2];
        Assert.Equal("Lt", lite.ShortLabel);
        Assert.Equal(0, lite.UsedPercent!.Value, 6);
    }

    [Theory]
    [InlineData("gemini-2.5-flash-lite", "flash-lite")]
    [InlineData("gemini-3.1-flash-lite-preview", "flash-lite")]
    [InlineData("gemini-3-flash-preview", "flash")]
    [InlineData("GEMINI-3.8-FLASH", "flash")]
    [InlineData("gemini-3.1-pro-preview-customtools", "pro")]
    [InlineData("gemma-3-27b-it", null)]
    public void Classifies_by_substring_flash_lite_then_flash_then_pro(string modelId, string? expected)
    {
        Assert.Equal(expected, GeminiQuotaParser.Classify(modelId));
    }

    [Fact]
    public void Fraction_outside_zero_to_one_is_clamped()
    {
        var meters = GeminiQuotaParser.Parse(
            """{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":-0.2},{"modelId":"gemini-2.5-flash","remainingFraction":1.4}]}""");

        Assert.Equal(100, meters.Single(m => m.Key == "pro").UsedPercent!.Value, 6);
        Assert.Equal(0, meters.Single(m => m.Key == "flash").UsedPercent!.Value, 6);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"buckets":[]}""")]
    [InlineData("""{"buckets":null}""")]
    [InlineData("[]")]
    public void Missing_buckets_give_no_meters(string json)
    {
        Assert.Empty(GeminiQuotaParser.Parse(json));
    }

    [Fact]
    public void Static_meter_options_match_meter_keys()
    {
        Assert.Equal(["pro", "flash", "flash-lite"], GeminiQuotaParser.MeterOptions.Select(o => o.Key).ToArray());
    }
}
```

- [ ] **Step 14: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GeminiCodeAssistParserTests|FullyQualifiedName~GeminiQuotaParserTests"`

Expected: 빌드 실패. `GeminiCodeAssistParser`, `GeminiCodeAssistInfo`, `GeminiQuotaParser`에 대한 `error CS0103`/`error CS0246`.

- [ ] **Step 15: 구현 — loadCodeAssist 해석, 종료 판정, 버킷 묶기**

`reasonCode`·`tierId`는 자유 문자열로 다룬다(`UNSUPPORTED_CLIENT`는 gemini-cli의 enum에 없다). 사용 %는 소수 둘째 자리에서 반올림해 `40.00000000000001` 같은 값을 없앤다.

`src/AITokenUsage.Core/Providers/Gemini/GeminiCodeAssistInfo.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>The parts of a loadCodeAssist response the widget needs.</summary>
/// <param name="HasCurrentTier">currentTier object present.</param>
/// <param name="TierId">paidTier.id ?? currentTier.id.</param>
/// <param name="PaidTierName">paidTier.name.</param>
/// <param name="Project">cloudaicompanionProject (string, or object id/projectId).</param>
/// <param name="HasUnsupportedClient">Any ineligibleTiers[].reasonCode/reasonMessage contains UNSUPPORTED_CLIENT.</param>
public sealed record GeminiCodeAssistInfo(
    bool HasCurrentTier,
    string? TierId,
    string? PaidTierName,
    string? Project,
    bool HasUnsupportedClient)
{
    /// <summary>Consumer "Login with Google" shutdown (2026-06-18).</summary>
    public bool IsConsumerShutdown(bool hasHostedDomain) =>
        !HasCurrentTier && HasUnsupportedClient && string.IsNullOrEmpty(PaidTierName) && !hasHostedDomain;

    /// <summary>retrieveUserQuota 403 SUBSCRIPTION_REQUIRED counts as the shutdown only in this case.</summary>
    public bool IsShutdownLicenseDenial(bool hasHostedDomain) =>
        HasUnsupportedClient
        && !string.Equals(TierId, "standard-tier", StringComparison.Ordinal)
        && string.IsNullOrEmpty(PaidTierName)
        && !hasHostedDomain;
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiCodeAssistParser.cs`:

```csharp
using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

public static class GeminiCodeAssistParser
{
    /// <summary>Parses a loadCodeAssist body. Throws JsonException when it is not a JSON object.</summary>
    public static GeminiCodeAssistInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("loadCodeAssist response is not a JSON object.");
        }

        var current = Json.Obj(root, "currentTier");
        var paid = Json.Obj(root, "paidTier");
        var tierId = Blank(paid is { } p ? Json.Str(p, "id") : null)
                     ?? Blank(current is { } c ? Json.Str(c, "id") : null);
        var paidName = Blank(paid is { } pn ? Json.Str(pn, "name") : null);

        return new GeminiCodeAssistInfo(current is not null, tierId, paidName, ReadProject(root), HasUnsupportedClient(root));
    }

    /// <summary>paidTier.name, else standard-tier → Standard, free-tier + hd → Workspace, free-tier → Free, legacy-tier → Legacy.</summary>
    public static string? PlanLabel(GeminiCodeAssistInfo info, bool hasHostedDomain)
    {
        if (!string.IsNullOrEmpty(info.PaidTierName))
        {
            return info.PaidTierName;
        }

        return info.TierId switch
        {
            "standard-tier" => "Standard",
            "free-tier" => hasHostedDomain ? "Workspace" : "Free",
            "legacy-tier" => "Legacy",
            _ => null,
        };
    }

    /// <summary>True when the id_token carries an "hd" (Workspace hosted domain) claim.</summary>
    public static bool HasHostedDomain(string? idToken) =>
        Jwt.TryReadPayload(idToken, out var payload)
        && payload.ValueKind == JsonValueKind.Object
        && !string.IsNullOrEmpty(Json.Str(payload, "hd"));

    /// <summary>Google error body: error.details[].reason == <paramref name="reason"/>.</summary>
    public static bool HasErrorReason(string body, string reason)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || Json.Obj(doc.RootElement, "error") is not { } error
                || Json.Arr(error, "details") is not { } details)
            {
                return false;
            }

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object
                    && string.Equals(Json.Str(detail, "reason"), reason, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Text markers CodexBar uses to recognise the consumer shutdown in non-200 bodies.</summary>
    public static bool LooksLikeShutdown(string body)
    {
        var text = body.ToLowerInvariant();
        return text.Contains("unsupported_client")
               || text.Contains("ineligibletiererror")
               || (text.Contains("no longer supported") && text.Contains("gemini code assist"))
               || (text.Contains("migrate") && text.Contains("antigravity") && text.Contains("gemini"));
    }

    private static string? ReadProject(JsonElement root)
    {
        if (!root.TryGetProperty("cloudaicompanionProject", out var project))
        {
            return null;
        }

        return project.ValueKind switch
        {
            JsonValueKind.String => Blank(project.GetString()),
            JsonValueKind.Object => Blank(Json.Str(project, "id")) ?? Blank(Json.Str(project, "projectId")),
            _ => null,
        };
    }

    private static bool HasUnsupportedClient(JsonElement root)
    {
        if (Json.Arr(root, "ineligibleTiers") is not { } tiers)
        {
            return false;
        }

        foreach (var tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (MentionsUnsupportedClient(Json.Str(tier, "reasonCode"))
                || MentionsUnsupportedClient(Json.Str(tier, "reasonMessage")))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MentionsUnsupportedClient(string? text) =>
        text?.Contains("UNSUPPORTED_CLIENT", StringComparison.OrdinalIgnoreCase) == true;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiQuotaParser.cs`:

```csharp
using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// retrieveUserQuota → meters. Buckets are grouped by modelId substring (flash-lite, then flash, then pro);
/// each group keeps its minimum remainingFraction; used % = (1 − f) × 100.
/// </summary>
public static class GeminiQuotaParser
{
    public static readonly IReadOnlyList<MeterOption> MeterOptions =
    [
        new("pro", "Pro 일간"),
        new("flash", "Flash 일간"),
        new("flash-lite", "Flash-Lite 일간"),
    ];

    private static readonly (string Key, string ShortLabel, string LongLabel)[] Groups =
    [
        ("pro", "Pro", "Pro 일간"),
        ("flash", "Fl", "Flash 일간"),
        ("flash-lite", "Lt", "Flash-Lite 일간"),
    ];

    public static string? Classify(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.Contains("flash-lite"))
        {
            return "flash-lite";
        }

        if (id.Contains("flash"))
        {
            return "flash";
        }

        return id.Contains("pro") ? "pro" : null;
    }

    /// <summary>Throws JsonException when the body is not JSON. Malformed buckets are skipped one by one.</summary>
    public static IReadOnlyList<UsageMeter> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Json.Arr(root, "buckets") is not { } buckets)
        {
            return [];
        }

        var lowest = new Dictionary<string, (double Fraction, DateTimeOffset? ResetsAt)>(StringComparer.Ordinal);
        foreach (var bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var modelId = Json.Str(bucket, "modelId");
            var fraction = Json.Num(bucket, "remainingFraction");
            if (string.IsNullOrWhiteSpace(modelId) || fraction is null || double.IsNaN(fraction.Value))
            {
                continue;
            }

            var group = Classify(modelId);
            if (group is null)
            {
                continue;
            }

            var clamped = Math.Clamp(fraction.Value, 0, 1);
            if (!lowest.TryGetValue(group, out var current) || clamped < current.Fraction)
            {
                lowest[group] = (clamped, Json.Iso(bucket, "resetTime"));
            }
        }

        var meters = new List<UsageMeter>();
        foreach (var (key, shortLabel, longLabel) in Groups)
        {
            if (lowest.TryGetValue(key, out var value))
            {
                var used = Math.Round((1 - value.Fraction) * 100, 2);
                meters.Add(new UsageMeter(key, shortLabel, longLabel, used, value.ResetsAt));
            }
        }

        return meters;
    }
}
```

- [ ] **Step 16: 테스트 실행 → 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GeminiCodeAssistParserTests|FullyQualifiedName~GeminiQuotaParserTests"`

Expected: `Passed!  - Failed:     0, Passed:    33`

- [ ] **Step 17: 실패하는 테스트 작성 — 공급자 전체 흐름**

호출 기록은 `StubHttpHandler`의 `Requests` 형식에 기대지 않도록 각 처리기 안에서 직접 남긴다. 처리기가 없는 URL(예: `onboardUser`)로 요청하면 `StubHttpHandler`가 예외를 던져 테스트가 실패하므로, "onboardUser를 부르지 않는다"는 등록하지 않는 것만으로 검증된다.

`tests/AITokenUsage.Core.Tests/Providers/Gemini/GeminiUsageProviderTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiUsageProviderTests
{
    // oauth_creds.json fixtures expire at 2026-10-28T04:00:00Z.
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _http = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly GeminiPaths _paths;

    private readonly List<string?> _loadTokens = [];
    private readonly List<string?> _quotaTokens = [];
    private readonly List<string> _loadBodies = [];
    private readonly List<string> _quotaBodies = [];
    private readonly List<string> _refreshBodies = [];

    public GeminiUsageProviderTests()
    {
        _paths = new GeminiPaths(_env);
        // The real OAuth client is located from the installed gemini-cli at runtime; tests use fake values.
        _env.Set(GeminiOAuthClientLocator.EnvClientId, "test-client-id.apps.googleusercontent.com");
        _env.Set(GeminiOAuthClientLocator.EnvClientSecret, "test-client-secret");
    }

    [Fact]
    public void Metadata_matches_spec()
    {
        var provider = CreateProvider();

        Assert.Equal("gemini", provider.Id);
        Assert.Equal("Gemini", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(2), provider.MinInterval);
        Assert.Equal([_paths.OAuthCredsPath, _paths.SettingsPath], provider.WatchPaths.ToArray());
        Assert.Equal(["pro", "flash", "flash-lite"], provider.StaticMeterOptions.Select(o => o.Key).ToArray());
    }

    [Fact]
    public async Task Missing_credentials_report_NotInstalled()
    {
        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Equal(GeminiMessages.NotInstalled, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Api_key_settings_report_Unsupported_without_network()
    {
        GivenCredsFile();
        _files.AddFile(_paths.SettingsPath, Fixture.Read("gemini/settings-api-key.json"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.ApiKeyMode, snapshot.StatusMessage);
        Assert.Empty(_loadTokens);
    }

    [Fact]
    public async Task Fresh_file_token_fetches_quota()
    {
        GivenCredsFile();
        _files.AddFile(_paths.SettingsPath, Fixture.Read("gemini/settings-oauth-jsonc.json"));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Standard", snapshot.PlanLabel);
        Assert.Equal(["pro", "flash", "flash-lite"], snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(40, snapshot.Meters[0].UsedPercent!.Value, 6);
        Assert.Equal(["test-access-token"], _loadTokens);
        Assert.Equal(["test-access-token"], _quotaTokens);
        Assert.Equal("""{"project":"fake-project-123"}""", Assert.Single(_quotaBodies));
        var loadBody = Assert.Single(_loadBodies);
        Assert.Contains("\"ideType\":\"IDE_UNSPECIFIED\"", loadBody);
        Assert.Contains("\"pluginType\":\"GEMINI\"", loadBody);
        Assert.Empty(_refreshBodies);
    }

    [Fact]
    public async Task Credential_manager_token_is_used_when_file_is_missing()
    {
        _credentials.Add(GeminiCredentialReader.CredentialTarget, "main-account", Fixture.Read("gemini/credman-main-account.json"));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["test-credman-access-token"], _quotaTokens);
    }

    [Fact]
    public async Task Expiring_token_is_refreshed_in_memory_and_reused()
    {
        GivenCredsFile();
        var originalFile = _files.ReadAllTextShared(_paths.OAuthCredsPath);
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));
        var provider = CreateProvider();

        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 3, 56, 0, TimeSpan.Zero)); // expiry - 4 min
        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10)); // file token is now expired, memory token is not
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        var refreshBody = Assert.Single(_refreshBodies);
        Assert.Contains("grant_type=refresh_token", refreshBody);
        Assert.Contains("refresh_token=test-refresh-token", refreshBody);
        Assert.Equal(["test-refreshed-access-token", "test-refreshed-access-token"], _quotaTokens);
        Assert.Equal(originalFile, _files.ReadAllTextShared(_paths.OAuthCredsPath)); // never written back
    }

    [Fact]
    public async Task Invalid_grant_reports_SignedOut()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.BadRequest, Fixture.Read("gemini/refresh-invalid-grant.json")));
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(GeminiMessages.SignInAgain, snapshot.StatusMessage);
        Assert.Empty(_loadTokens);
    }

    [Fact]
    public async Task Unauthorized_quota_refreshes_once_and_retries()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(
            Reply(HttpStatusCode.Unauthorized, """{"error":{"code":401}}"""),
            Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(_refreshBodies);
        Assert.Equal(["test-access-token", "test-refreshed-access-token"], _quotaTokens);
    }

    [Fact]
    public async Task Second_unauthorized_reports_SignedOut()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.Unauthorized, """{"error":{"code":401}}"""));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(GeminiMessages.SignInAgain, snapshot.StatusMessage);
        Assert.Single(_refreshBodies);
        Assert.Equal(2, _quotaTokens.Count);
    }

    [Fact]
    public async Task Consumer_shutdown_is_reported_without_quota_or_onboard_calls()
    {
        // StubHttpHandler throws on unmatched requests, so an onboardUser call would fail this test.
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-shutdown.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.ConsumerShutdown, snapshot.StatusMessage);
        Assert.Empty(_quotaTokens);
    }

    [Fact]
    public async Task Workspace_account_is_not_treated_as_shutdown()
    {
        GivenCredsFile("gemini/oauth_creds-workspace.json");
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-shutdown.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(GeminiMessages.NotOnboarded, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Current_tier_without_project_reports_ProjectRequired()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-no-project.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(GeminiMessages.ProjectRequired, snapshot.StatusMessage);
        Assert.Empty(_quotaTokens);
    }

    [Fact]
    public async Task Environment_project_is_used_when_response_has_none()
    {
        GivenCredsFile();
        _env.Set("GOOGLE_CLOUD_PROJECT", "env-project");
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-no-project.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("""{"project":"env-project"}""", Assert.Single(_quotaBodies));
    }

    [Fact]
    public async Task Dotenv_project_is_used_when_env_is_unset()
    {
        GivenCredsFile();
        _files.AddFile(_paths.GeminiDotEnvPath, "GOOGLE_CLOUD_PROJECT=dotenv-project\n");
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-no-project.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("""{"project":"dotenv-project"}""", Assert.Single(_quotaBodies));
    }

    [Fact]
    public async Task Subscription_required_after_unsupported_client_is_the_shutdown()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-free-unsupported-client.json")));
        GivenQuota(Reply(HttpStatusCode.Forbidden, Fixture.Read("gemini/quota-subscription-required.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.ConsumerShutdown, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Subscription_required_on_standard_tier_is_no_license()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.Forbidden, Fixture.Read("gemini/quota-subscription-required.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(GeminiMessages.NoLicense, snapshot.StatusMessage);
    }

    [Fact]
    public async Task LoadCodeAssist_is_cached_for_one_hour()
    {
        GivenCredsFile();
        GivenRefresh(Reply(HttpStatusCode.OK, Fixture.Read("gemini/refresh-ok.json")));
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(Reply(HttpStatusCode.OK, Fixture.Read("gemini/quota-buckets.json")));
        var provider = CreateProvider();

        await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Single(_loadTokens);
        Assert.Equal(2, _quotaTokens.Count);

        _time.Advance(TimeSpan.FromMinutes(31));
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, _loadTokens.Count);
    }

    [Fact]
    public async Task Rate_limited_quota_reports_retry_after()
    {
        GivenCredsFile();
        GivenLoad(Reply(HttpStatusCode.OK, Fixture.Read("gemini/load-standard.json")));
        GivenQuota(() =>
        {
            var response = Reply(HttpStatusCode.TooManyRequests, """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""")();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Corrupt_credentials_report_Error_not_SignedOut()
    {
        _files.AddFile(_paths.OAuthCredsPath, "{ \"access_token\": ");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(GeminiMessages.CredentialsUnreadable, snapshot.StatusMessage);
    }

    [Fact]
    public async Task Network_failure_reports_Error()
    {
        GivenCredsFile();
        _http.When(r => r.RequestUri!.AbsoluteUri == GeminiUsageProvider.LoadCodeAssistUrl,
            _ => throw new HttpRequestException("connection refused"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(GeminiMessages.Network, snapshot.StatusMessage);
    }

    private GeminiUsageProvider CreateProvider() =>
        new(TestContexts.Create(_http, _time, _files, _env, creds: _credentials));

    private void GivenCredsFile(string fixture = "gemini/oauth_creds.json") =>
        _files.AddFile(_paths.OAuthCredsPath, Fixture.Read(fixture));

    private void GivenLoad(params Func<HttpResponseMessage>[] replies) =>
        Route(GeminiUsageProvider.LoadCodeAssistUrl, replies, r =>
        {
            _loadTokens.Add(r.Headers.Authorization?.Parameter);
            _loadBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        });

    private void GivenQuota(params Func<HttpResponseMessage>[] replies) =>
        Route(GeminiUsageProvider.RetrieveUserQuotaUrl, replies, r =>
        {
            _quotaTokens.Add(r.Headers.Authorization?.Parameter);
            _quotaBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        });

    private void GivenRefresh(params Func<HttpResponseMessage>[] replies) =>
        Route(GeminiTokenRefresher.TokenUrl, replies,
            r => _refreshBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));

    /// <summary>Each call takes the next reply; the last one repeats.</summary>
    private void Route(string url, Func<HttpResponseMessage>[] replies, Action<HttpRequestMessage> record)
    {
        var queue = new Queue<Func<HttpResponseMessage>>(replies);
        _http.When(r => r.RequestUri!.AbsoluteUri == url, r =>
        {
            record(r);
            return (queue.Count > 1 ? queue.Dequeue() : queue.Peek())();
        });
    }

    private static Func<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
```

- [ ] **Step 18: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GeminiUsageProviderTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'GeminiUsageProvider' could not be found`, `error CS0103: The name 'GeminiMessages' does not exist in the current context`.

- [ ] **Step 19: 구현 — 메시지와 공급자**

`src/AITokenUsage.Core/Providers/Gemini/GeminiMessages.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>User-facing (Korean) status messages for the Gemini provider.</summary>
public static class GeminiMessages
{
    public const string ApiKeyMode = "API 키/Vertex — 사용량 조회 불가";
    public const string NotInstalled = "Gemini CLI 로그인 정보 없음 — 터미널에서 gemini 실행 후 로그인";
    public const string SignInAgain = "터미널에서 gemini 실행해 다시 로그인";
    public const string CredentialsUnreadable = "자격 증명 파일을 읽지 못함 — 잠시 후 다시 시도";
    public const string RefreshFailed = "토큰 갱신 실패 — 잠시 후 다시 시도";
    public const string ClientUnavailable = "gemini-cli 설치를 찾지 못해 로그인을 갱신할 수 없음 — 터미널에서 gemini 실행";
    public const string ConsumerShutdown = "개인 계정 지원 종료 (2026-06-18) — Antigravity 사용";
    public const string NoLicense = "Code Assist 라이선스 없음 — 사용량 조회 불가";
    public const string ProjectRequired = "GOOGLE_CLOUD_PROJECT 설정 필요";
    public const string NotOnboarded = "터미널에서 gemini를 한 번 실행해 설정을 마치세요";
    public const string RateLimited = "요청 제한 — 잠시 후 다시 시도";
    public const string Network = "네트워크 오류 — 잠시 후 다시 시도";
    public const string BadResponse = "응답 형식을 해석할 수 없음";
    public const string NoQuotaData = "사용량 정보가 비어 있음";

    public static string ServerError(int httpStatus) => $"Gemini 서버 오류 (HTTP {httpStatus})";
}
```

`src/AITokenUsage.Core/Providers/Gemini/GeminiUsageProvider.cs`:

```csharp
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
```

- [ ] **Step 20: Gemini 테스트 전체와 솔루션 테스트 실행 → 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~Providers.Gemini"`

Expected: `Passed!  - Failed:     0, Passed:    91`

Run: `dotnet test AITokenUsage.sln`

Expected: `Failed:     0`. 경고 없이 빌드된다(`dotnet build AITokenUsage.sln` → `0 Warning(s)`, `0 Error(s)`).

- [ ] **Step 21: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Gemini tests/AITokenUsage.Core.Tests/Providers/Gemini tests/AITokenUsage.Core.Tests/Fixtures/gemini tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj
git commit -m "feat(core): Gemini CLI 사용량 공급자 추가" -m "oauth_creds.json과 자격 증명 관리자에서 토큰을 읽고 메모리에서만 갱신한다. loadCodeAssist 1시간 캐시, 개인 계정 종료 판정, Pro/Flash/Flash-Lite 일간 미터를 제공한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Copilot 공급자 (`CopilotTokenSources`, `CopilotUsageParser`, `CopilotUsageProvider` + 로그인 멤버)

`GET https://api.github.com/copilot_internal/user`로 Copilot 월간 사용량을 읽는다. 근거는 spec §5.4, `docs/research/copilot-usage-api.md` §1–§4다. spec 구조도의 `CopilotUsageClient`는 `CopilotUsageProvider`의 비공개 메서드와 공용 `GitHubApi`(헤더·레이트 리밋 판정)로 나눴다. `GitHubApi`는 Task 13의 기기 코드 흐름도 쓴다.

**토큰 후보 (`CopilotTokenSources.EnumerateAsync`, 지연 열거·중복 제거)**

| 순서 | 출처 | 규칙 |
|---|---|---|
| 1 | 앱 저장 토큰 `ISecretStore` 이름 `copilot-token` | JSON `{"token":"…","login":"…"}`. 복호화·읽기 실패는 "없음" |
| 2 | 환경 변수 `COPILOT_GITHUB_TOKEN` → `GH_TOKEN` → `GITHUB_TOKEN` | 앞뒤 공백 제거 후 접두사 `gho_`·`ghu_`·`github_pat_`만. `ghp_`(classic PAT)·기타는 버림 |
| 3 | `gh auth token --hostname github.com` | `FindOnPath("gh")`가 있을 때만. `EnvironmentOverrides`로 `GH_TOKEN`/`GITHUB_TOKEN` 제거(null), `Timeout` 5초. 종료 코드 0이고 시간 초과가 아니면 stdout 첫 줄 |
| 4 | Credential Manager `EnumerateGeneric("copilot-cli*")` | blob = UTF-8 토큰. `UserName`(`https://github.com:<login>`)에서 login |
| 5 | `%LOCALAPPDATA%\github-copilot\apps.json` → `hosts.json` | 키에 `github.com`이 들어 있는 항목의 `oauth_token`(없으면 `access_token`), login은 `user`(없으면 `login`) |

**검증과 상태 (`CopilotUsageProvider.FetchAsync`)**
- 지난번에 성공한 후보(메모리)를 먼저 호출한다. 401/403/404면 잊고 1번부터 다시 열거한다(이미 시도한 토큰은 건너뜀). 그래서 `gh`는 기억한 토큰이 유효한 동안 다시 실행되지 않는다.
- 각 후보: **레이트 리밋**(429, `x-ratelimit-remaining: 0`인 403, `Retry-After`가 붙은 모든 비정상 응답) → `RateLimited`, `RetryAfter` = `Retry-After` 또는 `x-ratelimit-reset − now`. 다른 후보는 시도하지 않는다. → **401/403/404** → 다음 후보. → 그 밖의 비정상 → `Error`. → 200 → 파싱.
- 후보가 없거나 모두 거부되면 `SignedOut`, "설정에서 GitHub 로그인".
- 플랜도 미터도 없으면(`can_signup_for_limited`만 있는 계정 등) `Unsupported`, "Copilot 구독 없음".
- 요청 헤더: `Authorization: Bearer <token>`, `Accept: application/json`, `User-Agent: AITokenUsage/1.0`(**없으면 GitHub가 거부**), `X-GitHub-Api-Version: 2025-04-01`. `GET /user`에는 버전 헤더를 보내지 않는다(REST는 모르는 버전을 400으로 거부).

**파싱 규칙 (`CopilotUsageParser`)**

| 항목 | 규칙 |
|---|---|
| 플랜 | `access_type_sku`: `free_limited_copilot` Free, `free_educational_quota` Student. 그 외 `copilot_plan`: `individual` Pro, `individual_pro` Pro+, `individual_max` Max, `individual_edu` Student, `business` Business, `enterprise` Enterprise, 나머지 null |
| 주 미터 `monthly` (짧은 라벨 `월`) | Free면 `chat ?? premium_interactions`, 그 외 `premium_interactions`. 긴 라벨: `token_based_billing`이면 "월간 크레딧", Free의 chat이면 "월간 채팅", 그 외 "월간 프리미엄 요청" |
| 사용 % | `100 − clamp(percent_remaining, 0, 100)`. `percent_remaining`이 없으면 `(entitlement − quota_remaining ?? remaining) / entitlement` |
| 무제한 | `unlimited == true` 또는 `entitlement == -1` → `TextOverride` "무제한"(`credits_used`가 있으면 "N 크레딧 사용", `has_quota == false`면 "조직 한도 도달") |
| 과금형 자리표시 | `!unlimited && entitlement == 0` → % 미터를 만들지 않는다. `credits_used`가 있으면 `TextOverride` "N 크레딧 사용"인 텍스트 줄, 없으면 미터 없음. **"0% 사용"을 절대 보이지 않는다** |
| 레거시 Free | 스냅샷이 없으면 `monthly_quotas`(총량)·`limited_user_quotas`(남은 양)로 chat/completions % 계산 |
| `reset` 줄 (짧은 라벨 `↻`) | `UsedPercent` null, `TextOverride` `↻ M/d`(표시 시간대 = `TimeProvider.LocalTimeZone`), `ResetsAt` = 리셋 시각. 주 미터나 플랜이 있을 때만 |
| `completions` (`완성`) | Free만. `completions` 스냅샷 또는 레거시 필드 |
| 리셋 시각 | 주 스냅샷 `quota_reset_at`(unix 초) → `quota_reset_date_utc` → `quota_reset_date` → `limited_user_reset_date`(ISO 우선, 실패하면 `yyyy-MM-dd` UTC 자정, 숫자면 unix 초) → 다음 달 1일 00:00 UTC |
| 견고성 | `quota_snapshots`의 각 항목을 따로 읽는다. 객체가 아닌 항목(`null`, 문자열)은 건너뛴다. 숫자 문자열(`"300"`)도 읽는다 |

**로그인 멤버 (Task 19 설정 창이 사용)**
- `SignInWithTokenAsync(token, ct)`: `copilot_internal/user`로 검증 → `GET https://api.github.com/user`로 `login` 조회(실패하면 사용량 응답의 `login`) → `ISecretStore.Save("copilot-token", {"token","login"})` → 이 토큰을 "기억한 후보"로 둔다. 실패하면 `CopilotSignInException`(메시지는 한국어, 토큰 미포함)을 던지고 아무것도 저장하지 않는다.
- `SignOut()`: 저장 토큰 삭제, `SignedInLogin = null`, 기억한 후보가 저장 토큰이면 잊는다. gh·환경 변수·CLI 토큰은 건드리지 않는다.
- `SignedInLogin`: 저장 토큰의 login(없으면 "알 수 없는 계정"), 저장 토큰이 없으면 null. **UI 스레드에서 DPAPI 파일을 읽지 않도록** 생성자에서 읽지 않고 `FetchAsync`(백그라운드)·`SignInWithTokenAsync`·`SignOut`이 갱신한다. 따라서 앱 시작 직후 첫 조회 전에는 null이다.

**Files:**
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotUsage.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotUsageParser.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotTokenCandidate.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotSavedToken.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotTokenSources.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/GitHubApi.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotMessages.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotSignInException.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/CopilotUsageProvider.cs`
- Create (fixtures): `tests/AITokenUsage.Core.Tests/Fixtures/copilot/` 아래 `user-paid-legacy.json`(A), `user-free-legacy.json`(B), `user-business-tbb.json`(C), `user-unlimited-org.json`, `apps.json`, `hosts.json`, `github-user.json`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Copilot/CopilotUsageParserTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Copilot/CopilotTokenSourcesTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Copilot/CopilotUsageProviderTests.cs`

**Interfaces:**
- Consumes (Task 2): `ProviderContext`, `IEnvironment.GetVariable/LocalAppData`, `IFileSystem.ReadAllTextShared`, `IProcessRunner.FindOnPath(string) : string?`, `IProcessRunner.RunAsync(ProcessSpec, CancellationToken) : Task<ProcessResult>`, `ProcessSpec(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?>? EnvironmentOverrides, TimeSpan? Timeout)`, `ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)`, `ICredentialStore.EnumerateGeneric(string filter) : IReadOnlyList<StoredCredential>`, `StoredCredential(string TargetName, string? UserName, string Secret)`, `ISecretStore.Save/Load/Delete`, `IAppLog`, `IUsageProvider`, `UsageMeter`, `UsageSnapshot`, `ProviderIds.Copilot`, `MeterOption`, `TimeProvider.LocalTimeZone`. 테스트: `FakeProcessRunner.SetPath/OnRun`, `FakeCredentialStore.Add`, `FakeSecretStore`, `FakeEnvironment.Set`, `FakeFileSystem.AddFile`, `StubHttpHandler`, `Fixture.Read`, `TestContexts.Create`.
- Consumes (Task 3): `Json.Str/Num/Bool/Obj`, `SharedJson.Lenient`.
- Produces:
  - `CopilotUsageProvider(ProviderContext ctx) : IUsageProvider` — `Id = "copilot"`, `DisplayName = "Copilot"`, `MinInterval = 5분`, `WatchPaths = []`, `StaticMeterOptions = [monthly "월간 사용량", reset "리셋 날짜", completions "코드 완성 (Free)"]`, `string? SignedInLogin { get; }`, `Task SignInWithTokenAsync(string token, CancellationToken ct)`(실패 시 `CopilotSignInException`), `void SignOut()`.
  - `sealed class CopilotSignInException(string message, Exception? innerException = null) : Exception` — `Message`는 한국어 사용자 메시지.
  - `static GitHubApi` — `const UserAgent = "AITokenUsage/1.0"`, `const CopilotApiVersion = "2025-04-01"`, `const CopilotUserUrl`, `const UserUrl`, `HttpRequestMessage CreateGet(string url, string token, string? apiVersion)`, `bool IsRateLimited(HttpResponseMessage)`, `TimeSpan? RetryAfter(HttpResponseMessage, DateTimeOffset now)`.
  - `CopilotTokenSources(ProviderContext ctx)` — `IAsyncEnumerable<CopilotTokenCandidate> EnumerateAsync(CancellationToken ct = default)`, `static IReadOnlyList<string> EnvironmentVariables`, `static TimeSpan GitHubCliTimeout`, `static bool HasAcceptedPrefix(string token)`.
  - `record CopilotTokenCandidate(CopilotTokenSourceKind Kind, string Description, string Token, string? Login)` (`ToString()`은 토큰 미포함), `enum CopilotTokenSourceKind { Saved, Environment, GitHubCli, CopilotCli, LanguageServerFile }`.
  - `record CopilotSavedToken(string Token, string? Login)` — `const SecretName = "copilot-token"`, `string ToJson()`, `static CopilotSavedToken? TryParse(string? json)`, `static CopilotSavedToken? Load(ISecretStore secrets, IAppLog log)`.
  - `record CopilotUsage(string? Login, string? PlanLabel, IReadOnlyList<UsageMeter> Meters)`; `static CopilotUsageParser` — `MeterOptions`, `CopilotUsage Parse(string json, DateTimeOffset now, TimeZoneInfo displayZone)`, `string? PlanLabel(JsonElement root)`, `DateTimeOffset ResolveReset(JsonElement root, JsonElement? primarySnapshot, DateTimeOffset now)`.
  - `static CopilotMessages` — `SignInRequired`, `NoSubscription`, `RateLimited`, `Network`, `BadResponse`, `TokenRejected`, `UnknownLogin`, `ServerError(int)`.

- [ ] **Step 1: 사용량 응답 픽스처 작성**

필드 이름은 연구 보고서 §2.3에서 확인된 것이고 값은 가공했다. 유료 개인 플랜의 `access_type_sku` 문자열은 확인되지 않아(UNCONFIRMED) 테스트용 가짜 문자열을 넣었다. C는 CodexBar #1258의 실제 형태대로 `unlimited` 필드가 **없다**.

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/user-paid-legacy.json` (A: Pro, 레거시 프리미엄 요청, 사용 76.3%):

```json
{
  "login": "octocat",
  "access_type_sku": "test_paid_individual_sku",
  "copilot_plan": "individual",
  "chat_enabled": true,
  "can_signup_for_limited": false,
  "token_based_billing": false,
  "quota_reset_date": "2026-11-01",
  "quota_reset_date_utc": "2026-11-01T00:00:00.000Z",
  "quota_snapshots": {
    "chat": { "quota_id": "chat", "entitlement": 0, "remaining": 0, "percent_remaining": 100, "unlimited": true, "overage_count": 0, "overage_permitted": false },
    "completions": { "quota_id": "completions", "entitlement": 0, "remaining": 0, "percent_remaining": 100, "unlimited": true, "overage_count": 0, "overage_permitted": false },
    "premium_interactions": {
      "quota_id": "premium_interactions",
      "entitlement": 300,
      "remaining": 71,
      "quota_remaining": 71,
      "percent_remaining": 23.7,
      "unlimited": false,
      "overage_count": 0,
      "overage_permitted": true,
      "overage_entitlement": 0,
      "timestamp_utc": "2026-10-28T09:00:00.000Z"
    }
  }
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/user-free-legacy.json` (B: Free 레거시 필드만):

```json
{
  "login": "free-user",
  "access_type_sku": "free_limited_copilot",
  "chat_enabled": true,
  "monthly_quotas": { "chat": 50, "completions": 2000 },
  "limited_user_quotas": { "chat": 31, "completions": 1650 },
  "limited_user_reset_date": "2026-11-01"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/user-business-tbb.json` (C: Business 과금형, `credits_used`):

```json
{
  "login": "biz-user",
  "access_type_sku": "copilot_for_business_seat_quota",
  "copilot_plan": "business",
  "token_based_billing": true,
  "quota_reset_date": "2026-09-01",
  "quota_snapshots": {
    "premium_interactions": { "entitlement": 0, "remaining": 0, "percent_remaining": 100, "quota_id": "premium_interactions", "credits_used": 31 },
    "chat": { "entitlement": 0, "remaining": 0, "percent_remaining": 100, "quota_id": "chat", "credits_used": 0 },
    "completions": { "entitlement": 0, "remaining": 0, "percent_remaining": 100, "quota_id": "completions" }
  }
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/user-unlimited-org.json` (조직 무제한 좌석):

```json
{
  "login": "enterprise-user",
  "access_type_sku": "test_enterprise_seat_sku",
  "copilot_plan": "enterprise",
  "token_based_billing": false,
  "quota_reset_date_utc": "2026-11-01T00:00:00.000Z",
  "organization_login_list": ["example-org"],
  "quota_snapshots": {
    "chat": { "quota_id": "chat", "entitlement": -1, "remaining": 0, "percent_remaining": 100, "unlimited": true },
    "completions": { "quota_id": "completions", "entitlement": -1, "remaining": 0, "percent_remaining": 100, "unlimited": true },
    "premium_interactions": { "quota_id": "premium_interactions", "entitlement": -1, "remaining": 0, "percent_remaining": 100, "unlimited": true, "overage_permitted": false }
  }
}
```

- [ ] **Step 2: 실패하는 테스트 작성 — 파서**

`tests/AITokenUsage.Core.Tests/Providers/Copilot/CopilotUsageParserTests.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class CopilotUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov1Utc = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Paid_legacy_premium_requests()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-paid-legacy.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("octocat", usage.Login);
        Assert.Equal("Pro", usage.PlanLabel);
        Assert.Equal(["monthly", "reset"], usage.Meters.Select(m => m.Key).ToArray());

        var monthly = usage.Meters[0];
        Assert.Equal("월", monthly.ShortLabel);
        Assert.Equal("월간 프리미엄 요청", monthly.LongLabel);
        Assert.Equal(76.3, monthly.UsedPercent!.Value, 6);
        Assert.Equal(Nov1Utc, monthly.ResetsAt);
        Assert.Null(monthly.TextOverride);

        var reset = usage.Meters[1];
        Assert.Null(reset.UsedPercent);
        Assert.Equal("↻ 11/1", reset.TextOverride);
        Assert.Equal(Nov1Utc, reset.ResetsAt);
    }

    [Fact]
    public void Free_legacy_fields_give_chat_and_completions()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-free-legacy.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Free", usage.PlanLabel);
        Assert.Equal(["monthly", "reset", "completions"], usage.Meters.Select(m => m.Key).ToArray());
        Assert.Equal("월간 채팅", usage.Meters[0].LongLabel);
        Assert.Equal(38, usage.Meters[0].UsedPercent!.Value, 6);
        Assert.Equal(Nov1Utc, usage.Meters[0].ResetsAt);
        Assert.Equal("완성", usage.Meters[2].ShortLabel);
        Assert.Equal(17.5, usage.Meters[2].UsedPercent!.Value, 6);
    }

    [Fact]
    public void Business_token_billing_shows_credits_used_text_instead_of_zero_percent()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-business-tbb.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Business", usage.PlanLabel);
        var monthly = usage.Meters.Single(m => m.Key == "monthly");
        Assert.Null(monthly.UsedPercent);
        Assert.Equal("31 크레딧 사용", monthly.TextOverride);
        Assert.Equal("월간 크레딧", monthly.LongLabel);
        Assert.Equal("↻ 9/1", usage.Meters.Single(m => m.Key == "reset").TextOverride);
        Assert.DoesNotContain(usage.Meters, m => m.Key == "completions");
    }

    [Fact]
    public void Unlimited_org_seat_shows_unlimited_text()
    {
        var usage = CopilotUsageParser.Parse(Fixture.Read("copilot/user-unlimited-org.json"), Now, TimeZoneInfo.Utc);

        Assert.Equal("Enterprise", usage.PlanLabel);
        var monthly = usage.Meters.Single(m => m.Key == "monthly");
        Assert.Null(monthly.UsedPercent);
        Assert.Equal("무제한", monthly.TextOverride);
    }

    [Theory]
    [InlineData(-15, 100)]
    [InlineData(120, 0)]
    [InlineData(50, 50)]
    public void Percent_remaining_is_clamped(double percentRemaining, double expectedUsed)
    {
        var json = """{"copilot_plan":"individual","quota_snapshots":{"premium_interactions":{"entitlement":300,"percent_remaining":PCT,"unlimited":false}}}"""
            .Replace("PCT", percentRemaining.ToString(CultureInfo.InvariantCulture));

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(expectedUsed, usage.Meters.Single(m => m.Key == "monthly").UsedPercent!.Value, 6);
    }

    [Fact]
    public void Free_prefers_chat_snapshot_over_premium()
    {
        var json = """
            {"access_type_sku":"free_limited_copilot","quota_snapshots":{
              "chat":{"entitlement":50,"percent_remaining":80,"unlimited":false},
              "premium_interactions":{"entitlement":10,"percent_remaining":10,"unlimited":false},
              "completions":{"entitlement":2000,"percent_remaining":90,"unlimited":false}}}
            """;

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(20, usage.Meters.Single(m => m.Key == "monthly").UsedPercent!.Value, 6);
        Assert.Equal(10, usage.Meters.Single(m => m.Key == "completions").UsedPercent!.Value, 6);
    }

    [Fact]
    public void Free_falls_back_to_premium_when_chat_is_null()
    {
        var json = """{"access_type_sku":"free_limited_copilot","quota_snapshots":{"chat":null,"premium_interactions":{"entitlement":10,"percent_remaining":40,"unlimited":false}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        var monthly = usage.Meters.Single(m => m.Key == "monthly");
        Assert.Equal(60, monthly.UsedPercent!.Value, 6);
        Assert.Equal("월간 프리미엄 요청", monthly.LongLabel);
    }

    [Fact]
    public void Token_based_billing_labels_the_meter_as_credits()
    {
        var json = """{"copilot_plan":"individual_pro","token_based_billing":true,"quota_snapshots":{"premium_interactions":{"entitlement":7000,"percent_remaining":75,"unlimited":false}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal("Pro+", usage.PlanLabel);
        Assert.Equal("월간 크레딧", usage.Meters.Single(m => m.Key == "monthly").LongLabel);
    }

    [Fact]
    public void Malformed_snapshot_entries_are_ignored_and_numeric_strings_accepted()
    {
        var json = """{"copilot_plan":"individual","quota_snapshots":{"chat":"oops","premium_interactions":{"entitlement":"300","percent_remaining":"40"}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(60, usage.Meters.Single(m => m.Key == "monthly").UsedPercent!.Value, 6);
    }

    [Fact]
    public void Zero_entitlement_without_credits_has_no_monthly_meter()
    {
        var json = """{"copilot_plan":"business","quota_snapshots":{"premium_interactions":{"entitlement":0,"percent_remaining":100}}}""";

        var usage = CopilotUsageParser.Parse(json, Now, TimeZoneInfo.Utc);

        Assert.Equal(["reset"], usage.Meters.Select(m => m.Key).ToArray());
    }

    [Theory]
    [InlineData("""{"quota_reset_date_utc":"2026-11-01T00:00:00Z","quota_snapshots":{"premium_interactions":{"quota_reset_at":1793880000}}}""", "2026-11-05T12:00:00+00:00")]
    [InlineData("""{"quota_reset_date_utc":"2026-11-02T06:30:00.000Z","quota_reset_date":"2026-11-03"}""", "2026-11-02T06:30:00+00:00")]
    [InlineData("""{"quota_reset_date":"2026-08-01T00:00:00Z","limited_user_reset_date":"2026-12-01"}""", "2026-08-01T00:00:00+00:00")]
    [InlineData("""{"quota_reset_date":"2026-12-01"}""", "2026-12-01T00:00:00+00:00")]
    [InlineData("""{"limited_user_reset_date":"2026-11-15"}""", "2026-11-15T00:00:00+00:00")]
    [InlineData("""{"quota_reset_date":"not a date"}""", "2026-11-01T00:00:00+00:00")]
    [InlineData("""{}""", "2026-11-01T00:00:00+00:00")]
    public void Reset_precedence(string json, string expectedIso)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement? primary = root.TryGetProperty("quota_snapshots", out var snaps) ? snaps.GetProperty("premium_interactions") : null;

        var reset = CopilotUsageParser.ResolveReset(root, primary, Now);

        Assert.Equal(DateTimeOffset.Parse(expectedIso), reset);
    }

    [Fact]
    public void Reset_text_uses_the_display_time_zone()
    {
        var minusFive = TimeZoneInfo.CreateCustomTimeZone("Test-05", TimeSpan.FromHours(-5), "Test-05", "Test-05");
        var plusNine = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var json = Fixture.Read("copilot/user-paid-legacy.json");

        Assert.Equal("↻ 10/31", CopilotUsageParser.Parse(json, Now, minusFive).Meters.Single(m => m.Key == "reset").TextOverride);
        Assert.Equal("↻ 11/1", CopilotUsageParser.Parse(json, Now, plusNine).Meters.Single(m => m.Key == "reset").TextOverride);
    }

    [Theory]
    [InlineData("""{"access_type_sku":"free_limited_copilot","copilot_plan":"individual"}""", "Free")]
    [InlineData("""{"access_type_sku":"free_educational_quota"}""", "Student")]
    [InlineData("""{"copilot_plan":"individual"}""", "Pro")]
    [InlineData("""{"copilot_plan":"individual_pro"}""", "Pro+")]
    [InlineData("""{"copilot_plan":"individual_max"}""", "Max")]
    [InlineData("""{"copilot_plan":"individual_edu"}""", "Student")]
    [InlineData("""{"copilot_plan":"business"}""", "Business")]
    [InlineData("""{"copilot_plan":"enterprise"}""", "Enterprise")]
    [InlineData("""{"can_signup_for_limited":true}""", null)]
    public void Plan_labels(string json, string? expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, CopilotUsageParser.PlanLabel(doc.RootElement));
    }

    [Fact]
    public void No_plan_and_no_quota_gives_no_meters()
    {
        var usage = CopilotUsageParser.Parse("""{"can_signup_for_limited":true}""", Now, TimeZoneInfo.Utc);

        Assert.Null(usage.PlanLabel);
        Assert.Empty(usage.Meters);
    }
}
```

- [ ] **Step 3: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CopilotUsageParserTests"`

Expected: 빌드 실패. `error CS0234: The type or namespace name 'Copilot' does not exist in the namespace 'AITokenUsage.Core.Providers'`

- [ ] **Step 4: 구현 — 사용량 모델과 파서**

`src/AITokenUsage.Core/Providers/Copilot/CopilotUsage.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Providers.Copilot;

public sealed record CopilotUsage(string? Login, string? PlanLabel, IReadOnlyList<UsageMeter> Meters);
```

`src/AITokenUsage.Core/Providers/Copilot/CopilotUsageParser.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>
/// copilot_internal/user → plan label + meters. Keys: "monthly" (primary), "reset" (text row "↻ M/d"),
/// "completions" (Free only). Each quota_snapshots entry is read on its own so one odd entry cannot fail the rest.
/// </summary>
public static class CopilotUsageParser
{
    public static readonly IReadOnlyList<MeterOption> MeterOptions =
    [
        new("monthly", "월간 사용량"),
        new("reset", "리셋 날짜"),
        new("completions", "코드 완성 (Free)"),
    ];

    private const string FreeSku = "free_limited_copilot";
    private const string StudentSku = "free_educational_quota";
    private const double MaxEpochSeconds = 253_402_300_799;

    /// <summary>Throws JsonException when the body is not a JSON object.</summary>
    public static CopilotUsage Parse(string json, DateTimeOffset now, TimeZoneInfo displayZone)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("copilot_internal/user response is not a JSON object.");
        }

        var isFree = Json.Str(root, "access_type_sku") == FreeSku;
        var tokenBased = Json.Bool(root, "token_based_billing") == true;
        var snapshots = Json.Obj(root, "quota_snapshots");
        var chat = Snapshot(snapshots, "chat");
        var premium = Snapshot(snapshots, "premium_interactions");
        var primary = isFree ? chat ?? premium : premium;
        var resetsAt = ResolveReset(root, primary, now);
        var planLabel = PlanLabel(root);

        var monthlyLabel = tokenBased ? "월간 크레딧" : isFree && chat is not null ? "월간 채팅" : "월간 프리미엄 요청";
        var monthly = primary is { } snapshot
            ? SnapshotMeter("monthly", "월", monthlyLabel, snapshot, resetsAt)
            : isFree ? LegacyMeter(root, "chat", "monthly", "월", "월간 채팅", resetsAt) : null;

        var meters = new List<UsageMeter>();
        if (monthly is not null)
        {
            meters.Add(monthly);
        }

        if (monthly is not null || planLabel is not null)
        {
            meters.Add(new UsageMeter("reset", "↻", "리셋", null, resetsAt, TextOverride: ResetText(resetsAt, displayZone)));
        }

        if (isFree)
        {
            var completions = Snapshot(snapshots, "completions") is { } c
                ? SnapshotMeter("completions", "완성", "코드 완성 (월)", c, resetsAt)
                : LegacyMeter(root, "completions", "completions", "완성", "코드 완성 (월)", resetsAt);
            if (completions is not null)
            {
                meters.Add(completions);
            }
        }

        return new CopilotUsage(Json.Str(root, "login"), planLabel, meters);
    }

    public static string? PlanLabel(JsonElement root)
    {
        switch (Json.Str(root, "access_type_sku"))
        {
            case FreeSku:
                return "Free";
            case StudentSku:
                return "Student";
        }

        return Json.Str(root, "copilot_plan") switch
        {
            "individual" => "Pro",
            "individual_pro" => "Pro+",
            "individual_max" => "Max",
            "individual_edu" => "Student",
            "business" => "Business",
            "enterprise" => "Enterprise",
            _ => null,
        };
    }

    /// <summary>
    /// snapshot.quota_reset_at (unix s) → quota_reset_date_utc → quota_reset_date → limited_user_reset_date
    /// (ISO first, else yyyy-MM-dd as UTC midnight) → next 1st of the month 00:00 UTC.
    /// </summary>
    public static DateTimeOffset ResolveReset(JsonElement root, JsonElement? primarySnapshot, DateTimeOffset now)
    {
        if (primarySnapshot is { } snapshot && Json.Num(snapshot, "quota_reset_at") is double seconds
            && seconds is > 0 and < MaxEpochSeconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }

        foreach (var name in new[] { "quota_reset_date_utc", "quota_reset_date", "limited_user_reset_date" })
        {
            if (ReadDate(root, name) is { } date)
            {
                return date;
            }
        }

        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    private static UsageMeter? SnapshotMeter(string key, string shortLabel, string longLabel, JsonElement snapshot, DateTimeOffset resetsAt)
    {
        var entitlement = Json.Num(snapshot, "entitlement");
        var creditsUsed = Json.Num(snapshot, "credits_used");
        var unlimited = Json.Bool(snapshot, "unlimited") == true || entitlement == -1;

        if (unlimited)
        {
            var text = Json.Bool(snapshot, "has_quota") == false ? "조직 한도 도달"
                : creditsUsed is { } pooled ? CreditsText(pooled)
                : "무제한";
            return new UsageMeter(key, shortLabel, longLabel, null, resetsAt, TextOverride: text);
        }

        if (entitlement == 0)
        {
            // Business token-based billing placeholder: never show "0% used"; show credits if present.
            return creditsUsed is { } used
                ? new UsageMeter(key, shortLabel, longLabel, null, resetsAt, TextOverride: CreditsText(used))
                : null;
        }

        var percentRemaining = Json.Num(snapshot, "percent_remaining");
        var usedPercent = percentRemaining is { } remaining
            ? 100 - Math.Clamp(remaining, 0, 100)
            : UsedFromCounts(snapshot, entitlement);
        return usedPercent is { } value
            ? new UsageMeter(key, shortLabel, longLabel, Math.Round(value, 2), resetsAt)
            : null;
    }

    private static double? UsedFromCounts(JsonElement snapshot, double? entitlement)
    {
        if (entitlement is not > 0)
        {
            return null;
        }

        var remaining = Json.Num(snapshot, "quota_remaining") ?? Json.Num(snapshot, "remaining");
        return remaining is { } left
            ? Math.Clamp((entitlement.Value - left) / entitlement.Value * 100, 0, 100)
            : null;
    }

    /// <summary>Legacy Free: monthly_quotas (allowance) and limited_user_quotas (remaining).</summary>
    private static UsageMeter? LegacyMeter(JsonElement root, string quotaName, string key, string shortLabel, string longLabel, DateTimeOffset resetsAt)
    {
        if (Json.Obj(root, "monthly_quotas") is not { } monthly || Json.Obj(root, "limited_user_quotas") is not { } remaining)
        {
            return null;
        }

        var total = Json.Num(monthly, quotaName);
        var left = Json.Num(remaining, quotaName);
        if (total is not > 0 || left is null)
        {
            return null;
        }

        var used = 100 - Math.Clamp(left.Value / total.Value * 100, 0, 100);
        return new UsageMeter(key, shortLabel, longLabel, Math.Round(used, 2), resetsAt);
    }

    private static JsonElement? Snapshot(JsonElement? snapshots, string name) =>
        snapshots is { } s ? Json.Obj(s, name) : null;

    private static DateTimeOffset? ReadDate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds is > 0 and < MaxEpochSeconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }

        var text = value.GetString()!.Trim();
        const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, utc, out var iso))
        {
            return iso;
        }

        return DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, utc, out var day) ? day : null;
    }

    private static string CreditsText(double credits) =>
        $"{credits.ToString("0.##", CultureInfo.InvariantCulture)} 크레딧 사용";

    private static string ResetText(DateTimeOffset resetsAt, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(resetsAt, zone);
        return $"↻ {local.Month}/{local.Day}";
    }
}
```

- [ ] **Step 5: 테스트 실행 → 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CopilotUsageParserTests"`

Expected: `Passed!  - Failed:     0, Passed:    30`

- [ ] **Step 6: 언어 서버 파일 픽스처 작성**

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/apps.json` (두 번째 키는 `github.com`이 아니라서 건너뛰어야 한다):

```json
{
  "github.com:Iv1.b507a08c87ecfe98": {
    "user": "octocat",
    "oauth_token": "ghu_test_apps_token",
    "githubAppId": "Iv1.b507a08c87ecfe98"
  },
  "ghe.example.com:Iv1.b507a08c87ecfe98": {
    "user": "someone-else",
    "oauth_token": "ghu_test_other_host_token",
    "githubAppId": "Iv1.b507a08c87ecfe98"
  }
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/hosts.json`:

```json
{
  "github.com": {
    "user": "octocat",
    "oauth_token": "gho_test_hosts_token"
  }
}
```

- [ ] **Step 7: 실패하는 테스트 작성 — 토큰 후보 순서와 필터**

`tests/AITokenUsage.Core.Tests/Providers/Copilot/CopilotTokenSourcesTests.cs`:

```csharp
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class CopilotTokenSourcesTests
{
    private const string GhPath = @"C:\Program Files\GitHub CLI\gh.exe";

    private readonly FakeTimeProvider _time = new();
    private readonly StubHttpHandler _http = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly List<ProcessSpec> _ghCalls = [];

    [Fact]
    public async Task Yields_every_source_in_spec_order()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        _env.Set("COPILOT_GITHUB_TOKEN", "github_pat_env1");
        _env.Set("GH_TOKEN", "gho_env2");
        _env.Set("GITHUB_TOKEN", "ghu_env3");
        GivenGh("gho_cli\n");
        _credentials.Add("copilot-cli/https://github.com:octocat", "https://github.com:octocat", "gho_copilot_cli");
        GivenLanguageServerFile("apps.json", Fixture.Read("copilot/apps.json"));
        GivenLanguageServerFile("hosts.json", Fixture.Read("copilot/hosts.json"));

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Equal(
            [
                "saved", "env:COPILOT_GITHUB_TOKEN", "env:GH_TOKEN", "env:GITHUB_TOKEN", "gh auth token",
                "credman:copilot-cli/https://github.com:octocat", "file:apps.json", "file:hosts.json",
            ],
            candidates.Select(c => c.Description).ToArray());
        Assert.Equal(
            ["gho_saved", "github_pat_env1", "gho_env2", "ghu_env3", "gho_cli", "gho_copilot_cli", "ghu_test_apps_token", "gho_test_hosts_token"],
            candidates.Select(c => c.Token).ToArray());
        Assert.Equal(CopilotTokenSourceKind.Saved, candidates[0].Kind);
        Assert.Equal("saved-user", candidates[0].Login);
        Assert.Equal("octocat", candidates[5].Login);
        Assert.Equal(CopilotTokenSourceKind.LanguageServerFile, candidates[6].Kind);
        Assert.Equal("octocat", candidates[6].Login);
    }

    [Fact]
    public async Task Environment_tokens_need_an_accepted_prefix()
    {
        _env.Set("COPILOT_GITHUB_TOKEN", "ghp_classic_pat");
        _env.Set("GH_TOKEN", "  gho_trimmed  ");
        _env.Set("GITHUB_TOKEN", "some-random-value");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        var only = Assert.Single(candidates);
        Assert.Equal("gho_trimmed", only.Token);
        Assert.False(CopilotTokenSources.HasAcceptedPrefix("ghp_x"));
        Assert.True(CopilotTokenSources.HasAcceptedPrefix("github_pat_x"));
        Assert.True(CopilotTokenSources.HasAcceptedPrefix("ghu_x"));
    }

    [Fact]
    public async Task Gh_runs_with_token_env_removed_and_five_second_timeout()
    {
        GivenGh("gho_cli\r\n");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Equal("gho_cli", Assert.Single(candidates).Token);
        var spec = Assert.Single(_ghCalls);
        Assert.Equal(GhPath, spec.FileName);
        Assert.Equal(["auth", "token", "--hostname", "github.com"], spec.Arguments.ToArray());
        Assert.NotNull(spec.EnvironmentOverrides);
        Assert.True(spec.EnvironmentOverrides!.ContainsKey("GH_TOKEN"));
        Assert.Null(spec.EnvironmentOverrides["GH_TOKEN"]);
        Assert.True(spec.EnvironmentOverrides.ContainsKey("GITHUB_TOKEN"));
        Assert.Null(spec.EnvironmentOverrides["GITHUB_TOKEN"]);
        Assert.Equal(TimeSpan.FromSeconds(5), spec.Timeout);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task Gh_failure_or_timeout_yields_nothing(int exitCode, bool timedOut)
    {
        GivenGh("gho_should_not_be_used", exitCode, timedOut);

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Gh_is_not_run_when_missing_from_path()
    {
        _processes.OnRun(spec =>
        {
            _ghCalls.Add(spec);
            return new ProcessResult(0, "gho_unexpected", "", false);
        });

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Empty(candidates);
        Assert.Empty(_ghCalls);
    }

    [Fact]
    public async Task Enumeration_is_lazy_so_gh_does_not_run_when_saved_token_is_taken()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        GivenGh("gho_cli");

        await foreach (var candidate in CreateSources().EnumerateAsync(CancellationToken.None))
        {
            Assert.Equal("gho_saved", candidate.Token);
            break;
        }

        Assert.Empty(_ghCalls);
    }

    [Fact]
    public async Task Duplicate_tokens_are_yielded_once()
    {
        _env.Set("GH_TOKEN", "gho_same");
        GivenGh("gho_same");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Equal("env:GH_TOKEN", Assert.Single(candidates).Description);
    }

    [Fact]
    public async Task Broken_language_server_files_are_skipped()
    {
        GivenLanguageServerFile("apps.json", "{ not json");
        GivenLanguageServerFile("hosts.json", """{"github.com":{"user":"octocat"}}""");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidate_ToString_hides_the_token()
    {
        var candidate = new CopilotTokenCandidate(CopilotTokenSourceKind.Environment, "env:GH_TOKEN", "gho_secret_value", null);

        Assert.DoesNotContain("gho_secret_value", candidate.ToString());
    }

    private CopilotTokenSources CreateSources() =>
        new(TestContexts.Create(_http, _time, _files, _env, _processes, _credentials, _secrets));

    private void GivenGh(string stdout, int exitCode = 0, bool timedOut = false)
    {
        _processes.SetPath("gh", GhPath);
        _processes.OnRun(spec =>
        {
            _ghCalls.Add(spec);
            return new ProcessResult(exitCode, stdout, "", timedOut);
        });
    }

    private void GivenLanguageServerFile(string name, string content) =>
        _files.AddFile(Path.Combine(_env.LocalAppData, "github-copilot", name), content);

    private static async Task<List<CopilotTokenCandidate>> ToListAsync(IAsyncEnumerable<CopilotTokenCandidate> source)
    {
        var list = new List<CopilotTokenCandidate>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
```

- [ ] **Step 8: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CopilotTokenSourcesTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'CopilotTokenSources' could not be found`, `'CopilotTokenCandidate' could not be found`, 그리고 `CopilotSavedToken`·`CopilotTokenSourceKind`에 대한 `error CS0103`.

- [ ] **Step 9: 구현 — 후보 형식, 저장 토큰, 후보 열거**

`src/AITokenUsage.Core/Providers/Copilot/CopilotTokenCandidate.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Copilot;

public enum CopilotTokenSourceKind { Saved, Environment, GitHubCli, CopilotCli, LanguageServerFile }

/// <param name="Description">Log-safe origin such as "env:GH_TOKEN" (never the token).</param>
public sealed record CopilotTokenCandidate(CopilotTokenSourceKind Kind, string Description, string Token, string? Login)
{
    // Records print every property by default; keep the token out of logs and exception messages.
    public override string ToString() => $"{Kind} ({Description})";
}
```

`src/AITokenUsage.Core/Providers/Copilot/CopilotSavedToken.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>The device-flow token the app saved through ISecretStore (DPAPI) as {"token","login"}.</summary>
public sealed record CopilotSavedToken(string Token, string? Login)
{
    public const string SecretName = "copilot-token";

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("token", Token);
            if (Login is null)
            {
                writer.WriteNull("login");
            }
            else
            {
                writer.WriteString("login", Login);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static CopilotSavedToken? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var token = Json.Str(root, "token");
            return string.IsNullOrWhiteSpace(token) ? null : new CopilotSavedToken(token.Trim(), Json.Str(root, "login"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Loads and parses the saved token; unreadable or undecryptable data counts as "none".</summary>
    public static CopilotSavedToken? Load(ISecretStore secrets, IAppLog log)
    {
        try
        {
            return TryParse(secrets.Load(SecretName));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            log.Warn("Copilot: saved token could not be read", ex);
            return null;
        }
    }

    public override string ToString() => $"CopilotSavedToken {{ Login = {Login} }}";
}
```

`src/AITokenUsage.Core/Providers/Copilot/CopilotTokenSources.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>
/// Yields GitHub token candidates lazily, in spec order: saved device-flow token → env vars →
/// gh auth token → Copilot CLI Credential Manager entries → language-server apps.json/hosts.json.
/// Duplicate tokens are yielded once. Nothing here validates a token; the provider does that live.
/// </summary>
public sealed class CopilotTokenSources(ProviderContext ctx)
{
    public static readonly IReadOnlyList<string> EnvironmentVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];
    public static readonly TimeSpan GitHubCliTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] AcceptedEnvPrefixes = ["gho_", "ghu_", "github_pat_"];
    private static readonly string[] LanguageServerFiles = ["apps.json", "hosts.json"];

    /// <summary>Classic PATs (ghp_) and unknown prefixes are rejected, as the Copilot CLI does.</summary>
    public static bool HasAcceptedPrefix(string token) =>
        AcceptedEnvPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal));

    public async IAsyncEnumerable<CopilotTokenCandidate> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var saved = CopilotSavedToken.Load(ctx.Secrets, ctx.Log);
        if (saved is not null && seen.Add(saved.Token))
        {
            yield return new CopilotTokenCandidate(CopilotTokenSourceKind.Saved, "saved", saved.Token, saved.Login);
        }

        foreach (var name in EnvironmentVariables)
        {
            var value = ctx.Env.GetVariable(name)?.Trim();
            if (!string.IsNullOrEmpty(value) && HasAcceptedPrefix(value) && seen.Add(value))
            {
                yield return new CopilotTokenCandidate(CopilotTokenSourceKind.Environment, $"env:{name}", value, null);
            }
        }

        var fromGitHubCli = await ReadGitHubCliAsync(ct).ConfigureAwait(false);
        if (fromGitHubCli is not null && seen.Add(fromGitHubCli.Token))
        {
            yield return fromGitHubCli;
        }

        foreach (var candidate in ReadCopilotCliCredentials())
        {
            if (seen.Add(candidate.Token))
            {
                yield return candidate;
            }
        }

        foreach (var fileName in LanguageServerFiles)
        {
            foreach (var candidate in ReadLanguageServerFile(fileName))
            {
                if (seen.Add(candidate.Token))
                {
                    yield return candidate;
                }
            }
        }
    }

    private async Task<CopilotTokenCandidate?> ReadGitHubCliAsync(CancellationToken ct)
    {
        var gh = ctx.Processes.FindOnPath("gh");
        if (gh is null)
        {
            return null;
        }

        // Remove GH_TOKEN/GITHUB_TOKEN so gh returns its stored login instead of echoing the env token.
        var spec = new ProcessSpec(
            gh,
            ["auth", "token", "--hostname", "github.com"],
            new Dictionary<string, string?> { ["GH_TOKEN"] = null, ["GITHUB_TOKEN"] = null },
            GitHubCliTimeout);

        ProcessResult result;
        try
        {
            result = await ctx.Processes.RunAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            ctx.Log.Warn("Copilot: could not run gh", ex);
            return null;
        }

        if (result.TimedOut || result.ExitCode != 0)
        {
            ctx.Log.Info($"Copilot: gh auth token unavailable (exit {result.ExitCode}, timed out {result.TimedOut})");
            return null;
        }

        var token = result.StdOut
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(token)
            ? null
            : new CopilotTokenCandidate(CopilotTokenSourceKind.GitHubCli, "gh auth token", token, null);
    }

    private List<CopilotTokenCandidate> ReadCopilotCliCredentials()
    {
        IReadOnlyList<StoredCredential> items;
        try
        {
            items = ctx.Credentials.EnumerateGeneric("copilot-cli*");
        }
        catch (Win32Exception ex)
        {
            ctx.Log.Warn("Copilot: CredEnumerate failed", ex);
            return [];
        }

        var result = new List<CopilotTokenCandidate>();
        foreach (var item in items)
        {
            var token = item.Secret.Trim();
            if (token.Length > 0)
            {
                result.Add(new CopilotTokenCandidate(CopilotTokenSourceKind.CopilotCli, $"credman:{item.TargetName}",
                    token, LoginFromKeytarAccount(item.UserName)));
            }
        }

        return result;
    }

    private List<CopilotTokenCandidate> ReadLanguageServerFile(string fileName)
    {
        var path = Path.Combine(ctx.Env.LocalAppData, "github-copilot", fileName);
        string? text;
        try
        {
            text = ctx.Files.ReadAllTextShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log.Warn($"Copilot: could not read {fileName}", ex);
            return [];
        }

        var result = new List<CopilotTokenCandidate>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, SharedJson.Lenient);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!entry.Name.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                    || entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var token = Json.Str(entry.Value, "oauth_token") ?? Json.Str(entry.Value, "access_token");
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var login = Json.Str(entry.Value, "user") ?? Json.Str(entry.Value, "login");
                result.Add(new CopilotTokenCandidate(CopilotTokenSourceKind.LanguageServerFile, $"file:{fileName}", token.Trim(), login));
            }
        }
        catch (JsonException)
        {
            ctx.Log.Warn($"Copilot: {fileName} is not valid JSON");
        }

        return result;
    }

    /// <summary>keytar account "https://github.com:&lt;login&gt;" → login.</summary>
    private static string? LoginFromKeytarAccount(string? account)
    {
        if (string.IsNullOrEmpty(account))
        {
            return null;
        }

        var login = account[(account.LastIndexOf(':') + 1)..];
        return login.Length == 0 || login.Contains('/') ? null : login;
    }
}
```

- [ ] **Step 10: 테스트 실행 → 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CopilotTokenSourcesTests"`

Expected: `Passed!  - Failed:     0, Passed:    10`

- [ ] **Step 11: `GET /user` 픽스처 작성**

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/github-user.json`:

```json
{
  "login": "octocat",
  "id": 1,
  "type": "User",
  "name": "Test User"
}
```

- [ ] **Step 12: 실패하는 테스트 작성 — 공급자, 레이트 리밋, 로그인 멤버**

모든 `copilot_internal/user` 요청을 생성자에서 등록한 처리기 하나가 받고, `Authorization`의 토큰 값으로 응답을 고른다(등록하지 않은 토큰은 401). 이 처리기가 `User-Agent`·`Accept`·`X-GitHub-Api-Version`도 기록한다.

`tests/AITokenUsage.Core.Tests/Providers/Copilot/CopilotUsageProviderTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class CopilotUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _http = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly FakeSecretStore _secrets = new();

    private readonly Dictionary<string, Func<HttpResponseMessage>> _repliesByToken = [];
    private readonly List<string?> _tokensTried = [];
    private int _ghRuns;
    private string? _userAgent;
    private string? _accept;
    private string? _apiVersion;
    private string? _authScheme;

    public CopilotUsageProviderTests()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.CopilotUserUrl, r =>
        {
            var token = r.Headers.Authorization?.Parameter;
            _tokensTried.Add(token);
            _authScheme = r.Headers.Authorization?.Scheme;
            _userAgent = r.Headers.UserAgent.ToString();
            _accept = r.Headers.Accept.ToString();
            _apiVersion = r.Headers.TryGetValues("X-GitHub-Api-Version", out var values) ? values.Single() : null;
            return token is not null && _repliesByToken.TryGetValue(token, out var reply)
                ? reply()
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
    }

    [Fact]
    public void Metadata_matches_spec()
    {
        var provider = CreateProvider();

        Assert.Equal("copilot", provider.Id);
        Assert.Equal("Copilot", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(5), provider.MinInterval);
        Assert.Empty(provider.WatchPaths);
        Assert.Equal(["monthly", "reset", "completions"], provider.StaticMeterOptions.Select(o => o.Key).ToArray());
        Assert.Null(provider.SignedInLogin);
    }

    [Fact]
    public async Task Fetch_sends_required_headers_and_parses_usage()
    {
        _env.Set("GH_TOKEN", "gho_env");
        _repliesByToken["gho_env"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Pro", snapshot.PlanLabel);
        Assert.Equal(["monthly", "reset"], snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal("Bearer", _authScheme);
        Assert.Equal(GitHubApi.UserAgent, _userAgent);
        Assert.False(string.IsNullOrWhiteSpace(_userAgent));
        Assert.Contains("application/json", _accept);
        Assert.Equal("2025-04-01", _apiVersion);
    }

    [Fact]
    public async Task Falls_through_rejected_tokens_and_remembers_the_working_source()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        _env.Set("GH_TOKEN", "gho_env");
        GivenGh("gho_cli");
        _repliesByToken["gho_saved"] = Reply(HttpStatusCode.Unauthorized, "{}");
        _repliesByToken["gho_env"] = Reply(HttpStatusCode.NotFound, "{}");
        _repliesByToken["gho_cli"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        Assert.Equal(["gho_saved", "gho_env", "gho_cli", "gho_cli"], _tokensTried);
        Assert.Equal(1, _ghRuns);
    }

    [Fact]
    public async Task Forbidden_without_rate_limit_headers_falls_through()
    {
        _env.Set("COPILOT_GITHUB_TOKEN", "gho_first");
        _env.Set("GH_TOKEN", "gho_second");
        _repliesByToken["gho_first"] = Reply(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");
        _repliesByToken["gho_second"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["gho_first", "gho_second"], _tokensTried);
    }

    [Fact]
    public async Task Remembered_token_that_starts_failing_restarts_the_search()
    {
        _env.Set("GH_TOKEN", "gho_env");
        _env.Set("GITHUB_TOKEN", "gho_backup");
        _repliesByToken["gho_env"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        _repliesByToken["gho_backup"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();
        await provider.FetchAsync(CancellationToken.None);

        _repliesByToken["gho_env"] = Reply(HttpStatusCode.Unauthorized, "{}");
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["gho_env", "gho_env", "gho_backup"], _tokensTried);
    }

    [Fact]
    public async Task No_candidates_report_SignedOut_without_network()
    {
        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(CopilotMessages.SignInRequired, snapshot.StatusMessage);
        Assert.Empty(_tokensTried);
    }

    [Fact]
    public async Task All_candidates_rejected_report_SignedOut()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _env.Set("GITHUB_TOKEN", "gho_b");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(["gho_a", "gho_b"], _tokensTried);
    }

    [Fact]
    public async Task Status_429_stops_the_search_and_reports_retry_after()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _env.Set("GITHUB_TOKEN", "gho_b");
        _repliesByToken["gho_a"] = () =>
        {
            var response = Reply(HttpStatusCode.TooManyRequests, "{}")();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        };

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), snapshot.RetryAfter);
        Assert.Equal(["gho_a"], _tokensTried);
    }

    [Fact]
    public async Task Forbidden_with_zero_remaining_is_rate_limited()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = () =>
        {
            var response = Reply(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""")();
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", Now.AddMinutes(10).ToUnixTimeSeconds().ToString());
            return response;
        };

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Any_error_with_retry_after_is_rate_limited()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = () =>
        {
            var response = Reply(HttpStatusCode.ServiceUnavailable, "{}")();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Server_error_reports_Error()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = Reply(HttpStatusCode.InternalServerError, "{}");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal(CopilotMessages.ServerError(500), snapshot.StatusMessage);
    }

    [Fact]
    public async Task No_plan_reports_Unsupported()
    {
        _env.Set("GH_TOKEN", "gho_a");
        _repliesByToken["gho_a"] = Reply(HttpStatusCode.OK, """{"login":"octocat","can_signup_for_limited":true}""");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal(CopilotMessages.NoSubscription, snapshot.StatusMessage);
    }

    [Fact]
    public async Task SignIn_validates_saves_token_json_and_exposes_login()
    {
        GivenUserEndpoint();
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        var saved = CopilotSavedToken.TryParse(_secrets.Load(CopilotSavedToken.SecretName));
        Assert.NotNull(saved);
        Assert.Equal("ghu_device", saved!.Token);
        Assert.Equal("octocat", saved.Login);
        Assert.Equal("octocat", provider.SignedInLogin);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["ghu_device", "ghu_device"], _tokensTried);
    }

    [Fact]
    public async Task SignIn_with_rejected_token_throws_and_saves_nothing()
    {
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<CopilotSignInException>(
            () => provider.SignInWithTokenAsync("ghu_bad", CancellationToken.None));

        Assert.Equal(CopilotMessages.TokenRejected, ex.Message);
        Assert.DoesNotContain("ghu_bad", ex.ToString());
        Assert.Null(_secrets.Load(CopilotSavedToken.SecretName));
        Assert.Null(provider.SignedInLogin);
    }

    [Fact]
    public async Task SignOut_deletes_saved_token_and_forgets_it()
    {
        GivenUserEndpoint();
        _repliesByToken["ghu_device"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();
        await provider.SignInWithTokenAsync("ghu_device", CancellationToken.None);

        provider.SignOut();
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Null(_secrets.Load(CopilotSavedToken.SecretName));
        Assert.Null(provider.SignedInLogin);
        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal(["ghu_device"], _tokensTried); // only the sign-in validation call
    }

    [Fact]
    public async Task SignedInLogin_is_loaded_from_the_saved_token_on_fetch()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        _repliesByToken["gho_saved"] = Reply(HttpStatusCode.OK, Fixture.Read("copilot/user-paid-legacy.json"));
        var provider = CreateProvider();

        await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("saved-user", provider.SignedInLogin);
    }

    private CopilotUsageProvider CreateProvider() =>
        new(TestContexts.Create(_http, _time, _files, _env, _processes, _credentials, _secrets));

    private void GivenGh(string stdout)
    {
        _processes.SetPath("gh", @"C:\Program Files\GitHub CLI\gh.exe");
        _processes.OnRun(_ =>
        {
            _ghRuns++;
            return new ProcessResult(0, stdout, "", false);
        });
    }

    private void GivenUserEndpoint() =>
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubApi.UserUrl,
            _ => Reply(HttpStatusCode.OK, Fixture.Read("copilot/github-user.json"))());

    private static Func<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
```

- [ ] **Step 13: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CopilotUsageProviderTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'CopilotUsageProvider' could not be found`, `'CopilotSignInException' could not be found`, 그리고 `GitHubApi`·`CopilotMessages`에 대한 `error CS0103`.

- [ ] **Step 14: 구현 — GitHub 요청 공용 코드, 메시지, 예외, 공급자**

`src/AITokenUsage.Core/Providers/Copilot/GitHubApi.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>Shared GitHub request details. GitHub rejects requests without a User-Agent.</summary>
public static class GitHubApi
{
    public const string UserAgent = "AITokenUsage/1.0";
    public const string CopilotApiVersion = "2025-04-01";
    public const string CopilotUserUrl = "https://api.github.com/copilot_internal/user";
    public const string UserUrl = "https://api.github.com/user";

    /// <param name="apiVersion">X-GitHub-Api-Version, or null to omit (REST endpoints reject unknown versions).</param>
    public static HttpRequestMessage CreateGet(string url, string token, string? apiVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (apiVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", apiVersion);
        }

        return request;
    }

    /// <summary>429, 403 with x-ratelimit-remaining: 0, or any non-2xx carrying Retry-After.</summary>
    public static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (!response.IsSuccessStatusCode && response.Headers.RetryAfter is not null)
        {
            return true;
        }

        return response.StatusCode == HttpStatusCode.Forbidden
               && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
               && remaining.FirstOrDefault()?.Trim() == "0";
    }

    /// <summary>Retry-After (delta or date), else x-ratelimit-reset (epoch seconds), else null.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            return date > now ? date - now : TimeSpan.Zero;
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            && epoch is > 0 and < 253_402_300_799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return reset > now ? reset - now : TimeSpan.Zero;
        }

        return null;
    }
}
```

`src/AITokenUsage.Core/Providers/Copilot/CopilotMessages.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>User-facing (Korean) messages for the Copilot provider and sign-in.</summary>
public static class CopilotMessages
{
    public const string SignInRequired = "설정에서 GitHub 로그인";
    public const string NoSubscription = "Copilot 구독 없음";
    public const string RateLimited = "요청 제한 — 잠시 후 다시 시도";
    public const string Network = "네트워크 오류 — 잠시 후 다시 시도";
    public const string BadResponse = "응답 형식을 해석할 수 없음";
    public const string TokenRejected = "이 GitHub 계정으로는 Copilot 사용량을 조회할 수 없습니다";
    public const string UnknownLogin = "알 수 없는 계정";

    public static string ServerError(int httpStatus) => $"GitHub 응답 오류 (HTTP {httpStatus})";
}
```

`src/AITokenUsage.Core/Providers/Copilot/CopilotSignInException.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>Sign-in failed; <see cref="Exception.Message"/> is a Korean user-facing message (never contains the token).</summary>
public sealed class CopilotSignInException(string message, Exception? innerException = null)
    : Exception(message, innerException);
```

`src/AITokenUsage.Core/Providers/Copilot/CopilotUsageProvider.cs`:

```csharp
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
        _ctx.Secrets.Delete(CopilotSavedToken.SecretName);
        lock (_gate)
        {
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
        var saved = CopilotSavedToken.Load(_ctx.Secrets, _ctx.Log);
        lock (_gate)
        {
            _signedInLogin = saved is null ? null : DisplayLogin(saved);
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
            var result = await TryCandidateAsync(remembered, ct).ConfigureAwait(false);
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

            var result = await TryCandidateAsync(candidate, ct).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        _ctx.Log.Info("Copilot: no usable GitHub token found");
        return Status(ProviderStatus.SignedOut, CopilotMessages.SignInRequired);
    }

    /// <summary>Returns the snapshot to report, or null when this token was rejected and the next one should be tried.</summary>
    private async Task<UsageSnapshot?> TryCandidateAsync(CopilotTokenCandidate candidate, CancellationToken ct)
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
            _working = candidate;
        }

        if (usage.PlanLabel is null && usage.Meters.Count == 0)
        {
            return Status(ProviderStatus.Unsupported, CopilotMessages.NoSubscription);
        }

        return new UsageSnapshot(Id, ProviderStatus.Ok, usage.PlanLabel, usage.Meters, now);
    }

    private async Task<string?> FetchLoginAsync(string token, CancellationToken ct)
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

    private static bool IsRejection(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    private static string DisplayLogin(CopilotSavedToken saved) => saved.Login ?? CopilotMessages.UnknownLogin;

    private UsageSnapshot Status(ProviderStatus status, string message, TimeSpan? retryAfter = null) =>
        UsageSnapshot.WithStatus(Id, status, _ctx.Time.GetUtcNow(), message, retryAfter);
}
```

- [ ] **Step 15: Copilot 테스트 전체와 솔루션 테스트 실행 → 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~Providers.Copilot"`

Expected: `Passed!  - Failed:     0, Passed:    56`

Run: `dotnet test AITokenUsage.sln`

Expected: `Failed:     0`, 빌드 `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 16: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Copilot tests/AITokenUsage.Core.Tests/Providers/Copilot tests/AITokenUsage.Core.Tests/Fixtures/copilot
git commit -m "feat(core): GitHub Copilot 사용량 공급자 추가" -m "저장 토큰·환경 변수·gh·CLI 자격 증명·언어 서버 파일 순서로 토큰을 실제 호출로 검증하고, 월간 미터·리셋 날짜 줄·Free 코드 완성 미터와 로그인/로그아웃 멤버를 제공한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: `GitHubDeviceFlow` + `ProviderCatalog`

Copilot 토큰을 하나도 찾지 못했을 때 설정 창(Task 19)이 쓰는 GitHub 기기 코드 로그인과, 앱 합성 루트(Task 14)가 쓰는 공급자 카탈로그를 만든다. 근거는 spec §2.5 "Copilot 행", §5.4 "기기 코드 로그인", `docs/research/copilot-usage-api.md` §5다.

**기기 코드 흐름 규칙**
- 클라이언트 `Iv1.b507a08c87ecfe98`(Copilot GitHub App), scope `read:user`. 두 요청 모두 `POST`, `Content-Type: application/x-www-form-urlencoded`, `Accept: application/json`(없으면 GitHub가 form 형식으로 답한다), `User-Agent: AITokenUsage/1.0`.
- `StartAsync`: `POST https://github.com/login/device/code` 본문 `client_id=Iv1.b507a08c87ecfe98&scope=read%3Auser` → `device_code`, `user_code`, `verification_uri`(https가 아니거나 없으면 `https://github.com/login/device`), `expires_in`(기본 900초), `interval`(기본 5초). 200 본문의 `error`도 확인한다(`device_flow_disabled` → `Disabled`).
- `WaitForTokenAsync`: `await Task.Delay(interval, time, ct)` 후 `POST https://github.com/login/oauth/access_token` 본문 `client_id`, `device_code`, `grant_type=urn:ietf:params:oauth:grant-type:device_code`. **오류는 HTTP 200 본문으로 오므로 `error`를 먼저 본다.**
  - `authorization_pending` → 계속. `slow_down` → 간격 +5초(응답의 `interval`이 더 크면 그 값). `expired_token` → `Expired`. `access_denied` → `Denied`. `device_flow_disabled` → `Disabled`. 그 밖의 `error` → `Unknown`. `error`가 없고 `access_token`이 있으면 그 토큰을 돌려준다.
  - 시작 시각 + `ExpiresIn`을 넘기면 서버 응답 없이도 `Expired`.
  - 네트워크 실패·5xx는 연속 3회까지 다음 주기에 다시 시도하고, 3회째에 `Network`. 취소는 `OperationCanceledException` 그대로.
- 결과 토큰은 여기서 저장하지 않는다. 호출자(Task 19)가 `CopilotUsageProvider.SignInWithTokenAsync`로 검증·저장한다.

**Task 19가 이 API를 쓰는 순서** (코드는 Task 19에 있다)
1. `new GitHubDeviceFlow(host.ProviderContext.Http, host.ProviderContext.Time)`를 만든다.
2. 백그라운드에서 `StartAsync(ct)` → `UserCode`를 보여주고 [복사] 버튼을 두며, `VerificationUri`를 브라우저로 연다.
3. `WaitForTokenAsync(code, ct)` → 받은 토큰으로 `copilotProvider.SignInWithTokenAsync(token, ct)` → `host.RefreshNowAsync()`.
4. `DeviceFlowException`의 `Error == Expired`면 spec대로 `StartAsync`부터 다시 시작해 새 코드를 보여준다. 그 밖에는 `ex.UserMessage`(한국어)를 보여준다. 창을 닫으면 `ct`를 취소한다.

**Files:**
- Create: `src/AITokenUsage.Core/Providers/Copilot/DeviceCodeInfo.cs`
- Create: `src/AITokenUsage.Core/Providers/Copilot/DeviceFlowException.cs` (`DeviceFlowError` enum 포함)
- Create: `src/AITokenUsage.Core/Providers/Copilot/GitHubDeviceFlow.cs`
- Create: `src/AITokenUsage.Core/Providers/ProviderCatalog.cs`
- Create (fixtures): `tests/AITokenUsage.Core.Tests/Fixtures/copilot/` 아래 `device-code.json`, `device-code-disabled.json`, `token-pending.json`, `token-slow-down.json`, `token-success.json`, `token-expired.json`, `token-denied.json`, `token-incorrect-device-code.json`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Copilot/GitHubDeviceFlowTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/ProviderCatalogTests.cs`

**Interfaces:**
- Consumes: Task 12 `GitHubApi.UserAgent`, `CopilotUsageProvider`; Task 11 `GeminiUsageProvider`; Task 8 `Claude.ClaudeUsageProvider(ProviderContext)`; Task 10 `Codex.CodexUsageProvider(ProviderContext)`; Task 3 `Json.Str/Num`; Task 4 `ProviderDefaults.DefaultLines(string providerId)`; Task 2 `ProviderIds.All`, `ProviderContext`, `IUsageProvider`, 테스트 가짜. NuGet `Microsoft.Extensions.TimeProvider.Testing`의 `FakeTimeProvider`(`Advance`, `GetUtcNow`).
- Produces:
  - `GitHubDeviceFlow(HttpClient http, TimeProvider time)` — `Task<DeviceCodeInfo> StartAsync(CancellationToken ct)`, `Task<string> WaitForTokenAsync(DeviceCodeInfo code, CancellationToken ct)`(성공 시 access_token, 실패 시 `DeviceFlowException`), `const ClientId = "Iv1.b507a08c87ecfe98"`, `const Scope = "read:user"`, `const DeviceCodeUrl`, `const AccessTokenUrl`, `const GrantType`.
  - `sealed record DeviceCodeInfo(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval)` (`ToString()`은 `DeviceCode` 미포함).
  - `enum DeviceFlowError { Expired, Denied, Disabled, Network, Unknown }`.
  - `sealed class DeviceFlowException(DeviceFlowError error, Exception? innerException = null) : Exception` — `DeviceFlowError Error { get; }`, `string UserMessage { get; }`(한국어).
  - `static ProviderCatalog.CreateAll(ProviderContext ctx) : IReadOnlyList<IUsageProvider>` — 순서 `[ClaudeUsageProvider, CodexUsageProvider, GeminiUsageProvider, CopilotUsageProvider]`.

- [ ] **Step 1: 기기 코드 흐름 픽스처 작성**

GitHub 문서의 응답 형태다. 코드와 토큰은 가짜다.

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/device-code.json`:

```json
{
  "device_code": "test-device-code",
  "user_code": "WDJB-MJHT",
  "verification_uri": "https://github.com/login/device",
  "expires_in": 900,
  "interval": 5
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/device-code-disabled.json`:

```json
{
  "error": "device_flow_disabled",
  "error_description": "Device Flow must be explicitly enabled for this App",
  "error_uri": "https://docs.github.com/developers/apps/authorizing-oauth-apps#device-flow"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/token-pending.json`:

```json
{
  "error": "authorization_pending",
  "error_description": "The authorization request is still pending.",
  "error_uri": "https://docs.github.com/developers/apps/authorizing-oauth-apps#error-codes-for-the-device-flow"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/token-slow-down.json`:

```json
{
  "error": "slow_down",
  "error_description": "Too many requests have been made in the same timeframe.",
  "error_uri": "https://docs.github.com/developers/apps/authorizing-oauth-apps#error-codes-for-the-device-flow",
  "interval": 10
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/token-success.json`:

```json
{
  "access_token": "ghu_test_device_token",
  "token_type": "bearer",
  "scope": ""
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/token-expired.json`:

```json
{
  "error": "expired_token",
  "error_description": "The device_code has expired.",
  "error_uri": "https://docs.github.com/developers/apps/authorizing-oauth-apps#error-codes-for-the-device-flow"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/token-denied.json`:

```json
{
  "error": "access_denied",
  "error_description": "The authorization request was denied.",
  "error_uri": "https://docs.github.com/developers/apps/authorizing-oauth-apps#error-codes-for-the-device-flow"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/copilot/token-incorrect-device-code.json`:

```json
{
  "error": "incorrect_device_code",
  "error_description": "The device_code provided is not valid.",
  "error_uri": "https://docs.github.com/developers/apps/authorizing-oauth-apps#error-codes-for-the-device-flow"
}
```

- [ ] **Step 2: 실패하는 테스트 작성 — 기기 코드 흐름 (가짜 시계)**

폴링 루프는 `Task.Delay(interval, time, ct)`로 기다리므로, 테스트는 `FakeTimeProvider.Advance(1초)`와 짧은 실제 양보(`Task.Delay(1)`)를 번갈아 하며 시계를 민다(`DriveAsync`). 폴링 시각은 처리기 안에서 `GetUtcNow()`로 기록한다. 간격 검증은 하한(지연이 시작된 시각 이후)이 결정적이고, 상한은 루프가 다음 지연을 등록하기 전에 시계가 한두 칸 더 갈 수 있어 4초 여유를 둔다.

`tests/AITokenUsage.Core.Tests/Providers/Copilot/GitHubDeviceFlowTests.cs`:

```csharp
using System.Net;
using System.Text;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class GitHubDeviceFlowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly StubHttpHandler _http = new();
    private readonly List<DateTimeOffset> _pollTimes = [];
    private readonly List<string> _pollBodies = [];

    [Fact]
    public async Task Start_posts_form_with_client_and_scope_and_parses_the_code()
    {
        string? body = null, accept = null, contentType = null, userAgent = null;
        _http.When(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.DeviceCodeUrl, r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            accept = r.Headers.Accept.ToString();
            contentType = r.Content.Headers.ContentType?.MediaType;
            userAgent = r.Headers.UserAgent.ToString();
            return Reply(HttpStatusCode.OK, Fixture.Read("copilot/device-code.json"));
        });

        var code = await CreateFlow().StartAsync(CancellationToken.None);

        Assert.Equal("test-device-code", code.DeviceCode);
        Assert.Equal("WDJB-MJHT", code.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), code.VerificationUri);
        Assert.Equal(TimeSpan.FromSeconds(900), code.ExpiresIn);
        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);
        Assert.Equal("client_id=Iv1.b507a08c87ecfe98&scope=read%3Auser", body);
        Assert.Contains("application/json", accept);
        Assert.Equal("application/x-www-form-urlencoded", contentType);
        Assert.Equal(GitHubApi.UserAgent, userAgent);
        Assert.DoesNotContain("test-device-code", code.ToString());
    }

    [Fact]
    public async Task Start_reports_disabled_device_flow_from_a_200_body()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.DeviceCodeUrl,
            _ => Reply(HttpStatusCode.OK, Fixture.Read("copilot/device-code-disabled.json")));

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => CreateFlow().StartAsync(CancellationToken.None));

        Assert.Equal(DeviceFlowError.Disabled, ex.Error);
    }

    [Fact]
    public async Task Start_network_failure_is_Network()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.DeviceCodeUrl,
            _ => throw new HttpRequestException("no route to host"));

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => CreateFlow().StartAsync(CancellationToken.None));

        Assert.Equal(DeviceFlowError.Network, ex.Error);
    }

    [Fact]
    public async Task Pending_then_slow_down_then_success_returns_the_token()
    {
        GivenPollReplies("copilot/token-pending.json", "copilot/token-slow-down.json", "copilot/token-success.json");

        var token = await DriveAsync(CreateFlow().WaitForTokenAsync(Code(), CancellationToken.None));

        Assert.Equal("ghu_test_device_token", token);
        Assert.Equal(3, _pollTimes.Count);
        Assert.True(_pollTimes[0] - Start >= TimeSpan.FromSeconds(5));
        var gapAfterPending = _pollTimes[1] - _pollTimes[0];
        var gapAfterSlowDown = _pollTimes[2] - _pollTimes[1];
        Assert.InRange(gapAfterPending, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9));
        Assert.InRange(gapAfterSlowDown, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14));
        Assert.All(_pollBodies, b =>
        {
            Assert.Contains("client_id=Iv1.b507a08c87ecfe98", b);
            Assert.Contains("device_code=test-device-code", b);
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", b);
        });
    }

    [Theory]
    [InlineData("copilot/token-expired.json", DeviceFlowError.Expired)]
    [InlineData("copilot/token-denied.json", DeviceFlowError.Denied)]
    [InlineData("copilot/token-incorrect-device-code.json", DeviceFlowError.Unknown)]
    public async Task Terminal_poll_errors_throw(string fixture, DeviceFlowError expected)
    {
        GivenPollReplies("copilot/token-pending.json", fixture);

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(
            () => DriveAsync(CreateFlow().WaitForTokenAsync(Code(), CancellationToken.None)));

        Assert.Equal(expected, ex.Error);
        Assert.Equal(2, _pollTimes.Count);
        Assert.False(string.IsNullOrWhiteSpace(ex.UserMessage));
    }

    [Fact]
    public async Task Local_deadline_expires_while_still_pending()
    {
        GivenPollReplies("copilot/token-pending.json");

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(
            () => DriveAsync(CreateFlow().WaitForTokenAsync(Code(expiresSeconds: 12), CancellationToken.None)));

        Assert.Equal(DeviceFlowError.Expired, ex.Error);
        Assert.Equal(2, _pollTimes.Count); // polls at ~5 s and ~10 s, deadline hit before ~15 s
    }

    [Fact]
    public async Task Three_consecutive_network_failures_throw_Network()
    {
        var attempts = 0;
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.AccessTokenUrl, _ =>
        {
            attempts++;
            throw new HttpRequestException("connection reset");
        });

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(
            () => DriveAsync(CreateFlow().WaitForTokenAsync(Code(), CancellationToken.None)));

        Assert.Equal(DeviceFlowError.Network, ex.Error);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Cancellation_stops_polling()
    {
        GivenPollReplies("copilot/token-pending.json");
        using var cts = new CancellationTokenSource();
        var task = CreateFlow().WaitForTokenAsync(Code(), cts.Token);

        for (var i = 0; i < 200 && _pollTimes.Count == 0; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(_pollTimes);
    }

    private GitHubDeviceFlow CreateFlow() => new(new HttpClient(_http), _time);

    private static DeviceCodeInfo Code(int expiresSeconds = 900, int intervalSeconds = 5) =>
        new("test-device-code", "WDJB-MJHT", new Uri("https://github.com/login/device"),
            TimeSpan.FromSeconds(expiresSeconds), TimeSpan.FromSeconds(intervalSeconds));

    /// <summary>Each poll takes the next fixture; the last one repeats.</summary>
    private void GivenPollReplies(params string[] fixtures)
    {
        var queue = new Queue<string>(fixtures);
        _http.When(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.AccessTokenUrl, r =>
        {
            _pollTimes.Add(_time.GetUtcNow());
            _pollBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var fixture = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Reply(HttpStatusCode.OK, Fixture.Read(fixture));
        });
    }

    /// <summary>Moves the fake clock one second at a time, yielding so the polling loop can register its next delay.</summary>
    private async Task<T> DriveAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        return await task;
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
```

- [ ] **Step 3: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GitHubDeviceFlowTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'GitHubDeviceFlow' could not be found`, `'DeviceCodeInfo' could not be found`, `'DeviceFlowException' could not be found`, `error CS0103: The name 'DeviceFlowError' does not exist in the current context`.

- [ ] **Step 4: 구현 — 기기 코드 형식, 예외, 흐름**

`src/AITokenUsage.Core/Providers/Copilot/DeviceCodeInfo.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>Result of POST /login/device/code. Show <see cref="UserCode"/> and open <see cref="VerificationUri"/>.</summary>
public sealed record DeviceCodeInfo(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval)
{
    // DeviceCode is a bearer-like secret for the polling step; keep it out of logs.
    public override string ToString() => $"DeviceCodeInfo {{ UserCode = {UserCode}, VerificationUri = {VerificationUri} }}";
}
```

`src/AITokenUsage.Core/Providers/Copilot/DeviceFlowException.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Copilot;

public enum DeviceFlowError { Expired, Denied, Disabled, Network, Unknown }

public sealed class DeviceFlowException(DeviceFlowError error, Exception? innerException = null)
    : Exception($"GitHub device flow failed: {error}", innerException)
{
    public DeviceFlowError Error { get; } = error;

    /// <summary>Korean text for the settings window.</summary>
    public string UserMessage => Error switch
    {
        DeviceFlowError.Expired => "코드가 만료되었습니다. 다시 시도하세요",
        DeviceFlowError.Denied => "GitHub에서 로그인을 취소했습니다",
        DeviceFlowError.Disabled => "이 앱의 GitHub 기기 코드 로그인이 비활성화되어 있습니다",
        DeviceFlowError.Network => "네트워크 오류 — 잠시 후 다시 시도",
        _ => "GitHub 로그인 중 알 수 없는 오류가 발생했습니다",
    };
}
```

`src/AITokenUsage.Core/Providers/Copilot/GitHubDeviceFlow.cs`:

```csharp
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
```

- [ ] **Step 5: 테스트 실행 → 통과 확인, 타이밍 안정성 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~GitHubDeviceFlowTests"`

Expected: `Passed!  - Failed:     0, Passed:    10`

가짜 시계 테스트가 흔들리지 않는지 5번 반복한다.

Run (PowerShell): `1..5 | ForEach-Object { dotnet test tests/AITokenUsage.Core.Tests --no-build --filter "FullyQualifiedName~GitHubDeviceFlowTests" }`

Expected: 다섯 번 모두 `Passed!  - Failed:     0, Passed:    10`.

- [ ] **Step 6: 실패하는 테스트 작성 — 공급자 카탈로그**

순서가 `ProviderIds.All`과 같은지, 그리고 각 공급자의 `StaticMeterOptions`에 `ProviderDefaults.DefaultLines`의 1줄·2줄 키가 들어 있는지 확인한다. 기본 설정이 드롭다운에 없는 키를 가리키는 일을 막는 통합 확인이라서, Claude(Task 8)와 Codex(Task 10)의 미터 키도 함께 검증된다.

`tests/AITokenUsage.Core.Tests/Providers/ProviderCatalogTests.cs`:

```csharp
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers;

public sealed class ProviderCatalogTests
{
    [Fact]
    public void Creates_all_providers_in_spec_order()
    {
        var providers = Create();

        Assert.Equal(ProviderIds.All.ToArray(), providers.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Copilot_entry_exposes_sign_in_members()
    {
        var copilot = Assert.IsType<CopilotUsageProvider>(Create().Single(p => p.Id == ProviderIds.Copilot));

        Assert.Null(copilot.SignedInLogin);
    }

    [Fact]
    public void Every_provider_offers_its_default_line_keys()
    {
        foreach (var provider in Create())
        {
            var (line1, line2) = ProviderDefaults.DefaultLines(provider.Id);
            var keys = provider.StaticMeterOptions.Select(o => o.Key).ToList();

            Assert.Contains(line1, keys);
            Assert.Contains(line2, keys);
        }
    }

    [Fact]
    public void Every_provider_has_a_name_and_a_minimum_interval()
    {
        foreach (var provider in Create())
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName));
            Assert.True(provider.MinInterval >= TimeSpan.FromMinutes(1), provider.Id);
            Assert.NotNull(provider.WatchPaths);
        }
    }

    private static IReadOnlyList<IUsageProvider> Create() =>
        ProviderCatalog.CreateAll(TestContexts.Create(new StubHttpHandler(), new FakeTimeProvider()));
}
```

- [ ] **Step 7: 테스트 실행 → 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ProviderCatalogTests"`

Expected: 빌드 실패. `error CS0103: The name 'ProviderCatalog' does not exist in the current context`

- [ ] **Step 8: 구현 — `ProviderCatalog`**

`src/AITokenUsage.Core/Providers/ProviderCatalog.cs`:

```csharp
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Providers.Gemini;

namespace AITokenUsage.Core.Providers;

public static class ProviderCatalog
{
    /// <summary>All providers in default display order: claude, codex, gemini, copilot.</summary>
    public static IReadOnlyList<IUsageProvider> CreateAll(ProviderContext ctx) =>
    [
        new ClaudeUsageProvider(ctx),
        new CodexUsageProvider(ctx),
        new GeminiUsageProvider(ctx),
        new CopilotUsageProvider(ctx),
    ];
}
```

- [ ] **Step 9: 테스트 실행 → 통과 확인, 전체 테스트**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ProviderCatalogTests"`

Expected: `Passed!  - Failed:     0, Passed:     4`. 실패하면 메시지에 나온 공급자의 `StaticMeterOptions`가 Task 4 `ProviderDefaults`의 기본 키(claude `session`/`weekly`, codex `5h`/`weekly`, gemini `pro`/`flash`, copilot `monthly`/`reset`)를 포함하도록 해당 Task의 코드를 고친다.

Run: `dotnet test AITokenUsage.sln`

Expected: `Failed:     0`, 빌드 `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 10: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Copilot/DeviceCodeInfo.cs src/AITokenUsage.Core/Providers/Copilot/DeviceFlowException.cs src/AITokenUsage.Core/Providers/Copilot/GitHubDeviceFlow.cs src/AITokenUsage.Core/Providers/ProviderCatalog.cs tests/AITokenUsage.Core.Tests/Providers/Copilot/GitHubDeviceFlowTests.cs tests/AITokenUsage.Core.Tests/Providers/ProviderCatalogTests.cs tests/AITokenUsage.Core.Tests/Fixtures/copilot
git commit -m "feat(core): GitHub 기기 코드 로그인과 공급자 카탈로그 추가" -m "Copilot 앱 클라이언트로 기기 코드를 받아 interval마다 폴링하고(slow_down이면 +5초), 네 공급자를 기본 순서로 만드는 ProviderCatalog를 추가한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
