## Phase 3 — 공급자

> 이 파트는 Phase 3 중 **Codex 공급자(Task 9, 10)** 만 다룬다. 근거는 스펙 §5.2와 `docs/research/codex-usage-api.md` 전체이며, API URL·헤더·JSON 필드명·경로는 그 보고서를 그대로 따른다.
> 이 PC에는 Codex가 없으므로(스펙 §1.3, §7.2) 모든 동작은 보고서 형태 기반 샘플로 단위 테스트한다. 실제 계정 연결은 확인하지 못한다.

### Task 9: Codex 1 — 경로·auth.json·창 라벨·wham/usage 파서

**목표:** `CODEX_HOME`과 `config.toml`로 사용량 URL을 정하고, `auth.json`을 공유 모드로 안전하게 읽어 로그인 방식·계정 ID·토큰 신선도를 판정하고, 창 길이(±5%)로 라벨을 붙여 `wham/usage` 응답을 `UsageMeter` 목록으로 바꾼다. 요청 헤더 규칙(`chatgpt-account-id`, FedRAMP일 때만 `X-OpenAI-Fedramp`, luna-reserve 헤더 금지)을 테스트로 고정한다.

**Files:**
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexPaths.cs`
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexAuth.cs`
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexAuthReader.cs`
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexWindowLabeler.cs`
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexUsageParser.cs`
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexUsageClient.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/auth-chatgpt.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/auth-legacy.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-plus.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-pro-spark.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-free.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-primary-weekly.json`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexTestData.cs` (테스트 도우미)
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexPathsTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexAuthReaderTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexWindowLabelerTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexUsageParserTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexUsageClientTests.cs`
- Modify(없을 때만): `tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj` — Fixtures 출력 복사 항목(Step 1)

**Interfaces:**
- Consumes
  - Task 2: `IEnvironment.GetVariable(string)`, `IEnvironment.UserProfile`, `IFileSystem.ReadAllTextShared(string)`, `UsageMeter(...)`
  - Task 3: `Jwt.TryReadPayload(string?, out JsonElement)`, `Jwt.GetExpiry(string?)`, `Json.Str/Num/Bool/Obj/Arr(JsonElement, string)`
  - 테스트(Task 2): `FakeEnvironment()`·`Set(name, value?)`, `FakeFileSystem()`·`AddFile(path, content, DateTime?)`, `StubHttpHandler()`·`When(...)`, `Fixture.Read(string)`, `FakeTimeProvider`
- Produces (모두 `namespace AITokenUsage.Core.Providers.Codex;`)

```csharp
public sealed class CodexPaths
{
    public const string DefaultBaseUrl = "https://chatgpt.com/backend-api/";
    public CodexPaths(string home);
    public string Home { get; }
    public string AuthFile { get; }            // <home>\auth.json
    public string ConfigFile { get; }          // <home>\config.toml
    public string SessionsDirectory { get; }   // <home>\sessions
    public static CodexPaths Resolve(IEnvironment env);               // CODEX_HOME → %USERPROFILE%\.codex
    public static string? ReadConfiguredBaseUrl(string? configToml);  // 최상위 chatgpt_base_url만
    public static string NormalizeBaseUrl(string? configured);        // 끝 '/' 제거, chatgpt.com에 /backend-api 보충
    public static string BuildUsageUrl(string normalizedBaseUrl);     // /wham/usage 또는 /api/codex/usage
    public string ResolveUsageUrl(IFileSystem files);
}

public enum CodexAuthMode { ChatGpt, ApiKey, Other }

public sealed record CodexAuth(CodexAuthMode Mode, string? AccessToken, string? AccountId, bool IsFedramp,
    string? PlanTypeClaim, DateTimeOffset? AccessTokenExpiresAt, DateTimeOffset? LastRefresh)
{
    public static readonly TimeSpan ExpiryMargin;       // 5분
    public static readonly TimeSpan LastRefreshMaxAge;  // 8일
    public bool HasAccessToken { get; }
    public bool IsFresh(DateTimeOffset now);
    // ToString()은 토큰을 출력하지 않는다
}

public enum CodexAuthReadStatus { Ok, Missing, Unreadable }
public sealed record CodexAuthReadResult(CodexAuthReadStatus Status, CodexAuth? Auth);

public sealed class CodexAuthReader
{
    public const string AuthClaim = "https://api.openai.com/auth";
    public CodexAuthReader(IFileSystem files, Func<TimeSpan, CancellationToken, Task>? delay = null);
    public Task<CodexAuthReadResult> ReadAsync(string authFilePath, CancellationToken ct);
    public static CodexAuth? TryParse(string json);
    public static DateTimeOffset? ParseTimestamp(string? value);
}

public sealed record CodexWindow(double? UsedPercent, long? WindowMinutes, DateTimeOffset? ResetsAt);
public sealed record CodexWindowLabel(string Key, string ShortLabel, string LongLabel);

public static class CodexWindowLabeler
{
    public static readonly CodexWindowLabel FiveHour, Daily, Weekly, Monthly, Annual, Primary, Secondary;
    public static long? MinutesFromSeconds(double? seconds);
    public static CodexWindowLabel Label(long? minutes, bool isSecondary);
    public static IReadOnlyList<UsageMeter> ToMeters(CodexWindow? primary, CodexWindow? secondary,
        string? featureKey = null, string? featureName = null);
    public static string Abbreviate(string name);
}

public sealed record CodexUsageResult(string? PlanType, IReadOnlyList<UsageMeter> Meters);

public static class CodexUsageParser
{
    public static CodexUsageResult ParseWham(string json, DateTimeOffset now); // 객체가 아니면 JsonException
    public static string? FormatPlan(string? planType);                       // "pro" → "Pro"
}

public enum CodexHttpOutcome { Ok, Unauthorized, RateLimited, Failed }
public sealed record CodexHttpResult(CodexHttpOutcome Outcome, int? StatusCode, string? Body, TimeSpan? RetryAfter);

public sealed class CodexUsageClient
{
    public static readonly string UserAgent; // "AITokenUsage/<ver>"
    public CodexUsageClient(HttpClient http, TimeProvider time);
    public static HttpRequestMessage BuildRequest(string usageUrl, CodexAuth auth);
    public Task<CodexHttpResult> GetUsageAsync(string usageUrl, CodexAuth auth, CancellationToken ct);
}
```

**설계 메모 (이 Task의 규칙 요약)**
- **경로**: `CODEX_HOME`이 정의돼 있고 공백이 아니면 그 값, 아니면 `%USERPROFILE%\.codex`. `config.toml`은 정규식으로 **최상위** `chatgpt_base_url = "..."`(또는 `'...'`)만 읽는다. 첫 `[테이블]` 헤더 이후 키(프로필)는 무시한다.
- **기준 URL 정규화**: 기본 `https://chatgpt.com/backend-api/` → 끝 `/` 제거 → `https://chatgpt.com`·`https://chat.openai.com`으로 시작하는데 `/backend-api`가 없으면 붙인다. 사용량 URL은 base에 `/backend-api`가 있으면 `{base}/wham/usage`, 없으면 `{base}/api/codex/usage`.
- **로그인 방식**
  - `auth_mode`가 있으면: `apikey`·`bedrockApiKey`·`bedrockAccessKeys` → `ApiKey`(플랜 한도 없음), `chatgpt`·`chatgptAuthTokens` → `ChatGpt`, 나머지(`personalAccessToken`, `agentIdentity`, `headers`, 미지의 값) → `Other`(app-server로만 조회).
  - `auth_mode`가 없으면: `tokens.access_token`이 있으면 `ChatGpt`(보고서: ChatGPT 로그인도 `OPENAI_API_KEY`를 저장하므로 토큰이 우선) → `personal_access_token` → `Other` → `bedrock_*` → `ApiKey` → `OPENAI_API_KEY`가 문자열 → `ApiKey` → 그 외 `ChatGpt`.
- **계정 ID**: `tokens.account_id` → id_token, access_token 순으로 JWT의 최상위 `chatgpt_account_id` → `https://api.openai.com/auth`.`chatgpt_account_id` → `organizations[]` 중 첫 번째 비어 있지 않은 `id`.
- **FedRAMP**: id_token의 `https://api.openai.com/auth`.`chatgpt_account_is_fedramp == true`일 때만.
- **신선도**: access_token `exp > now + 5분`. `exp`를 모르면 `now − last_refresh < 8일`. 둘 다 없으면 오래된 것으로 본다.
- **읽기 재시도**: 파일이 없으면 즉시 `Missing`. JSON 파싱 실패·IO 예외는 50/100/200ms 간격으로 3회 다시 읽고(총 4회) 그래도 실패하면 `Unreadable`(**로그아웃으로 취급하지 않음**). 지연은 ms 단위라 실제 시간(`Task.Delay`)을 쓴다. `TimeProvider`로 기다리면 `FakeTimeProvider` 테스트가 멈추기 때문이다. 테스트에서는 `delay` 인자로 대체한다.
- **창 라벨**: 정수 연산 `95e ≤ 100m ≤ 105e`로 ±5%를 판정한다.

  | 분(±5%) | 키 | 위젯 짧은 라벨 | 카드 긴 라벨 |
  |---|---|---|---|
  | 300 | `5h` | `5h` | 5시간 한도 |
  | 1440 | `daily` | `일` | 일간 한도 |
  | 10080 | `weekly` | `주` | 주간 한도 |
  | 43200 | `monthly` | `월` | 월간 한도 |
  | 525600 | `annual` | `년` | 연간 한도 |
  | 그 외·없음(primary) | `primary` | `한도` | 사용 한도 |
  | 그 외·없음(secondary) | `secondary` | `보조` | 보조 한도 |

- **미터 순서**: primary/secondary 위치가 아니라 **창 길이 오름차순**(모르는 길이는 뒤). primary가 주간이고 secondary가 5시간이면 `[5h, weekly]`가 된다. 같은 키가 겹치면 뒤쪽 창은 `primary`/`secondary` 키로 바꾼다.
- **추가 한도**(`additional_rate_limits`): 키 `<metered_feature>:<키>`, 짧은 라벨 `<짧은 라벨>·<이름 약어 2글자>`(예: `주·Sp`), 긴 라벨 `<limit_name> · <긴 라벨>`. `metered_feature`가 없거나 `rate_limit`이 null인 항목, 객체가 아닌 항목은 그 항목만 건너뛴다.
- **리셋 시각**: `reset_at`(epoch 초)을 쓰고, 없으면 `now + reset_after_seconds`.
- **플랜 라벨**: `plan_type` 원문의 첫 글자만 대문자(`pro` → `Pro`). 빈 값과 `unknown`은 null.
- **요청 헤더**: `Authorization: Bearer <access_token>`, `chatgpt-account-id`(알 때만), `User-Agent: AITokenUsage/<ver>`, `Accept: application/json`, FedRAMP일 때만 `X-OpenAI-Fedramp: true`. `x-openai-codex-luna-reserve`와 `originator`는 **보내지 않는다**. 토큰 갱신(`/oauth/token`)은 하지 않는다.
- **비밀 보호**: `CodexAuth`·`CodexHttpResult`의 `ToString()`은 토큰·본문을 출력하지 않는다. 로그에는 예외 형식 이름만 남긴다.

- [ ] **Step 1: 사전 확인 — Fixtures 복사 설정**

`tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj`에 Task 2가 넣은 Fixtures 복사 항목이 있는지 확인한다.

```powershell
Select-String -Path tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj -Pattern 'Fixtures'
```

Expected: `Fixtures\**` 를 `CopyToOutputDirectory`로 복사하는 줄이 1개 이상 보인다. 없으면 csproj의 `</Project>` 바로 앞에 아래 항목을 추가한다.

```xml
  <ItemGroup>
    <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: 테스트 도우미와 Fixtures 작성**

모든 토큰은 가짜다. JWT는 `base64url(헤더).base64url(페이로드).test-signature` 형태로 직접 만든 서명 없는 값이고, 기준 시각은 `2026-10-28T03:00:00Z`(= epoch `1793156400`)이다.

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexTestData.cs`

```csharp
using System.Text;
using System.Text.Json.Nodes;

namespace AITokenUsage.Core.Tests.Providers.Codex;

/// <summary>Fake Codex credentials and paths for tests. Nothing here is a real secret.</summary>
internal static class CodexTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    public static readonly DateTime FileTime = new(2026, 10, 28, 2, 0, 0, DateTimeKind.Utc);

    public const string Home = @"C:\Users\test\.codex";
    public const string AuthPath = @"C:\Users\test\.codex\auth.json";
    public const string ConfigPath = @"C:\Users\test\.codex\config.toml";
    public const string SessionsDir = @"C:\Users\test\.codex\sessions";
    public const string DefaultUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public const string AccountId = "acct-test-0001";

    /// <summary>Unsigned fake JWT: base64url(header).base64url(payload).test-signature</summary>
    public static string Jwt(string payloadJson)
        => Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}") + "." + Base64Url(payloadJson) + ".test-signature";

    public static string AccessJwt(DateTimeOffset expiresAt)
        => Jwt($$"""{"exp":{{expiresAt.ToUnixTimeSeconds()}},"iat":{{expiresAt.AddHours(-1).ToUnixTimeSeconds()}},"scp":["openid"]}""");

    public static string IdJwt(string? accountId = AccountId, bool fedramp = false, string plan = "plus")
    {
        var auth = new JsonObject
        {
            ["chatgpt_plan_type"] = plan,
            ["chatgpt_user_id"] = "user-test-0001",
            ["chatgpt_account_is_fedramp"] = fedramp,
        };
        if (accountId is not null) auth["chatgpt_account_id"] = accountId;
        var payload = new JsonObject
        {
            ["email"] = "dev@example.com",
            ["https://api.openai.com/auth"] = auth,
            ["exp"] = Now.AddHours(1).ToUnixTimeSeconds(),
        };
        return Jwt(payload.ToJsonString());
    }

    /// <summary>auth.json in the shape Codex writes (research §1.3).</summary>
    public static string AuthJson(
        string? authMode = "chatgpt",
        string? accessToken = null,
        string? idToken = null,
        string? accountId = AccountId,
        string? lastRefresh = "2026-10-28T02:00:00.123456789Z",
        string? openAiApiKey = null,
        bool includeTokens = true)
    {
        var root = new JsonObject();
        if (authMode is not null) root["auth_mode"] = authMode;
        root["OPENAI_API_KEY"] = openAiApiKey;
        if (includeTokens)
        {
            root["tokens"] = new JsonObject
            {
                ["id_token"] = idToken ?? IdJwt(),
                ["access_token"] = accessToken ?? AccessJwt(Now.AddHours(1)),
                ["refresh_token"] = "test-refresh-token",
                ["account_id"] = accountId,
            };
        }
        if (lastRefresh is not null) root["last_refresh"] = lastRefresh;
        return root.ToJsonString();
    }

    private static string Base64Url(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/auth-chatgpt.json` — 현재 형식. `auth_mode`가 `chatgpt`인데 토큰 교환으로 얻은 `OPENAI_API_KEY`도 들어 있다. id_token 페이로드는 `{"email":"dev@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"acct-fixture-0001","chatgpt_plan_type":"pro","chatgpt_user_id":"user-fixture-0001","chatgpt_account_is_fedramp":false},"exp":1793160000,"iat":1793156400}`, access_token 페이로드는 `{"exp":1793170800,"iat":1793156400,"https://api.openai.com/auth":{"chatgpt_account_id":"acct-fixture-0001"}}`이다.

```json
{
  "auth_mode": "chatgpt",
  "OPENAI_API_KEY": "sk-test-exchanged-key",
  "tokens": {
    "id_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJlbWFpbCI6ImRldkBleGFtcGxlLmNvbSIsImh0dHBzOi8vYXBpLm9wZW5haS5jb20vYXV0aCI6eyJjaGF0Z3B0X2FjY291bnRfaWQiOiJhY2N0LWZpeHR1cmUtMDAwMSIsImNoYXRncHRfcGxhbl90eXBlIjoicHJvIiwiY2hhdGdwdF91c2VyX2lkIjoidXNlci1maXh0dXJlLTAwMDEiLCJjaGF0Z3B0X2FjY291bnRfaXNfZmVkcmFtcCI6ZmFsc2V9LCJleHAiOjE3OTMxNjAwMDAsImlhdCI6MTc5MzE1NjQwMH0.test-signature",
    "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE3OTMxNzA4MDAsImlhdCI6MTc5MzE1NjQwMCwiaHR0cHM6Ly9hcGkub3BlbmFpLmNvbS9hdXRoIjp7ImNoYXRncHRfYWNjb3VudF9pZCI6ImFjY3QtZml4dHVyZS0wMDAxIn19.test-signature",
    "refresh_token": "test-refresh-token",
    "oauth_provenance": { "issuer": "https://auth.openai.com", "client_id": "app_test_client" },
    "login_owner_id": "owner-test-0001",
    "account_id": "acct-fixture-0001"
  },
  "last_refresh": "2026-10-28T02:59:00.123456789Z"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/auth-legacy.json` — 옛 형식. `auth_mode`가 없고 `account_id`가 null이며 access_token이 JWT가 아니다(`exp` 없음, `last_refresh` 3일 전).

```json
{
  "OPENAI_API_KEY": "sk-test-exchanged-key",
  "tokens": {
    "id_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJlbWFpbCI6ImRldkBleGFtcGxlLmNvbSIsImh0dHBzOi8vYXBpLm9wZW5haS5jb20vYXV0aCI6eyJjaGF0Z3B0X2FjY291bnRfaWQiOiJhY2N0LWZpeHR1cmUtMDAwMSIsImNoYXRncHRfcGxhbl90eXBlIjoicHJvIiwiY2hhdGdwdF91c2VyX2lkIjoidXNlci1maXh0dXJlLTAwMDEiLCJjaGF0Z3B0X2FjY291bnRfaXNfZmVkcmFtcCI6ZmFsc2V9LCJleHAiOjE3OTMxNjAwMDAsImlhdCI6MTc5MzE1NjQwMH0.test-signature",
    "access_token": "test-opaque-access-token",
    "refresh_token": "test-refresh-token",
    "account_id": null
  },
  "last_refresh": "2026-10-25T03:00:00Z"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-plus.json` — Plus, 5시간(18000초) + 주간(604800초).

```json
{
  "plan_type": "plus",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": { "used_percent": 42, "limit_window_seconds": 18000, "reset_after_seconds": 7200, "reset_at": 1793163600 },
    "secondary_window": { "used_percent": 5, "limit_window_seconds": 604800, "reset_after_seconds": 259200, "reset_at": 1793415600 }
  },
  "credits": { "has_credits": false, "unlimited": false, "balance": "0", "approx_local_messages": [], "approx_cloud_messages": [] },
  "spend_control": null,
  "additional_rate_limits": null,
  "rate_limit_reached_type": null,
  "rate_limit_reset_credits": { "available_count": 0 },
  "account_id": "acct-test-0001",
  "user_id": "user-test-0001",
  "rate_limit_upsell": {}
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-pro-spark.json` — Pro + `additional_rate_limits`(Spark 정상 항목, `rate_limit: null` 항목, `metered_feature` 없는 항목, 숫자 항목).

```json
{
  "plan_type": "pro",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": { "used_percent": 18, "limit_window_seconds": 18000, "reset_after_seconds": 7200, "reset_at": 1793163600 },
    "secondary_window": { "used_percent": 33.5, "limit_window_seconds": 604800, "reset_after_seconds": 259200, "reset_at": 1793415600 }
  },
  "credits": { "has_credits": true, "unlimited": false, "balance": "150.0", "approx_local_messages": [], "approx_cloud_messages": [] },
  "spend_control": {
    "reached": false,
    "individual_limit": { "source": "workspace_spend_controls", "limit": "25000", "used": "8000", "remaining": "17000",
                          "used_percent": 32, "remaining_percent": 68, "reset_after_seconds": 43200, "reset_at": 1793415600 }
  },
  "additional_rate_limits": [
    { "limit_name": "GPT-5.3-Codex-Spark", "metered_feature": "gpt_5_3_codex_spark", "normal_model_slug": null,
      "rate_limit": { "allowed": true, "limit_reached": false,
        "primary_window":   { "used_percent": 30,  "limit_window_seconds": 18000,  "reset_after_seconds": 0, "reset_at": 1793163600 },
        "secondary_window": { "used_percent": 100, "limit_window_seconds": 604800, "reset_after_seconds": 0, "reset_at": 1793761200 } } },
    { "limit_name": "Broken entry", "metered_feature": "broken_feature", "rate_limit": null },
    { "limit_name": "No feature", "rate_limit": { "allowed": true, "limit_reached": false,
        "primary_window": { "used_percent": 1, "limit_window_seconds": 18000, "reset_at": 1793163600 } } },
    42
  ],
  "rate_limit_reached_type": null,
  "rate_limit_reset_credits": { "available_count": 3 },
  "account_id": "acct-test-0001",
  "user_id": "user-test-0001"
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-free.json` — Free, PRIMARY에 30일(2592000초) 창 하나만.

```json
{
  "plan_type": "free",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": { "used_percent": 11, "reset_at": 1795748400, "limit_window_seconds": 2592000 },
    "secondary_window": null
  },
  "credits": null,
  "additional_rate_limits": null
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/wham-primary-weekly.json` — primary가 주간, secondary가 5시간인 경우.

```json
{
  "plan_type": "team",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": { "used_percent": 61, "limit_window_seconds": 604800, "reset_after_seconds": 259200, "reset_at": 1793415600 },
    "secondary_window": { "used_percent": 12, "limit_window_seconds": 18000, "reset_after_seconds": 7200, "reset_at": 1793163600 }
  },
  "credits": null,
  "additional_rate_limits": []
}
```

- [ ] **Step 3: `CodexPaths` 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexPathsTests.cs`

```csharp
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexPathsTests
{
    [Fact]
    public void Resolve_UsesCodexHome_WhenDefined()
    {
        var env = new FakeEnvironment();
        env.Set("CODEX_HOME", @"D:\codex-home");

        var paths = CodexPaths.Resolve(env);

        Assert.Equal(@"D:\codex-home", paths.Home);
        Assert.Equal(@"D:\codex-home\auth.json", paths.AuthFile);
        Assert.Equal(@"D:\codex-home\config.toml", paths.ConfigFile);
        Assert.Equal(@"D:\codex-home\sessions", paths.SessionsDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToUserProfile_WhenCodexHomeMissingOrBlank(string? codexHome)
    {
        var env = new FakeEnvironment();
        env.Set("CODEX_HOME", codexHome);

        var paths = CodexPaths.Resolve(env);

        Assert.Equal(CodexTestData.Home, paths.Home);
        Assert.Equal(CodexTestData.AuthPath, paths.AuthFile);
    }

    [Theory]
    [InlineData("chatgpt_base_url = \"https://proxy.example.com/backend-api/\"", "https://proxy.example.com/backend-api/")]
    [InlineData("  chatgpt_base_url='http://localhost:8080'  # local", "http://localhost:8080")]
    [InlineData("model = \"gpt-5-codex\"\nchatgpt_base_url = \"https://chatgpt.com\"\n[profiles.work]\nmodel = \"o3\"", "https://chatgpt.com")]
    [InlineData("# chatgpt_base_url = \"https://commented.example.com\"", null)]
    [InlineData("[profiles.work]\nchatgpt_base_url = \"https://profile.example.com\"", null)]
    [InlineData("chatgpt_base_url = \"\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadConfiguredBaseUrl_ReadsOnlyTopLevelKey(string? toml, string? expected)
    {
        Assert.Equal(expected, CodexPaths.ReadConfiguredBaseUrl(toml?.Replace("\n", "\r\n")));
    }

    [Theory]
    [InlineData(null, "https://chatgpt.com/backend-api")]
    [InlineData("   ", "https://chatgpt.com/backend-api")]
    [InlineData("https://chatgpt.com", "https://chatgpt.com/backend-api")]
    [InlineData("https://chatgpt.com/", "https://chatgpt.com/backend-api")]
    [InlineData("https://chat.openai.com", "https://chat.openai.com/backend-api")]
    [InlineData("https://chatgpt.com/backend-api/", "https://chatgpt.com/backend-api")]
    [InlineData("https://proxy.example.com/backend-api/", "https://proxy.example.com/backend-api")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    public void NormalizeBaseUrl_FollowsCodexRules(string? configured, string expected)
    {
        Assert.Equal(expected, CodexPaths.NormalizeBaseUrl(configured));
    }

    [Theory]
    [InlineData("https://chatgpt.com/backend-api", "https://chatgpt.com/backend-api/wham/usage")]
    [InlineData("https://proxy.example.com/backend-api", "https://proxy.example.com/backend-api/wham/usage")]
    [InlineData("http://localhost:8080", "http://localhost:8080/api/codex/usage")]
    public void BuildUsageUrl_PicksWhamOnlyForBackendApi(string normalizedBase, string expected)
    {
        Assert.Equal(expected, CodexPaths.BuildUsageUrl(normalizedBase));
    }

    [Fact]
    public void ResolveUsageUrl_UsesDefault_WhenConfigMissing()
    {
        var paths = CodexPaths.Resolve(new FakeEnvironment());

        Assert.Equal(CodexTestData.DefaultUsageUrl, paths.ResolveUsageUrl(new FakeFileSystem()));
    }

    [Fact]
    public void ResolveUsageUrl_ReadsConfigToml()
    {
        var files = new FakeFileSystem();
        files.AddFile(CodexTestData.ConfigPath, "chatgpt_base_url = \"http://localhost:8080/\"\n", CodexTestData.FileTime);
        var paths = CodexPaths.Resolve(new FakeEnvironment());

        Assert.Equal("http://localhost:8080/api/codex/usage", paths.ResolveUsageUrl(files));
    }
}
```

- [ ] **Step 4: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexPathsTests"`

Expected: 빌드 실패. `error CS0234: The type or namespace name 'Codex' does not exist in the namespace 'AITokenUsage.Core.Providers'`

- [ ] **Step 5: `CodexPaths` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexPaths.cs`

```csharp
using System.Text.RegularExpressions;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>Codex CLI file locations (CODEX_HOME) and the ChatGPT backend usage URL.</summary>
public sealed class CodexPaths
{
    public const string DefaultBaseUrl = "https://chatgpt.com/backend-api/";

    // Top-level `chatgpt_base_url = "..."` or `chatgpt_base_url = '...'` (TOML basic or literal string).
    private static readonly Regex BaseUrlLine = new(
        @"^\s*chatgpt_base_url\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
        RegexOptions.CultureInvariant);

    public CodexPaths(string home)
    {
        Home = home;
    }

    public string Home { get; }
    public string AuthFile => Path.Combine(Home, "auth.json");
    public string ConfigFile => Path.Combine(Home, "config.toml");
    public string SessionsDirectory => Path.Combine(Home, "sessions");

    /// <summary>CODEX_HOME when defined and non-blank, otherwise %USERPROFILE%\.codex.</summary>
    public static CodexPaths Resolve(IEnvironment env)
    {
        var configured = env.GetVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
            return new CodexPaths(configured.Trim());
        return new CodexPaths(Path.Combine(env.UserProfile, ".codex"));
    }

    /// <summary>Reads a top-level chatgpt_base_url. Keys inside [tables] (profiles) are ignored.</summary>
    public static string? ReadConfiguredBaseUrl(string? configToml)
    {
        if (string.IsNullOrEmpty(configToml)) return null;
        foreach (var rawLine in configToml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith('['))
                return null;
            var match = BaseUrlLine.Match(line);
            if (match.Success)
            {
                var value = match.Groups["v"].Value.Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    /// <summary>Strips trailing '/', and appends /backend-api to chatgpt.com hosts that lack it.</summary>
    public static string NormalizeBaseUrl(string? configured)
    {
        var url = string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.Trim();
        url = url.TrimEnd('/');
        var isChatGptHost = url.StartsWith("https://chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://chat.openai.com", StringComparison.OrdinalIgnoreCase);
        if (isChatGptHost && !url.Contains("/backend-api", StringComparison.OrdinalIgnoreCase))
            url += "/backend-api";
        return url;
    }

    /// <summary>{base}/wham/usage when the base contains /backend-api, otherwise {base}/api/codex/usage.</summary>
    public static string BuildUsageUrl(string normalizedBaseUrl)
        => normalizedBaseUrl.Contains("/backend-api", StringComparison.OrdinalIgnoreCase)
            ? normalizedBaseUrl + "/wham/usage"
            : normalizedBaseUrl + "/api/codex/usage";

    /// <summary>Reads config.toml (shared read) and returns the usage URL. Unreadable config means default.</summary>
    public string ResolveUsageUrl(IFileSystem files)
    {
        string? toml = null;
        try
        {
            toml = files.ReadAllTextShared(ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            toml = null;
        }
        return BuildUsageUrl(NormalizeBaseUrl(ReadConfiguredBaseUrl(toml)));
    }
}
```

- [ ] **Step 6: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexPathsTests"`

Expected: `Passed!  - Failed: 0, Passed: 25`

- [ ] **Step 7: `CodexAuthReader` 실패하는 테스트 작성**

`SequenceFileSystem`은 읽을 때마다 큐의 다음 내용(또는 예외)을 돌려주는 테스트 전용 `IFileSystem`이다. 부분 기록된 파일 → 정상 파일 순서를 재현한다.

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexAuthReaderTests.cs`

```csharp
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexAuthReaderTests
{
    private static readonly DateTimeOffset Now = CodexTestData.Now;

    [Fact]
    public void TryParse_CurrentChatGptFile_ReadsTokensClaimsAndTimestamps()
    {
        var auth = CodexAuthReader.TryParse(Fixture.Read("Codex/auth-chatgpt.json"));

        Assert.NotNull(auth);
        Assert.Equal(CodexAuthMode.ChatGpt, auth.Mode); // OPENAI_API_KEY is set but auth_mode wins
        Assert.StartsWith("eyJ", auth.AccessToken);
        Assert.Equal("acct-fixture-0001", auth.AccountId);
        Assert.False(auth.IsFedramp);
        Assert.Equal("pro", auth.PlanTypeClaim);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793170800), auth.AccessTokenExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 2, 59, 0, TimeSpan.Zero), auth.LastRefresh!.Value, TimeSpan.FromSeconds(1));
        Assert.True(auth.IsFresh(Now));
    }

    [Fact]
    public void TryParse_LegacyFileWithoutAuthMode_UsesTokensAndJwtAccountId()
    {
        var auth = CodexAuthReader.TryParse(Fixture.Read("Codex/auth-legacy.json"));

        Assert.NotNull(auth);
        Assert.Equal(CodexAuthMode.ChatGpt, auth.Mode);
        Assert.Equal("test-opaque-access-token", auth.AccessToken);
        Assert.Equal("acct-fixture-0001", auth.AccountId); // tokens.account_id is null → id_token claim
        Assert.Null(auth.AccessTokenExpiresAt);             // opaque token: no exp
        Assert.True(auth.IsFresh(Now));                     // last_refresh 3 days ago
    }

    [Theory]
    [InlineData("chatgpt", CodexAuthMode.ChatGpt)]
    [InlineData("chatgptAuthTokens", CodexAuthMode.ChatGpt)]
    [InlineData("apikey", CodexAuthMode.ApiKey)]
    [InlineData("bedrockApiKey", CodexAuthMode.ApiKey)]
    [InlineData("bedrockAccessKeys", CodexAuthMode.ApiKey)]
    [InlineData("personalAccessToken", CodexAuthMode.Other)]
    [InlineData("agentIdentity", CodexAuthMode.Other)]
    [InlineData("headers", CodexAuthMode.Other)]
    [InlineData("somethingNew", CodexAuthMode.Other)]
    public void TryParse_MapsExplicitAuthMode(string authMode, CodexAuthMode expected)
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(authMode: authMode, openAiApiKey: "sk-test-key"));

        Assert.Equal(expected, auth!.Mode);
    }

    [Theory]
    [InlineData("""{"OPENAI_API_KEY":"sk-test-key"}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"OPENAI_API_KEY":null}""", CodexAuthMode.ChatGpt)]
    [InlineData("""{"OPENAI_API_KEY":null,"personal_access_token":"test-pat"}""", CodexAuthMode.Other)]
    [InlineData("""{"OPENAI_API_KEY":null,"bedrock_api_key":{"key":"test"}}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"OPENAI_API_KEY":null,"bedrock_access_keys":{"id":"test"}}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"OPENAI_API_KEY":"sk-test-key","tokens":{"access_token":"test-access-token"}}""", CodexAuthMode.ChatGpt)]
    public void TryParse_ResolvesMissingAuthMode(string json, CodexAuthMode expected)
    {
        Assert.Equal(expected, CodexAuthReader.TryParse(json)!.Mode);
    }

    [Fact]
    public void TryParse_AccountId_FallsBackToTopLevelJwtClaim()
    {
        var idToken = CodexTestData.Jwt("""{"chatgpt_account_id":"acct-top-level","https://api.openai.com/auth":{"chatgpt_account_id":"acct-nested"}}""");

        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: idToken, accountId: null));

        Assert.Equal("acct-top-level", auth!.AccountId);
    }

    [Fact]
    public void TryParse_AccountId_FallsBackToFirstNonEmptyOrganization()
    {
        var idToken = CodexTestData.Jwt("""{"organizations":[{"id":""},{"id":"org-test-0002"},{"id":"org-test-0003"}]}""");

        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: idToken, accountId: null));

        Assert.Equal("org-test-0002", auth!.AccountId);
    }

    [Fact]
    public void TryParse_AccountId_FallsBackToAccessTokenClaims()
    {
        var idToken = CodexTestData.Jwt("""{"email":"dev@example.com"}""");
        var accessToken = CodexTestData.Jwt("""{"exp":1793170800,"https://api.openai.com/auth":{"chatgpt_account_id":"acct-from-access"}}""");

        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: idToken, accessToken: accessToken, accountId: null));

        Assert.Equal("acct-from-access", auth!.AccountId);
    }

    [Fact]
    public void TryParse_StoredAccountId_WinsOverClaims()
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: CodexTestData.IdJwt("acct-claim"), accountId: "acct-stored"));

        Assert.Equal("acct-stored", auth!.AccountId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryParse_ReadsFedrampClaimFromIdToken(bool fedramp)
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(idToken: CodexTestData.IdJwt(fedramp: fedramp)));

        Assert.Equal(fedramp, auth!.IsFedramp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"auth_mode\":\"chat")]
    [InlineData("[]")]
    [InlineData("null")]
    public void TryParse_ReturnsNull_ForEmptyPartialOrNonObject(string text)
    {
        Assert.Null(CodexAuthReader.TryParse(text));
    }

    [Theory]
    [InlineData(6 * 60, true)]   // exp = now + 6 min
    [InlineData(5 * 60, false)]  // exp = now + 5 min: inside the margin
    [InlineData(4 * 60, false)]
    [InlineData(-60, false)]
    public void IsFresh_UsesExpiryWithFiveMinuteMargin(int secondsUntilExpiry, bool expected)
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: CodexTestData.AccessJwt(Now.AddSeconds(secondsUntilExpiry))));

        Assert.Equal(expected, auth!.IsFresh(Now));
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(30, false)]
    public void IsFresh_WithoutExp_UsesLastRefreshWithinEightDays(int daysAgo, bool expected)
    {
        var lastRefresh = Now.AddDays(-daysAgo).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: "test-opaque-access-token", lastRefresh: lastRefresh));

        Assert.Equal(expected, auth!.IsFresh(Now));
    }

    [Fact]
    public void IsFresh_IsFalse_WithoutExpOrLastRefresh_OrWithoutAccessToken()
    {
        var noTimes = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: "test-opaque-access-token", lastRefresh: null));
        var noTokens = CodexAuthReader.TryParse(CodexTestData.AuthJson(includeTokens: false));

        Assert.False(noTimes!.IsFresh(Now));
        Assert.False(noTokens!.HasAccessToken);
        Assert.False(noTokens.IsFresh(Now));
    }

    [Fact]
    public void ToString_NeverContainsTokens()
    {
        var auth = CodexAuthReader.TryParse(CodexTestData.AuthJson(accessToken: "test-secret-access-token"))!;

        Assert.DoesNotContain("test-secret-access-token", auth.ToString());
    }

    [Fact]
    public async Task ReadAsync_ReturnsMissing_WhenFileDoesNotExist()
    {
        var reader = new CodexAuthReader(new FakeFileSystem(), (_, _) => Task.CompletedTask);

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Missing, result.Status);
        Assert.Null(result.Auth);
    }

    [Fact]
    public async Task ReadAsync_RetriesPartialFile_ThenSucceeds()
    {
        var files = new SequenceFileSystem("", "{\"auth_mode\":\"chatgpt\",\"tok", CodexTestData.AuthJson());
        var delays = new List<TimeSpan>();
        var reader = new CodexAuthReader(files, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Ok, result.Status);
        Assert.Equal(CodexAuthMode.ChatGpt, result.Auth!.Mode);
        Assert.Equal(3, files.Reads);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100) }, delays);
    }

    [Fact]
    public async Task ReadAsync_ReturnsUnreadable_AfterThreeRetries_NotSignedOut()
    {
        var files = new SequenceFileSystem("{", "{", "{", "{", "{");
        var delays = new List<TimeSpan>();
        var reader = new CodexAuthReader(files, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Unreadable, result.Status);
        Assert.Equal(4, files.Reads);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) }, delays);
    }

    [Fact]
    public async Task ReadAsync_TreatsIoExceptionAsTransient()
    {
        var files = new SequenceFileSystem(new IOException("sharing violation"), CodexTestData.AuthJson());
        var reader = new CodexAuthReader(files, (_, _) => Task.CompletedTask);

        var result = await reader.ReadAsync(CodexTestData.AuthPath, CancellationToken.None);

        Assert.Equal(CodexAuthReadStatus.Ok, result.Status);
        Assert.Equal(2, files.Reads);
    }

    /// <summary>Returns the queued contents (or throws the queued exception) on each read.</summary>
    private sealed class SequenceFileSystem(params object[] reads) : IFileSystem
    {
        private readonly Queue<object> _reads = new(reads);
        public int Reads { get; private set; }

        public string? ReadAllTextShared(string path)
        {
            Reads++;
            var next = _reads.Count > 1 ? _reads.Dequeue() : _reads.Peek();
            if (next is Exception ex) throw ex;
            return (string)next;
        }

        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) => [];
        public DateTime GetLastWriteTimeUtc(string path) => CodexTestData.FileTime;
        public IEnumerable<string> ReadLinesReverse(string path) => [];
    }
}
```

- [ ] **Step 8: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexAuthReaderTests"`

Expected: 빌드 실패. `error CS0103: The name 'CodexAuthReader' does not exist in the current context` (및 `CodexAuthMode`에 대한 CS0103/CS0246)

- [ ] **Step 9: `CodexAuth`, `CodexAuthReader` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexAuth.cs`

```csharp
namespace AITokenUsage.Core.Providers.Codex;

/// <summary>How the Codex CLI is logged in, reduced to what the widget cares about.</summary>
public enum CodexAuthMode
{
    /// <summary>ChatGPT plan login (chatgpt, chatgptAuthTokens). Has plan limits.</summary>
    ChatGpt,
    /// <summary>API key style login (apikey, bedrockApiKey, bedrockAccessKeys). No plan limits.</summary>
    ApiKey,
    /// <summary>personalAccessToken, agentIdentity, headers or an unknown mode. Only the app-server can read limits.</summary>
    Other,
}

/// <summary>Parsed auth.json. ToString never prints tokens.</summary>
public sealed record CodexAuth(
    CodexAuthMode Mode,
    string? AccessToken,
    string? AccountId,
    bool IsFedramp,
    string? PlanTypeClaim,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? LastRefresh)
{
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LastRefreshMaxAge = TimeSpan.FromDays(8);

    public bool HasAccessToken => !string.IsNullOrEmpty(AccessToken);

    /// <summary>exp > now + 5 min; when exp is unknown, last_refresh younger than 8 days.</summary>
    public bool IsFresh(DateTimeOffset now)
    {
        if (!HasAccessToken) return false;
        if (AccessTokenExpiresAt is { } expiresAt) return expiresAt > now + ExpiryMargin;
        if (LastRefresh is { } refreshedAt) return now - refreshedAt < LastRefreshMaxAge;
        return false;
    }

    public override string ToString()
        => $"CodexAuth {{ Mode = {Mode}, HasAccessToken = {HasAccessToken}, HasAccountId = {AccountId is not null} }}";
}

public enum CodexAuthReadStatus { Ok, Missing, Unreadable }

public sealed record CodexAuthReadResult(CodexAuthReadStatus Status, CodexAuth? Auth)
{
    public static readonly CodexAuthReadResult Missing = new(CodexAuthReadStatus.Missing, null);
    public static readonly CodexAuthReadResult Unreadable = new(CodexAuthReadStatus.Unreadable, null);
}
```

`src/AITokenUsage.Core/Providers/Codex/CodexAuthReader.cs`

```csharp
using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>
/// Reads Codex auth.json with FileShare.ReadWrite|Delete (via IFileSystem). Never writes it.
/// A parse failure is retried (Codex rewrites the file in place) and is never treated as a logout.
/// </summary>
public sealed class CodexAuthReader
{
    public const string AuthClaim = "https://api.openai.com/auth";

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    private readonly IFileSystem _files;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public CodexAuthReader(IFileSystem files, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _files = files;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public async Task<CodexAuthReadResult> ReadAsync(string authFilePath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            string? text;
            try
            {
                text = _files.ReadAllTextShared(authFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text = ""; // transient (sharing violation etc.) - retry like a partial file
            }

            if (text is null) return CodexAuthReadResult.Missing;

            var auth = TryParse(text);
            if (auth is not null) return new CodexAuthReadResult(CodexAuthReadStatus.Ok, auth);
            if (attempt >= RetryDelays.Length) return CodexAuthReadResult.Unreadable;
            await _delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    /// <summary>Parses auth.json text. Returns null when the text is not a JSON object.</summary>
    public static CodexAuth? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tokens = Json.Obj(root, "tokens");
            var accessToken = tokens is { } t1 ? NonEmpty(Json.Str(t1, "access_token")) : null;
            var idToken = tokens is { } t2 ? NonEmpty(Json.Str(t2, "id_token")) : null;
            var storedAccountId = tokens is { } t3 ? NonEmpty(Json.Str(t3, "account_id")) : null;

            var rawMode = NonEmpty(Json.Str(root, "auth_mode"));
            var mode = rawMode is null ? ResolveMissingMode(root, accessToken) : MapMode(rawMode);

            var accountId = storedAccountId ?? AccountIdFromJwt(idToken) ?? AccountIdFromJwt(accessToken);
            var idClaims = AuthClaims(idToken);
            var isFedramp = idClaims is { } c1 && Json.Bool(c1, "chatgpt_account_is_fedramp") == true;
            var planClaim = idClaims is { } c2 ? NonEmpty(Json.Str(c2, "chatgpt_plan_type")) : null;

            return new CodexAuth(
                mode,
                accessToken,
                accountId,
                isFedramp,
                planClaim,
                Jwt.GetExpiry(accessToken),
                ParseTimestamp(Json.Str(root, "last_refresh")));
        }
    }

    /// <summary>RFC 3339 UTC timestamp (Codex writes 0/3/6/9 fraction digits).</summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static CodexAuthMode MapMode(string rawMode)
    {
        if (Is(rawMode, "apikey") || Is(rawMode, "bedrockApiKey") || Is(rawMode, "bedrockAccessKeys"))
            return CodexAuthMode.ApiKey;
        if (Is(rawMode, "chatgpt") || Is(rawMode, "chatgptAuthTokens"))
            return CodexAuthMode.ChatGpt;
        return CodexAuthMode.Other;
    }

    // Old files have no auth_mode. Codex resolves: personal_access_token > bedrock_api_key >
    // bedrock_access_keys > OPENAI_API_KEY != null > chatgpt. A ChatGPT login may also store a
    // token-exchanged OPENAI_API_KEY, so an existing access_token wins first.
    private static CodexAuthMode ResolveMissingMode(JsonElement root, string? accessToken)
    {
        if (accessToken is not null) return CodexAuthMode.ChatGpt;
        if (HasValue(root, "personal_access_token")) return CodexAuthMode.Other;
        if (HasValue(root, "bedrock_api_key") || HasValue(root, "bedrock_access_keys")) return CodexAuthMode.ApiKey;
        if (root.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind == JsonValueKind.String)
            return CodexAuthMode.ApiKey;
        return CodexAuthMode.ChatGpt;
    }

    private static string? AccountIdFromJwt(string? jwt)
    {
        if (!Jwt.TryReadPayload(jwt, out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
        if (NonEmpty(Json.Str(payload, "chatgpt_account_id")) is { } direct) return direct;
        if (Json.Obj(payload, AuthClaim) is { } auth && NonEmpty(Json.Str(auth, "chatgpt_account_id")) is { } nested)
            return nested;
        if (Json.Arr(payload, "organizations") is { } organizations)
        {
            foreach (var organization in organizations.EnumerateArray())
            {
                if (organization.ValueKind == JsonValueKind.Object && NonEmpty(Json.Str(organization, "id")) is { } id)
                    return id;
            }
        }
        return null;
    }

    private static JsonElement? AuthClaims(string? jwt)
        => Jwt.TryReadPayload(jwt, out var payload) && payload.ValueKind == JsonValueKind.Object
            ? Json.Obj(payload, AuthClaim)
            : null;

    private static bool HasValue(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
```

- [ ] **Step 10: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexAuthReaderTests"`

Expected: `Passed!  - Failed: 0, Passed: 41`

- [ ] **Step 11: `CodexWindowLabeler` 실패하는 테스트 작성**

스펙 §7.1의 299/300/10079/10080/43200/기타와 ±5% 경계(285/315 포함, 284/316 제외)를 모두 넣는다.

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexWindowLabelerTests.cs`

```csharp
using AITokenUsage.Core.Providers.Codex;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexWindowLabelerTests
{
    [Theory]
    [InlineData(299L, false, "5h", "5h", "5시간 한도")]
    [InlineData(300L, true, "5h", "5h", "5시간 한도")]
    [InlineData(285L, false, "5h", "5h", "5시간 한도")]     // 300 × 0.95
    [InlineData(315L, false, "5h", "5h", "5시간 한도")]     // 300 × 1.05
    [InlineData(284L, false, "primary", "한도", "사용 한도")]
    [InlineData(316L, true, "secondary", "보조", "보조 한도")]
    [InlineData(1440L, false, "daily", "일", "일간 한도")]
    [InlineData(10079L, true, "weekly", "주", "주간 한도")]
    [InlineData(10080L, false, "weekly", "주", "주간 한도")]
    [InlineData(43200L, false, "monthly", "월", "월간 한도")]
    [InlineData(525600L, false, "annual", "년", "연간 한도")]
    [InlineData(720L, false, "primary", "한도", "사용 한도")]
    [InlineData(720L, true, "secondary", "보조", "보조 한도")]
    [InlineData(0L, false, "primary", "한도", "사용 한도")]
    [InlineData(null, true, "secondary", "보조", "보조 한도")]
    public void Label_UsesWindowLengthWithFivePercentTolerance(long? minutes, bool isSecondary, string key, string shortLabel, string longLabel)
    {
        var label = CodexWindowLabeler.Label(minutes, isSecondary);

        Assert.Equal(new CodexWindowLabel(key, shortLabel, longLabel), label);
    }

    [Theory]
    [InlineData(18000d, 300L)]
    [InlineData(604800d, 10080L)]
    [InlineData(2592000d, 43200L)]
    [InlineData(61d, 2L)]
    [InlineData(0d, null)]
    [InlineData(-5d, null)]
    [InlineData(null, null)]
    public void MinutesFromSeconds_RoundsUp(double? seconds, long? expected)
    {
        Assert.Equal(expected, CodexWindowLabeler.MinutesFromSeconds(seconds));
    }

    [Fact]
    public void ToMeters_OrdersByWindowLength_NotByPosition()
    {
        var weeklyInPrimary = new CodexWindow(61, 10080, null);
        var fiveHourInSecondary = new CodexWindow(12, 300, null);

        var meters = CodexWindowLabeler.ToMeters(weeklyInPrimary, fiveHourInSecondary);

        Assert.Equal(new[] { "5h", "weekly" }, meters.Select(m => m.Key).ToArray());
        Assert.Equal(12, meters[0].UsedPercent);
        Assert.Equal(61, meters[1].UsedPercent);
    }

    [Fact]
    public void ToMeters_SingleMonthlyWindow_IsShownAlone()
    {
        var meters = CodexWindowLabeler.ToMeters(new CodexWindow(11, 43200, null), null);

        var meter = Assert.Single(meters);
        Assert.Equal("monthly", meter.Key);
        Assert.Equal("월", meter.ShortLabel);
    }

    [Fact]
    public void ToMeters_SkipsWindowsWithoutUsedPercent()
    {
        var meters = CodexWindowLabeler.ToMeters(new CodexWindow(null, 300, null), new CodexWindow(7, 10080, null));

        Assert.Equal("weekly", Assert.Single(meters).Key);
    }

    [Fact]
    public void ToMeters_DuplicateLengths_GetPositionalKey()
    {
        var meters = CodexWindowLabeler.ToMeters(new CodexWindow(1, 10080, null), new CodexWindow(2, 10080, null));

        Assert.Equal(new[] { "weekly", "secondary" }, meters.Select(m => m.Key).ToArray());
    }

    [Fact]
    public void ToMeters_FeaturePrefix_BuildsAdditionalLimitKeysAndLabels()
    {
        var reset = new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero);

        var meters = CodexWindowLabeler.ToMeters(
            new CodexWindow(30, 300, reset), new CodexWindow(100, 10080, null), "gpt_5_3_codex_spark", "GPT-5.3-Codex-Spark");

        Assert.Equal(2, meters.Count);
        Assert.Equal("gpt_5_3_codex_spark:5h", meters[0].Key);
        Assert.Equal("5h·Sp", meters[0].ShortLabel);
        Assert.Equal("GPT-5.3-Codex-Spark · 5시간 한도", meters[0].LongLabel);
        Assert.Equal(reset, meters[0].ResetsAt);
        Assert.Equal("gpt_5_3_codex_spark:weekly", meters[1].Key);
        Assert.Equal("주·Sp", meters[1].ShortLabel);
        Assert.Equal(100, meters[1].UsedPercent);
    }

    [Theory]
    [InlineData("GPT-5.3-Codex-Spark", "Sp")]
    [InlineData("gpt_5_3_codex_spark", "sp")]
    [InlineData("o3", "o3")]
    [InlineData("5", "5")]
    public void Abbreviate_TakesLastWordWithLetters(string name, string expected)
    {
        Assert.Equal(expected, CodexWindowLabeler.Abbreviate(name));
    }
}
```

- [ ] **Step 12: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexWindowLabelerTests"`

Expected: 빌드 실패. `error CS0103: The name 'CodexWindowLabeler' does not exist in the current context` (및 `CodexWindow`, `CodexWindowLabel`에 대한 CS0246)

- [ ] **Step 13: `CodexWindowLabeler` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexWindowLabeler.cs`

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>One rate-limit window from any Codex source (wham, app-server, rollout).</summary>
public sealed record CodexWindow(double? UsedPercent, long? WindowMinutes, DateTimeOffset? ResetsAt);

public sealed record CodexWindowLabel(string Key, string ShortLabel, string LongLabel);

/// <summary>
/// Labels windows by their length (±5%), never by primary/secondary position,
/// and turns them into UsageMeters ordered from the shortest window.
/// </summary>
public static class CodexWindowLabeler
{
    public static readonly CodexWindowLabel FiveHour = new("5h", "5h", "5시간 한도");
    public static readonly CodexWindowLabel Daily = new("daily", "일", "일간 한도");
    public static readonly CodexWindowLabel Weekly = new("weekly", "주", "주간 한도");
    public static readonly CodexWindowLabel Monthly = new("monthly", "월", "월간 한도");
    public static readonly CodexWindowLabel Annual = new("annual", "년", "연간 한도");
    public static readonly CodexWindowLabel Primary = new("primary", "한도", "사용 한도");
    public static readonly CodexWindowLabel Secondary = new("secondary", "보조", "보조 한도");

    private static readonly (long Minutes, CodexWindowLabel Label)[] Known =
    [
        (300, FiveHour),
        (1440, Daily),
        (10080, Weekly),
        (43200, Monthly),
        (525600, Annual),
    ];

    /// <summary>wham/usage limit_window_seconds to minutes: (s + 59) / 60; s ≤ 0 → null.</summary>
    public static long? MinutesFromSeconds(double? seconds)
    {
        if (seconds is not double value || value <= 0) return null;
        var whole = (long)Math.Ceiling(value);
        return (whole + 59) / 60;
    }

    public static CodexWindowLabel Label(long? minutes, bool isSecondary)
    {
        if (minutes is long m && m > 0)
        {
            foreach (var (expected, label) in Known)
            {
                // m within ±5% of expected, in integer math: 0.95e ≤ m ≤ 1.05e
                if (m * 100 >= expected * 95 && m * 100 <= expected * 105)
                    return label;
            }
        }
        return isSecondary ? Secondary : Primary;
    }

    /// <summary>
    /// Builds meters for a primary/secondary pair. Windows without UsedPercent are skipped.
    /// With <paramref name="featureKey"/> (additional limits) keys become "&lt;feature&gt;:&lt;key&gt;".
    /// </summary>
    public static IReadOnlyList<UsageMeter> ToMeters(
        CodexWindow? primary, CodexWindow? secondary, string? featureKey = null, string? featureName = null)
    {
        var entries = new List<(long SortMinutes, int Index, UsageMeter Meter)>(2);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        AddWindow(primary, isSecondary: false, index: 0);
        AddWindow(secondary, isSecondary: true, index: 1);
        return entries.OrderBy(e => e.SortMinutes).ThenBy(e => e.Index).Select(e => e.Meter).ToList();

        void AddWindow(CodexWindow? window, bool isSecondary, int index)
        {
            if (window?.UsedPercent is not double used) return;
            var label = Label(window.WindowMinutes, isSecondary);
            if (!usedKeys.Add(label.Key))
            {
                label = isSecondary ? Secondary : Primary;
                if (!usedKeys.Add(label.Key)) return;
            }

            var meter = featureKey is null
                ? new UsageMeter(label.Key, label.ShortLabel, label.LongLabel, used, window.ResetsAt)
                : new UsageMeter(
                    $"{featureKey}:{label.Key}",
                    $"{label.ShortLabel}·{Abbreviate(featureName ?? featureKey)}",
                    $"{featureName ?? featureKey} · {label.LongLabel}",
                    used,
                    window.ResetsAt);
            entries.Add((window.WindowMinutes ?? long.MaxValue, index, meter));
        }
    }

    /// <summary>"GPT-5.3-Codex-Spark" → "Sp": first two letters of the last word that has a letter.</summary>
    public static string Abbreviate(string name)
    {
        var words = name.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = words.Length - 1; i >= 0; i--)
        {
            if (words[i].Any(char.IsLetter))
                return words[i].Length <= 2 ? words[i] : words[i][..2];
        }
        return name.Length <= 2 ? name : name[..2];
    }
}
```

- [ ] **Step 14: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexWindowLabelerTests"`

Expected: `Passed!  - Failed: 0, Passed: 31`

- [ ] **Step 15: `CodexUsageParser` 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexUsageParserTests.cs`

```csharp
using System.Text.Json;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexUsageParserTests
{
    private static readonly DateTimeOffset Now = CodexTestData.Now;
    private static readonly DateTimeOffset FiveHourReset = DateTimeOffset.FromUnixTimeSeconds(1793163600);  // 2026-10-28 05:00Z
    private static readonly DateTimeOffset WeeklyReset = DateTimeOffset.FromUnixTimeSeconds(1793415600);    // 2026-10-31 03:00Z

    [Fact]
    public void ParseWham_Plus_FiveHourAndWeekly()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("Codex/wham-plus.json"), Now);

        Assert.Equal("plus", result.PlanType);
        Assert.Equal(2, result.Meters.Count);
        var fiveHour = result.Meters[0];
        Assert.Equal(("5h", "5h", "5시간 한도"), (fiveHour.Key, fiveHour.ShortLabel, fiveHour.LongLabel));
        Assert.Equal(42, fiveHour.UsedPercent);
        Assert.Equal(FiveHourReset, fiveHour.ResetsAt);
        Assert.Null(fiveHour.ServerSeverity);
        var weekly = result.Meters[1];
        Assert.Equal(("weekly", "주", "주간 한도"), (weekly.Key, weekly.ShortLabel, weekly.LongLabel));
        Assert.Equal(5, weekly.UsedPercent);
        Assert.Equal(WeeklyReset, weekly.ResetsAt);
    }

    [Fact]
    public void ParseWham_ProWithSpark_AddsAdditionalLimitMeters_AndSkipsBrokenEntries()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("Codex/wham-pro-spark.json"), Now);

        Assert.Equal("pro", result.PlanType);
        Assert.Equal(
            new[] { "5h", "weekly", "gpt_5_3_codex_spark:5h", "gpt_5_3_codex_spark:weekly" },
            result.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(33.5, result.Meters[1].UsedPercent);
        var sparkWeekly = result.Meters[3];
        Assert.Equal("주·Sp", sparkWeekly.ShortLabel);
        Assert.Equal("GPT-5.3-Codex-Spark · 주간 한도", sparkWeekly.LongLabel);
        Assert.Equal(100, sparkWeekly.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793761200), sparkWeekly.ResetsAt);
    }

    [Fact]
    public void ParseWham_Free_SingleThirtyDayPrimary()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("Codex/wham-free.json"), Now);

        Assert.Equal("free", result.PlanType);
        var meter = Assert.Single(result.Meters);
        Assert.Equal(("monthly", "월", "월간 한도"), (meter.Key, meter.ShortLabel, meter.LongLabel));
        Assert.Equal(11, meter.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795748400), meter.ResetsAt);
    }

    [Fact]
    public void ParseWham_PrimaryIsWeekly_IsLabeledByLength()
    {
        var result = CodexUsageParser.ParseWham(Fixture.Read("Codex/wham-primary-weekly.json"), Now);

        Assert.Equal(new[] { "5h", "weekly" }, result.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(12, result.Meters[0].UsedPercent);
        Assert.Equal(FiveHourReset, result.Meters[0].ResetsAt);
        Assert.Equal(61, result.Meters[1].UsedPercent);
        Assert.Equal(WeeklyReset, result.Meters[1].ResetsAt);
    }

    [Fact]
    public void ParseWham_UsesResetAfterSeconds_WhenResetAtMissing()
    {
        const string json = """
            {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":"7.5","limit_window_seconds":18000,"reset_after_seconds":600}}}
            """;

        var meter = Assert.Single(CodexUsageParser.ParseWham(json, Now).Meters);

        Assert.Equal(7.5, meter.UsedPercent);
        Assert.Equal(Now.AddMinutes(10), meter.ResetsAt);
    }

    [Fact]
    public void ParseWham_NullRateLimit_ReturnsNoMeters()
    {
        var result = CodexUsageParser.ParseWham("""{"plan_type":"go","rate_limit":null,"additional_rate_limits":null}""", Now);

        Assert.Equal("go", result.PlanType);
        Assert.Empty(result.Meters);
    }

    [Theory]
    [InlineData("<html><body>Just a moment...</body></html>")]
    [InlineData("[]")]
    [InlineData("")]
    public void ParseWham_Throws_ForNonObjectBody(string body)
    {
        Assert.ThrowsAny<JsonException>(() => CodexUsageParser.ParseWham(body, Now));
    }

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("plus", "Plus")]
    [InlineData("team", "Team")]
    [InlineData("free", "Free")]
    [InlineData("self_serve_business_usage_based", "Self_serve_business_usage_based")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FormatPlan_CapitalizesRawPlanType(string? planType, string? expected)
    {
        Assert.Equal(expected, CodexUsageParser.FormatPlan(planType));
    }
}
```

- [ ] **Step 16: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexUsageParserTests"`

Expected: 빌드 실패. `error CS0103: The name 'CodexUsageParser' does not exist in the current context`

- [ ] **Step 17: `CodexUsageParser` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexUsageParser.cs`

```csharp
using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

public sealed record CodexUsageResult(string? PlanType, IReadOnlyList<UsageMeter> Meters);

/// <summary>Parses GET {base}/wham/usage. Unknown fields are ignored; a broken item only drops itself.</summary>
public static class CodexUsageParser
{
    /// <exception cref="JsonException">The body is not a JSON object (e.g. an HTML challenge page).</exception>
    public static CodexUsageResult ParseWham(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("wham/usage body is not a JSON object.");

        var meters = new List<UsageMeter>();
        if (Json.Obj(root, "rate_limit") is { } main)
            meters.AddRange(CodexWindowLabeler.ToMeters(Window(main, "primary_window", now), Window(main, "secondary_window", now)));

        if (Json.Arr(root, "additional_rate_limits") is { } additional)
        {
            foreach (var item in additional.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var feature = NonEmpty(Json.Str(item, "metered_feature"));
                if (feature is null || string.Equals(feature, "codex", StringComparison.OrdinalIgnoreCase)) continue;
                if (Json.Obj(item, "rate_limit") is not { } limit) continue;
                var name = NonEmpty(Json.Str(item, "limit_name")) ?? feature;
                meters.AddRange(CodexWindowLabeler.ToMeters(
                    Window(limit, "primary_window", now), Window(limit, "secondary_window", now), feature, name));
            }
        }

        return new CodexUsageResult(NonEmpty(Json.Str(root, "plan_type")), meters);
    }

    /// <summary>Raw plan_type with the first letter upper-cased ("pro" → "Pro"). Empty or "unknown" → null.</summary>
    public static string? FormatPlan(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType)) return null;
        var trimmed = planType.Trim();
        if (trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static CodexWindow? Window(JsonElement parent, string name, DateTimeOffset now)
    {
        if (Json.Obj(parent, name) is not { } window) return null;
        var used = Json.Num(window, "used_percent");
        if (used is null) return null;

        var minutes = CodexWindowLabeler.MinutesFromSeconds(Json.Num(window, "limit_window_seconds"));
        DateTimeOffset? resetsAt = Json.Num(window, "reset_at") is double epoch && epoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)epoch)
            : null;
        if (resetsAt is null && Json.Num(window, "reset_after_seconds") is double after && after > 0)
            resetsAt = now.AddSeconds(after);
        return new CodexWindow(used, minutes, resetsAt);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
```

- [ ] **Step 18: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexUsageParserTests"`

Expected: `Passed!  - Failed: 0, Passed: 17`

- [ ] **Step 19: `CodexUsageClient`(요청 헤더) 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexUsageClientTests.cs`

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexUsageClientTests
{
    private static CodexAuth Auth(string? accountId = CodexTestData.AccountId, bool fedramp = false)
        => new(CodexAuthMode.ChatGpt, "test-access-token", accountId, fedramp, "plus", CodexTestData.Now.AddHours(1), null);

    [Fact]
    public void BuildRequest_SendsRequiredHeaders_AndNothingElse()
    {
        using var request = CodexUsageClient.BuildRequest(CodexTestData.DefaultUsageUrl, Auth());

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(CodexTestData.DefaultUsageUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-access-token", request.Headers.Authorization.Parameter);
        Assert.Equal(CodexTestData.AccountId, Assert.Single(request.Headers.GetValues("chatgpt-account-id")));
        Assert.StartsWith("AITokenUsage/", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
        Assert.False(request.Headers.Contains("X-OpenAI-Fedramp"));
        Assert.False(request.Headers.Contains("x-openai-codex-luna-reserve"));
        Assert.False(request.Headers.Contains("originator"));
        Assert.Null(request.Content);
    }

    [Fact]
    public void BuildRequest_AddsFedrampHeader_OnlyForFedrampAccounts()
    {
        using var request = CodexUsageClient.BuildRequest(CodexTestData.DefaultUsageUrl, Auth(fedramp: true));

        Assert.Equal("true", Assert.Single(request.Headers.GetValues("X-OpenAI-Fedramp")));
    }

    [Fact]
    public void BuildRequest_OmitsAccountHeader_WhenAccountUnknown()
    {
        using var request = CodexUsageClient.BuildRequest(CodexTestData.DefaultUsageUrl, Auth(accountId: null));

        Assert.False(request.Headers.Contains("chatgpt-account-id"));
    }

    [Fact]
    public async Task GetUsageAsync_Ok_ReturnsBody()
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ => Json(HttpStatusCode.OK, Fixture.Read("Codex/wham-plus.json")));
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(CodexHttpOutcome.Ok, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("\"plan_type\"", result.Body);
        Assert.DoesNotContain("plan_type", result.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CodexHttpOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CodexHttpOutcome.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CodexHttpOutcome.Failed)]
    public async Task GetUsageAsync_MapsErrorStatus(HttpStatusCode status, CodexHttpOutcome expected)
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ => Json(status, "{\"error\":{\"message\":\"test\"}}"));
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task GetUsageAsync_429_ReadsRetryAfter()
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(CodexHttpOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), result.RetryAfter);
    }

    [Fact]
    public async Task GetUsageAsync_NetworkError_IsFailedWithoutStatus()
    {
        var http = new StubHttpHandler();
        http.When(_ => true, _ => throw new HttpRequestException("connection refused"));
        var client = new CodexUsageClient(new HttpClient(http), new FakeTimeProvider(CodexTestData.Now));

        var result = await client.GetUsageAsync(CodexTestData.DefaultUsageUrl, Auth(), CancellationToken.None);

        Assert.Equal(CodexHttpOutcome.Failed, result.Outcome);
        Assert.Null(result.StatusCode);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
```

- [ ] **Step 20: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexUsageClientTests"`

Expected: 빌드 실패. `error CS0103: The name 'CodexUsageClient' does not exist in the current context` (및 `CodexHttpOutcome`에 대한 CS0246)

- [ ] **Step 21: `CodexUsageClient` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexUsageClient.cs`

```csharp
using System.Net;
using System.Net.Http.Headers;

namespace AITokenUsage.Core.Providers.Codex;

public enum CodexHttpOutcome { Ok, Unauthorized, RateLimited, Failed }

/// <summary>Result of one wham/usage call. Body stays in memory only; ToString never prints it.</summary>
public sealed record CodexHttpResult(CodexHttpOutcome Outcome, int? StatusCode, string? Body, TimeSpan? RetryAfter)
{
    public override string ToString() => $"CodexHttpResult {{ Outcome = {Outcome}, StatusCode = {StatusCode} }}";
}

/// <summary>
/// GET {base}/wham/usage with Authorization, chatgpt-account-id, User-Agent, Accept and
/// (FedRAMP accounts only) X-OpenAI-Fedramp. Never sends Codex's reserve opt-in header or an
/// originator header, and never refreshes tokens.
/// </summary>
public sealed class CodexUsageClient
{
    public static readonly string UserAgent =
        "AITokenUsage/" + (typeof(CodexUsageClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public CodexUsageClient(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public static HttpRequestMessage BuildRequest(string usageUrl, CodexAuth auth)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, usageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(auth.AccountId))
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", auth.AccountId);
        if (auth.IsFedramp)
            request.Headers.TryAddWithoutValidation("X-OpenAI-Fedramp", "true");
        return request;
    }

    public async Task<CodexHttpResult> GetUsageAsync(string usageUrl, CodexAuth auth, CancellationToken ct)
    {
        using var request = BuildRequest(usageUrl, auth);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new CodexHttpResult(CodexHttpOutcome.Unauthorized, status, null, null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new CodexHttpResult(CodexHttpOutcome.RateLimited, status, null, RetryAfterOf(response));
            if (!response.IsSuccessStatusCode)
                return new CodexHttpResult(CodexHttpOutcome.Failed, status, null, null);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new CodexHttpResult(CodexHttpOutcome.Ok, status, body, null);
        }
        catch (HttpRequestException)
        {
            return new CodexHttpResult(CodexHttpOutcome.Failed, null, null, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CodexHttpResult(CodexHttpOutcome.Failed, null, null, null); // HttpClient timeout
        }
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            if (wait > TimeSpan.Zero) return wait;
        }
        return null;
    }
}
```

- [ ] **Step 22: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexUsageClientTests"`

Expected: `Passed!  - Failed: 0, Passed: 9`

- [ ] **Step 23: 전체 빌드·테스트**

Run: `dotnet build AITokenUsage.sln` → Expected: `0 Error(s)`, 새 경고 없음.
Run: `dotnet test AITokenUsage.sln` → Expected: 실패 0. Codex 테스트만 보면 `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~Providers.Codex"` → `Passed: 123`.

- [ ] **Step 24: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Codex tests/AITokenUsage.Core.Tests/Providers/Codex tests/AITokenUsage.Core.Tests/Fixtures/Codex tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj
git commit -m "feat(core): Codex 경로·auth.json 판독·창 라벨·wham/usage 파서 추가" -m "CODEX_HOME·config.toml 기준 URL 정규화, auth_mode·계정 ID·토큰 신선도 규칙, ±5% 창 라벨과 요청 헤더(chatgpt-account-id, X-OpenAI-Fedramp) 테스트를 포함한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

(csproj를 바꾸지 않았다면 `git add`에 포함돼도 아무 변화가 없다.)

---

### Task 10: Codex 2 — rollout 로그·app-server·`CodexUsageProvider` 조합

**목표:** 토큰이 오래됐거나 `wham/usage`가 실패할 때 쓸 두 대체 경로(`codex app-server` JSON-RPC, rollout 세션 로그 A–D 형식)를 만들고, 스펙 §5.2 순서대로 조합한 `CodexUsageProvider`를 완성한다.

**Files:**
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexRolloutReader.cs`
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexAppServerClient.cs`
- Create: `src/AITokenUsage.Core/Providers/Codex/CodexUsageProvider.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-a.jsonl`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-b.jsonl`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-c.jsonl`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-d.jsonl`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/Codex/app-server-stdout.jsonl`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexRolloutReaderTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexAppServerClientTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Codex/CodexUsageProviderTests.cs`

**Interfaces:**
- Consumes
  - Task 2: `ProviderContext`, `IUsageProvider`, `IFileSystem.DirectoryExists/EnumerateFiles/GetLastWriteTimeUtc/ReadLinesReverse`, `IProcessRunner.FindOnPath/StartInteractive`, `IInteractiveProcess.WriteLineAsync/ReadLineAsync/DisposeAsync`, `ProcessSpec`, `IAppLog`, `UsageSnapshot`·`UsageSnapshot.WithStatus`, `ProviderStatus`, `ProviderIds.Codex`, `MeterOption`
  - Task 3: `Json.Str/Num/Obj`
  - Task 9: `CodexPaths`, `CodexAuthReader`(·`ParseTimestamp`), `CodexAuth`, `CodexWindow`, `CodexWindowLabeler.ToMeters`, `CodexUsageParser.ParseWham/FormatPlan`, `CodexUsageClient`
  - 테스트(Task 2): `FakeProcessRunner()`·`SetPath`·`OnInteractive`, `FakeInteractiveProcess()`·`EnqueueOutput`·`Written`, `FakeFileSystem`·`AddDirectory`·`Remove`·`FileExists`, `TestContexts.Create`, `NullLog`; `FakeTimeProvider.Advance/SetLocalTimeZone`
- Produces (모두 `namespace AITokenUsage.Core.Providers.Codex;`) — Task 13 `ProviderCatalog`가 `new CodexUsageProvider(ctx)`를 쓴다.

```csharp
public sealed record CodexRolloutSnapshot(DateTimeOffset Timestamp, string? PlanType, IReadOnlyList<UsageMeter> Meters);

public sealed class CodexRolloutReader
{
    public const int MaxFilesToScan = 10;
    public CodexRolloutReader(IFileSystem files);
    public CodexRolloutSnapshot? ReadLatest(string sessionsDirectory, DateTimeOffset now);
    public static CodexRolloutSnapshot? ParseLine(string line, DateTimeOffset now);
}

public enum CodexAppServerOutcome { Ok, AuthRequired, Failed, TimedOut }

public sealed record CodexAppServerResult(CodexAppServerOutcome Outcome, string? PlanType, IReadOnlyList<UsageMeter> Meters)
{
    public static CodexAppServerResult Of(CodexAppServerOutcome outcome);
}

public sealed class CodexAppServerClient
{
    public const string CommandName = "codex";
    public static readonly TimeSpan InitializeTimeout; // 8초
    public static readonly TimeSpan RequestTimeout;    // 3초
    public static readonly TimeSpan ProcessTimeout;    // 20초 (ProcessSpec.Timeout 안전망)
    public CodexAppServerClient(IProcessRunner processes, TimeProvider time, IAppLog log);
    public static ProcessSpec BuildSpec(string codexPath);
    public Task<CodexAppServerResult> ReadRateLimitsAsync(string codexPath, CancellationToken ct);
    public static CodexAppServerResult MapRateLimits(JsonElement result);
}

public sealed class CodexUsageProvider : IUsageProvider
{
    public static readonly TimeSpan AppServerMinimumGap; // 15분
    public const string MessageApiKey = "API 키 로그인 — 플랜 한도 없음";
    public const string MessageNotInstalled = "설치 안 됨 — codex 설치 후 codex login";
    public const string MessageSignedOut = "로그인 필요 — 터미널에서 codex login";
    public const string MessageWaitingForCli = "Codex 로그인 갱신 대기 — 터미널에서 codex 실행";
    public const string MessageUnsupportedLogin = "이 로그인 방식은 codex 설치 후 조회 가능";
    public const string MessageAuthUnreadable = "auth.json을 읽지 못했습니다 — 잠시 후 다시 시도";
    public const string MessageFailed = "Codex 사용량을 불러오지 못했습니다";
    public CodexUsageProvider(ProviderContext ctx);
    public string Id { get; }                                   // "codex"
    public string DisplayName { get; }                          // "Codex"
    public TimeSpan MinInterval { get; }                        // 1분
    public IReadOnlyList<string> WatchPaths { get; }            // [<CODEX_HOME>\auth.json]
    public IReadOnlyList<MeterOption> StaticMeterOptions { get; } // 5h, weekly, daily, monthly
    public Task<UsageSnapshot> FetchAsync(CancellationToken ct);
}
```

**설계 메모**
- **rollout 로그** (`CodexRolloutReader`)
  - `sessions` 아래를 재귀로 열거한 뒤 파일명이 `rollout-`로 시작하고 `.jsonl`로 끝나는 것만 남긴다(`.jsonl.zst` 제외). `LastWriteTimeUtc` 내림차순으로 최대 10개 파일을 보고, 각 파일은 `IFileSystem.ReadLinesReverse`로 끝에서부터 읽는다. 쓸 수 있는 줄이 없으면 다음(더 오래된) 파일로 넘어간다.
  - 쓸 수 있는 줄: `type == "event_msg"`, `payload.type == "token_count"`, `payload.rate_limits`가 객체, `limit_id`가 없거나 null이거나 `"codex"`, `timestamp`가 있고, 창이 하나 이상.
  - 형식 A(`primary_used_percent`/`primary_window_minutes`, 리셋 정보 없음), B(`resets_in_seconds` → 줄 `timestamp` + 초), C·D(`resets_at` epoch 초). 창 분은 `window_minutes`(A는 `*_window_minutes`).
  - `resets_at`이 이미 지났으면 사용량 0%, 리셋 시각 null. 빠진 창은 미터를 만들지 않는다.
- **app-server** (`CodexAppServerClient`)
  - 실행: `FindOnPath("codex")` 결과가 `.exe`/`.com`이면 그 경로로 직접, 그 외(`codex.cmd` npm shim, 확장자 없음)는 `cmd.exe /d /c codex -s read-only -a never app-server`.
  - 줄 단위 JSON, **`"jsonrpc"` 필드 없음**: `{"id":1,"method":"initialize",...}`(8초) → `{"method":"initialized","params":{}}` → `{"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}`(3초). `supportsLunaReserve`는 보내지 않는다.
  - `method`가 있는 줄(알림·서버 요청), 다른 id, JSON이 아닌 잡음 줄은 건너뛴다. 오류 메시지에 `authentication required`가 있으면 `AuthRequired`, 그 외 오류는 `Failed`, 시간 초과는 `TimedOut`, stdout 종료(null)는 `Failed`.
  - 매핑: `rateLimitsByLimitId.codex`(없으면 `rateLimits`) → 기본 미터, 나머지 `rateLimitsByLimitId` 항목 → `<limitId>:<키>` 추가 미터. 창은 `usedPercent`/`windowDurationMins`/`resetsAt`.
  - 시간 초과는 `new CancellationTokenSource(timeout, TimeProvider)`로 만들어 `FakeTimeProvider.Advance`로 테스트한다. 끝나면 항상 `DisposeAsync()`(stdin 닫기·종료는 Task 2 실제 구현 책임).
- **`CodexUsageProvider` 순서** (스펙 §5.2, 보고서 §3.6)

  | 상황 | 결과 |
  |---|---|
  | `auth_mode` = `apikey`·`bedrock*` | `Unsupported`, "API 키 로그인 — 플랜 한도 없음". HTTP·프로세스 호출 없음 |
  | 토큰 신선 + `wham/usage` 200 JSON | `Ok`, 플랜 = `plan_type`(없으면 id_token `chatgpt_plan_type`) |
  | `wham/usage` 401 | `auth.json`을 다시 읽어 access_token이 **바뀌었을 때만** 새 토큰으로 1회 재시도 |
  | `wham/usage` 429 | `RateLimited`, `RetryAfter` 그대로, 메시지 "요청 제한 — HH:mm 이후 재시도"(없으면 5분 기준) |
  | 토큰 오래됨 / 401 그대로 / 403·5xx·네트워크·비JSON 본문 / `auth.json` 없음·토큰 없음·손상 | 대체 경로로 |
  | 대체 1: `codex`가 PATH에 있고 마지막 실행 후 15분 지남 | app-server 실행. 성공하면 `Ok`(그 결과를 기억) |
  | 15분 안(쿨다운) | app-server를 다시 띄우지 않고, 기억해 둔 성공 결과와 rollout 중 더 새 것을 쓴다 |
  | 대체 2: rollout 로그 | `Ok`, 메시지 "로그 기준 · HH:mm"(로컬 시각, `TimeProvider.LocalTimeZone`) |
  | 모두 실패 | app-server가 인증 필요 → `SignedOut` "로그인 필요 — 터미널에서 codex login" / `auth.json` 없음 + codex 없음 → `NotInstalled` "설치 안 됨 — codex 설치 후 codex login" / `auth.json` 없음 + codex 있음 → `SignedOut` / 오래된 토큰·401 → `WaitingForCliRefresh` "Codex 로그인 갱신 대기 — 터미널에서 codex 실행" / 토큰 없는 로그인(PAT 등) + codex 없음 → `Unsupported` "이 로그인 방식은 codex 설치 후 조회 가능" / `auth.json` 손상 → `Error` "auth.json을 읽지 못했습니다 — 잠시 후 다시 시도" / HTTP 실패 → `Error` "Codex 사용량을 불러오지 못했습니다 (HTTP 503)" / 그 밖 → `Error` "Codex 사용량을 불러오지 못했습니다" |

  - `FetchAsync`는 `SemaphoreSlim`으로 직렬화하고, 취소가 아닌 예외는 모두 `Error` 스냅샷으로 바꾼다(로그에는 예외 형식 이름만).
  - `IFileSystem`에는 쓰기 API가 없으므로 `auth.json`에 쓰는 경로가 구조적으로 없다. 토큰 갱신 엔드포인트도 호출하지 않는다.

- [ ] **Step 1: rollout·app-server Fixtures 작성**

모든 줄은 보고서 §4.2–4.3의 필드명을 그대로 쓴 가공 샘플이다. 기준 시각은 `2026-10-28T03:00:00Z`이고, `1793152800` = 02:00Z, `1793163600` = 05:00Z, `1793415600` = 10/31 03:00Z, `1793761200` = 11/4 03:00Z이다.

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-a.jsonl` — 형식 A(v0.40). 기대값: 마지막 `token_count`(01:02:01Z) → 5h 12%, 주 30%, 리셋 없음.

```jsonl
{"timestamp":"2025-09-20T01:00:00.000Z","type":"session_meta","payload":{"id":"00000000-0000-4000-8000-00000000000a","timestamp":"2025-09-20T01:00:00.000Z","cwd":"C:\work\demo","originator":"codex_cli_rs","cli_version":"0.40.0"}}
{"timestamp":"2025-09-20T01:00:05.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary_used_percent":3.0,"secondary_used_percent":20.0,"primary_to_secondary_ratio_percent":3.0,"primary_window_minutes":299,"secondary_window_minutes":10079}}}
{"timestamp":"2025-09-20T01:02:00.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}}
{"timestamp":"2025-09-20T01:02:01.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1200,"cached_input_tokens":0,"output_tokens":300,"reasoning_output_tokens":0,"total_tokens":1500}},"rate_limits":{"primary_used_percent":12.0,"secondary_used_percent":30.0,"primary_to_secondary_ratio_percent":3.0,"primary_window_minutes":299,"secondary_window_minutes":10079}}}
{"timestamp":"2025-09-20T01:02:02.000Z","type":"event_msg","payload":{"type":"agent_message","message":"done"}}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-b.jsonl` — 형식 B(v0.45). 마지막 줄은 `rate_limits: null`이라 건너뛰고, 02:30Z 줄 → 5h 25%(리셋 02:30 + 9000초 = 05:00Z), 주 22%(리셋 11/1 02:30Z).

```jsonl
{"timestamp":"2026-10-28T01:00:00.000Z","type":"session_meta","payload":{"id":"00000000-0000-4000-8000-00000000000b","timestamp":"2026-10-28T01:00:00.000Z","cwd":"C:\work\demo","originator":"codex_cli_rs","cli_version":"0.45.0"}}
{"timestamp":"2026-10-28T01:10:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":5.0,"window_minutes":299,"resets_in_seconds":17000},"secondary":{"used_percent":20.0,"window_minutes":10079,"resets_in_seconds":350000}}}}
{"timestamp":"2026-10-28T02:30:00.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":5000,"cached_input_tokens":0,"output_tokens":900,"reasoning_output_tokens":0,"total_tokens":5900}},"rate_limits":{"primary":{"used_percent":25.0,"window_minutes":299,"resets_in_seconds":9000},"secondary":{"used_percent":22.0,"window_minutes":10079,"resets_in_seconds":345600}}}}
{"timestamp":"2026-10-28T02:30:01.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":5000,"cached_input_tokens":0,"output_tokens":900,"reasoning_output_tokens":0,"total_tokens":5900}},"rate_limits":null}}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-c.jsonl` — 형식 C(v0.66, `credits`·`plan_type`). 00:40Z 줄 → 5h 64%(리셋 02:00Z), 주 41%, 플랜 `plus`. 03:00Z 기준으로는 5h 리셋이 지나 0%.

```jsonl
{"timestamp":"2026-10-27T23:00:00.000Z","type":"session_meta","payload":{"id":"00000000-0000-4000-8000-00000000000c","timestamp":"2026-10-27T23:00:00.000Z","cwd":"C:\work\demo","originator":"codex_cli_rs","cli_version":"0.66.0"}}
{"timestamp":"2026-10-27T23:30:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":10.0,"window_minutes":300,"resets_at":1793152800},"secondary":{"used_percent":40.0,"window_minutes":10080,"resets_at":1793415600},"credits":{"has_credits":false,"unlimited":false,"balance":null},"plan_type":"plus"}}}
{"timestamp":"2026-10-28T00:40:00.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":8000,"cached_input_tokens":2000,"output_tokens":1500,"reasoning_output_tokens":400,"total_tokens":9500}},"rate_limits":{"primary":{"used_percent":64.0,"window_minutes":300,"resets_at":1793152800},"secondary":{"used_percent":41.0,"window_minutes":10080,"resets_at":1793415600},"credits":{"has_credits":false,"unlimited":false,"balance":null},"plan_type":"plus"}}}
{"timestamp":"2026-10-28T00:40:01.000Z","type":"turn_context","payload":{"cwd":"C:\work\demo","approval_policy":"on-request","model":"gpt-5-codex"}}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/rollout-d.jsonl` — 형식 D(현재). 뒤쪽의 `gpt_5_3_codex_spark`, `codex_other` 줄은 건너뛰고 02:50Z `codex` 줄 → 5h 42%, 주 5%, 플랜 `pro`.

```jsonl
{"timestamp":"2026-10-28T02:40:00.000Z","type":"session_meta","payload":{"id":"00000000-0000-4000-8000-00000000000d","timestamp":"2026-10-28T02:40:00.000Z","cwd":"C:\work\demo","originator":"codex_cli_rs","cli_version":"0.142.4"}}
{"timestamp":"2026-10-28T02:50:00.000Z","ordinal":12,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":20000,"cached_input_tokens":12000,"output_tokens":3000,"reasoning_output_tokens":800,"total_tokens":23000}},"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":42.0,"window_minutes":300,"resets_at":1793163600},"secondary":{"used_percent":5.0,"window_minutes":10080,"resets_at":1793415600},"credits":{"has_credits":true,"unlimited":false,"balance":"150.0"},"individual_limit":null,"spend_control_reached":null,"plan_type":"pro","rate_limit_reached_type":null}}}
{"timestamp":"2026-10-28T02:55:00.000Z","ordinal":13,"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"gpt_5_3_codex_spark","limit_name":"GPT-5.3-Codex-Spark","primary":{"used_percent":90.0,"window_minutes":300,"resets_at":1793163600},"secondary":{"used_percent":100.0,"window_minutes":10080,"resets_at":1793761200},"credits":null,"individual_limit":null,"spend_control_reached":null,"plan_type":"pro","rate_limit_reached_type":null}}}
{"timestamp":"2026-10-28T02:56:00.000Z","ordinal":14,"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex_other","limit_name":null,"primary":{"used_percent":77.0,"window_minutes":300,"resets_at":1793163600},"secondary":null,"credits":null,"individual_limit":null,"spend_control_reached":null,"plan_type":"pro","rate_limit_reached_type":null}}}
{"timestamp":"2026-10-28T02:56:30.000Z","ordinal":15,"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"token_count is mentioned here"}]}}
```

`tests/AITokenUsage.Core.Tests/Fixtures/Codex/app-server-stdout.jsonl` — 알림 줄, 잡음 줄, 같은 id를 가진 서버 요청 줄이 섞인 stdout.

```jsonl
{"method":"codex/event/session_configured","params":{"msg":{"type":"session_configured"}}}
{"id":1,"result":{"userAgent":"codex_cli_rs/0.142.4 (Windows 10.0.26200; x86_64) ai-token-usage"}}
warning: config.toml uses a deprecated key
{"method":"account/rateLimits/updated","params":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":41,"windowDurationMins":300,"resetsAt":1793163600}}}}
{"id":2,"method":"item/commandExecution/requestApproval","params":{}}
{"id":2,"result":{"rateLimits":{"limitId":"codex","limitName":null,"normalModelSlug":null,"primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1793163600},"secondary":{"usedPercent":5,"windowDurationMins":10080,"resetsAt":1793415600},"credits":{"hasCredits":true,"unlimited":false,"balance":"150.0"},"individualLimit":null,"spendControlReached":null,"planType":"pro","rateLimitReachedType":null},"rateLimitsByLimitId":{"codex":{"limitId":"codex","limitName":null,"normalModelSlug":null,"primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1793163600},"secondary":{"usedPercent":5,"windowDurationMins":10080,"resetsAt":1793415600},"credits":null,"individualLimit":null,"spendControlReached":null,"planType":"pro","rateLimitReachedType":null},"gpt_5_3_codex_spark":{"limitId":"gpt_5_3_codex_spark","limitName":"GPT-5.3-Codex-Spark","normalModelSlug":null,"primary":{"usedPercent":30,"windowDurationMins":300,"resetsAt":1793163600},"secondary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":1793761200},"credits":null,"individualLimit":null,"spendControlReached":null,"planType":"pro","rateLimitReachedType":null}},"ordinaryUsageAllowed":true,"rateLimitResetCredits":{"availableCount":0,"credits":null},"accountId":"acct-test-0001","rateLimitUpsell":null}}
```

- [ ] **Step 2: `CodexRolloutReader` 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexRolloutReaderTests.cs`

```csharp
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexRolloutReaderTests
{
    private const string DayDir = CodexTestData.SessionsDir + @"\2026\10\28";
    private static readonly DateTimeOffset Now = CodexTestData.Now;

    private static (FakeFileSystem Files, CodexRolloutReader Reader) Setup()
    {
        var files = new FakeFileSystem();
        files.AddDirectory(CodexTestData.SessionsDir);
        return (files, new CodexRolloutReader(files));
    }

    private static string RolloutPath(string stamp, string thread = "00000000-0000-4000-8000-000000000001")
        => $@"{DayDir}\rollout-{stamp}-{thread}.jsonl";

    [Fact]
    public void VariantA_FlatFields_NoResetInfo()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T10-00-00"), Fixture.Read("Codex/rollout-a.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2025, 9, 20, 1, 2, 1, TimeSpan.Zero), snapshot.Timestamp);
        Assert.Null(snapshot.PlanType);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(12, snapshot.Meters[0].UsedPercent);
        Assert.Equal(30, snapshot.Meters[1].UsedPercent);
        Assert.All(snapshot.Meters, m => Assert.Null(m.ResetsAt));
    }

    [Fact]
    public void VariantB_RelativeResets_AddedToLineTimestamp_AndNullRateLimitsSkipped()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T10-00-00"), Fixture.Read("Codex/rollout-b.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 2, 30, 0, TimeSpan.Zero), snapshot.Timestamp);
        Assert.Equal(25, snapshot.Meters[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero), snapshot.Meters[0].ResetsAt);
        Assert.Equal("weekly", snapshot.Meters[1].Key);
        Assert.Equal(22, snapshot.Meters[1].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 2, 30, 0, TimeSpan.Zero), snapshot.Meters[1].ResetsAt);
    }

    [Fact]
    public void VariantC_EpochResets_AndPlanType()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("Codex/rollout-c.jsonl"), CodexTestData.FileTime);
        var beforeFiveHourReset = new DateTimeOffset(2026, 10, 28, 1, 0, 0, TimeSpan.Zero);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, beforeFiveHourReset);

        Assert.NotNull(snapshot);
        Assert.Equal("plus", snapshot.PlanType);
        Assert.Equal(64, snapshot.Meters[0].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793152800), snapshot.Meters[0].ResetsAt);
        Assert.Equal(41, snapshot.Meters[1].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793415600), snapshot.Meters[1].ResetsAt);
    }

    [Fact]
    public void ResetAlreadyPassed_ShowsZeroPercent()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("Codex/rollout-c.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now); // 03:00Z, 5h reset was 02:00Z

        Assert.NotNull(snapshot);
        Assert.Equal("5h", snapshot.Meters[0].Key);
        Assert.Equal(0, snapshot.Meters[0].UsedPercent);
        Assert.Null(snapshot.Meters[0].ResetsAt);
        Assert.Equal(41, snapshot.Meters[1].UsedPercent);
    }

    [Fact]
    public void VariantD_SkipsOtherLimitIds()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T11-40-00"), Fixture.Read("Codex/rollout-d.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 2, 50, 0, TimeSpan.Zero), snapshot.Timestamp);
        Assert.Equal("pro", snapshot.PlanType);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(42, snapshot.Meters[0].UsedPercent);
        Assert.Equal(5, snapshot.Meters[1].UsedPercent);
    }

    [Fact]
    public void MissingWindow_OnlyPresentWindowBecomesMeter()
    {
        var (files, reader) = Setup();
        const string line = """{"timestamp":"2026-10-28T02:58:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","limit_name":null,"secondary":{"used_percent":9.0,"window_minutes":10080,"resets_at":1793415600},"credits":null,"plan_type":"plus"}}}""";
        files.AddFile(RolloutPath("2026-10-28T11-50-00"), line + "\n", CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        var meter = Assert.Single(snapshot!.Meters);
        Assert.Equal("weekly", meter.Key);
        Assert.Equal(9, meter.UsedPercent);
    }

    [Fact]
    public void PicksNewestFileByLastWriteTime_NotByName()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T23-59-59"), Fixture.Read("Codex/rollout-c.jsonl"), new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc));
        files.AddFile(RolloutPath("2026-10-28T00-00-01"), Fixture.Read("Codex/rollout-d.jsonl"), new DateTime(2026, 10, 28, 2, 59, 0, DateTimeKind.Utc));

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.Equal("pro", snapshot!.PlanType); // rollout-d content
    }

    [Fact]
    public void FallsThroughToOlderFile_WhenNewestHasNoUsableLine()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("Codex/rollout-c.jsonl"), new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc));
        const string noLimits = """{"timestamp":"2026-10-28T02:59:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":null}}""";
        files.AddFile(RolloutPath("2026-10-28T11-59-00"), noLimits + "\n" + "not json at all\n", new DateTime(2026, 10, 28, 2, 59, 0, DateTimeKind.Utc));

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.Equal("plus", snapshot!.PlanType); // rollout-c content
    }

    [Fact]
    public void IgnoresCompressedRollouts()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("Codex/rollout-c.jsonl"), new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc));
        files.AddFile(RolloutPath("2026-10-28T11-40-00") + ".zst", Fixture.Read("Codex/rollout-d.jsonl"), new DateTime(2026, 10, 28, 2, 59, 0, DateTimeKind.Utc));

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.Equal("plus", snapshot!.PlanType);
    }

    [Fact]
    public void ReturnsNull_WhenSessionsDirectoryMissingOrEmpty()
    {
        var files = new FakeFileSystem();
        var reader = new CodexRolloutReader(files);
        Assert.Null(reader.ReadLatest(CodexTestData.SessionsDir, Now));

        files.AddDirectory(CodexTestData.SessionsDir);
        Assert.Null(reader.ReadLatest(CodexTestData.SessionsDir, Now));
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-10-28T02:00:00.000Z","type":"response_item","payload":{"type":"token_count"}}""")]
    [InlineData("""{"timestamp":"2026-10-28T02:00:00.000Z","type":"event_msg","payload":{"type":"agent_message","message":"\"token_count\""}}""")]
    [InlineData("""{"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":1.0,"window_minutes":300,"resets_at":1793163600}}}}""")]
    [InlineData("""{"timestamp":"2026-10-28T02:00:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":null,"secondary":null}}}""")]
    [InlineData("{\"timestamp\":\"2026-10-28T02:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"")]
    public void ParseLine_RejectsUnusableLines(string line)
    {
        Assert.Null(CodexRolloutReader.ParseLine(line, Now));
    }
}
```

- [ ] **Step 3: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexRolloutReaderTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'CodexRolloutReader' could not be found`

- [ ] **Step 4: `CodexRolloutReader` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexRolloutReader.cs`

```csharp
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>The newest usable token_count rate_limits found in the rollout logs.</summary>
public sealed record CodexRolloutSnapshot(DateTimeOffset Timestamp, string? PlanType, IReadOnlyList<UsageMeter> Meters);

/// <summary>
/// Offline fallback: scans %CODEX_HOME%\sessions\**\rollout-*.jsonl (newest LastWriteTimeUtc first,
/// lines from the end) for the last token_count event whose limit_id is "codex" or absent.
/// Supports rate_limits variants A (v0.40), B (v0.41-0.47), C (v0.48+), D (current). Ignores *.jsonl.zst.
/// </summary>
public sealed class CodexRolloutReader
{
    public const int MaxFilesToScan = 10;

    private readonly IFileSystem _files;

    public CodexRolloutReader(IFileSystem files)
    {
        _files = files;
    }

    public CodexRolloutSnapshot? ReadLatest(string sessionsDirectory, DateTimeOffset now)
    {
        List<string> candidates;
        try
        {
            if (!_files.DirectoryExists(sessionsDirectory)) return null;
            candidates = _files.EnumerateFiles(sessionsDirectory, "*", recursive: true)
                .Where(IsPlainRollout)
                .Select(path => (FilePath: path, LastWrite: _files.GetLastWriteTimeUtc(path)))
                .OrderByDescending(file => file.LastWrite)
                .Take(MaxFilesToScan)
                .Select(file => file.FilePath)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var path in candidates)
        {
            try
            {
                foreach (var line in _files.ReadLinesReverse(path))
                {
                    var snapshot = ParseLine(line, now);
                    if (snapshot is not null) return snapshot;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File vanished or is locked; try the next older one.
            }
        }
        return null;
    }

    /// <summary>Returns a snapshot for a usable codex token_count line, otherwise null.</summary>
    public static CodexRolloutSnapshot? ParseLine(string line, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"token_count\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Json.Str(root, "type") != "event_msg") return null;
            if (Json.Obj(root, "payload") is not { } payload || Json.Str(payload, "type") != "token_count") return null;
            if (Json.Obj(payload, "rate_limits") is not { } limits) return null;

            var limitId = Json.Str(limits, "limit_id");
            if (limitId is not null && limitId != "codex") return null;

            if (CodexAuthReader.ParseTimestamp(Json.Str(root, "timestamp")) is not { } timestamp) return null;

            CodexWindow? primary;
            CodexWindow? secondary;
            if (Json.Num(limits, "primary_used_percent") is not null || Json.Num(limits, "secondary_used_percent") is not null)
            {
                // Variant A: flat fields, no reset information.
                primary = FlatWindow(limits, "primary");
                secondary = FlatWindow(limits, "secondary");
            }
            else
            {
                // Variants B, C, D: nested windows.
                primary = NestedWindow(Json.Obj(limits, "primary"), timestamp, now);
                secondary = NestedWindow(Json.Obj(limits, "secondary"), timestamp, now);
            }

            var meters = CodexWindowLabeler.ToMeters(primary, secondary);
            if (meters.Count == 0) return null;
            var planType = Json.Str(limits, "plan_type");
            return new CodexRolloutSnapshot(timestamp, string.IsNullOrWhiteSpace(planType) ? null : planType, meters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPlainRollout(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private static CodexWindow? FlatWindow(JsonElement limits, string prefix)
    {
        var used = Json.Num(limits, prefix + "_used_percent");
        if (used is null) return null;
        return new CodexWindow(used, ToMinutes(Json.Num(limits, prefix + "_window_minutes")), null);
    }

    private static CodexWindow? NestedWindow(JsonElement? element, DateTimeOffset eventTime, DateTimeOffset now)
    {
        if (element is not { } window) return null;
        var used = Json.Num(window, "used_percent");
        if (used is null) return null;
        var minutes = ToMinutes(Json.Num(window, "window_minutes"));

        DateTimeOffset? resetsAt = null;
        if (Json.Num(window, "resets_at") is double epoch && epoch > 0)
            resetsAt = DateTimeOffset.FromUnixTimeSeconds((long)epoch);                     // C, D
        else if (Json.Num(window, "resets_in_seconds") is double relative && relative >= 0)
            resetsAt = eventTime.AddSeconds(relative);                                     // B

        if (resetsAt is { } reset && reset <= now)
            return new CodexWindow(0, minutes, null); // the window reset since the log line was written
        return new CodexWindow(used, minutes, resetsAt);
    }

    private static long? ToMinutes(double? minutes)
        => minutes is double value && value > 0 ? (long)Math.Round(value) : null;
}
```

- [ ] **Step 5: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexRolloutReaderTests"`

Expected: `Passed!  - Failed: 0, Passed: 15`

- [ ] **Step 6: `CodexAppServerClient` 실패하는 테스트 작성**

시간 초과 테스트는 `FakeTimeProvider`를 1초씩 당기며(사이에 `Task.Delay(10)`으로 비동기 진행 양보) 작업이 끝날 때까지 기다린다. 경과한 가짜 시간으로 초기화 8초·요청 3초 한도를 구분한다.

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexAppServerClientTests.cs`

```csharp
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexAppServerClientTests
{
    private const string NpmShim = @"C:\Users\test\AppData\Roaming\npm\codex.cmd";
    private const string InitializeOk = """{"id":1,"result":{"userAgent":"codex_cli_rs/0.142.4"}}""";

    private readonly FakeTimeProvider _time = new(CodexTestData.Now);
    private readonly FakeProcessRunner _processes = new();
    private readonly List<ProcessSpec> _started = new();

    private CodexAppServerClient CreateClient(FakeInteractiveProcess process)
    {
        _processes.OnInteractive(spec =>
        {
            _started.Add(spec);
            return process;
        });
        return new CodexAppServerClient(_processes, _time, new NullLog());
    }

    private static FakeInteractiveProcess ProcessWith(params string[] lines)
    {
        var process = new FakeInteractiveProcess();
        foreach (var line in lines) process.EnqueueOutput(line);
        return process;
    }

    private async Task<T> AdvanceUntilDone<T>(Task<T> task)
    {
        for (var i = 0; i < 60 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
        return await task;
    }

    [Fact]
    public async Task ReadRateLimits_SkipsNotificationsAndNoise_AndMapsMeters()
    {
        var lines = Fixture.Read("Codex/app-server-stdout.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();
        var process = ProcessWith(lines);
        var client = CreateClient(process);

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Ok, result.Outcome);
        Assert.Equal("pro", result.PlanType);
        Assert.Equal(
            new[] { "5h", "weekly", "gpt_5_3_codex_spark:5h", "gpt_5_3_codex_spark:weekly" },
            result.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(42, result.Meters[0].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793163600), result.Meters[0].ResetsAt);
        Assert.Equal(5, result.Meters[1].UsedPercent);
        Assert.Equal("주·Sp", result.Meters[3].ShortLabel);
    }

    [Fact]
    public async Task ReadRateLimits_WritesNewlineDelimitedJsonRpc_WithoutJsonrpcField()
    {
        var lines = Fixture.Read("Codex/app-server-stdout.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();
        var process = ProcessWith(lines);
        var client = CreateClient(process);

        await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(3, process.Written.Count);
        using var initialize = JsonDocument.Parse(process.Written[0]);
        using var initialized = JsonDocument.Parse(process.Written[1]);
        using var read = JsonDocument.Parse(process.Written[2]);
        Assert.All(process.Written, line => Assert.DoesNotContain("jsonrpc", line));
        Assert.All(process.Written, line => Assert.DoesNotContain("\n", line));

        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("initialize", initialize.RootElement.GetProperty("method").GetString());
        Assert.Equal("ai-token-usage", initialize.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());

        Assert.False(initialized.RootElement.TryGetProperty("id", out _));
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());

        Assert.Equal(2, read.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("account/rateLimits/read", read.RootElement.GetProperty("method").GetString());
        var parameters = read.RootElement.GetProperty("params");
        Assert.True(parameters.GetProperty("excludeResetCreditDetails").GetBoolean());
        Assert.False(parameters.TryGetProperty("supportsLunaReserve", out _));
    }

    [Theory]
    [InlineData(@"C:\Users\test\AppData\Roaming\npm\codex.cmd", "cmd.exe", "/d|/c|codex|-s|read-only|-a|never|app-server")]
    [InlineData(@"C:\Users\test\AppData\Roaming\npm\codex", "cmd.exe", "/d|/c|codex|-s|read-only|-a|never|app-server")]
    [InlineData(@"C:\Tools\codex\codex.exe", @"C:\Tools\codex\codex.exe", "-s|read-only|-a|never|app-server")]
    public void BuildSpec_UsesCmdForShims_AndExeDirectly(string codexPath, string fileName, string arguments)
    {
        var spec = CodexAppServerClient.BuildSpec(codexPath);

        Assert.Equal(fileName, spec.FileName);
        Assert.Equal(arguments.Split('|'), spec.Arguments);
        Assert.Null(spec.EnvironmentOverrides);
    }

    [Fact]
    public async Task ReadRateLimits_StartsTheResolvedSpec()
    {
        var client = CreateClient(ProcessWith(InitializeOk, """{"id":2,"result":{"rateLimits":null}}"""));

        await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        var spec = Assert.Single(_started);
        Assert.Equal("cmd.exe", spec.FileName);
    }

    [Fact]
    public async Task ReadRateLimits_AuthError_IsAuthRequired()
    {
        var client = CreateClient(ProcessWith(
            InitializeOk,
            """{"id":2,"error":{"code":-32600,"message":"codex account authentication required to read rate limits"}}"""));

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.AuthRequired, result.Outcome);
        Assert.Empty(result.Meters);
    }

    [Fact]
    public async Task ReadRateLimits_OtherError_IsFailed()
    {
        var client = CreateClient(ProcessWith(
            InitializeOk,
            """{"id":2,"error":{"code":-32603,"message":"failed to fetch codex rate limits: upstream 502"}}"""));

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ReadRateLimits_InitializeError_IsFailed()
    {
        var client = CreateClient(ProcessWith("""{"id":1,"error":{"code":-32600,"message":"bad clientInfo"}}"""));

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ReadRateLimits_InitializeTimesOutAfterEightSeconds()
    {
        var client = CreateClient(ProcessWith("""{"method":"codex/event/started","params":{}}"""));
        var started = _time.GetUtcNow();

        var result = await AdvanceUntilDone(client.ReadRateLimitsAsync(NpmShim, CancellationToken.None));

        Assert.Equal(CodexAppServerOutcome.TimedOut, result.Outcome);
        Assert.True(_time.GetUtcNow() - started >= CodexAppServerClient.InitializeTimeout);
    }

    [Fact]
    public async Task ReadRateLimits_RequestTimesOutAfterThreeSeconds()
    {
        var client = CreateClient(ProcessWith(InitializeOk));
        var started = _time.GetUtcNow();

        var result = await AdvanceUntilDone(client.ReadRateLimitsAsync(NpmShim, CancellationToken.None));

        Assert.Equal(CodexAppServerOutcome.TimedOut, result.Outcome);
        var elapsed = _time.GetUtcNow() - started;
        Assert.True(elapsed >= CodexAppServerClient.RequestTimeout);
        Assert.True(elapsed < CodexAppServerClient.InitializeTimeout);
    }

    [Fact]
    public async Task ReadRateLimits_StartFailure_IsFailed()
    {
        _processes.OnInteractive(_ => throw new System.ComponentModel.Win32Exception(2));
        var client = new CodexAppServerClient(_processes, _time, new NullLog());

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void MapRateLimits_FallsBackToRateLimits_WhenByLimitIdMissing()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":11,"windowDurationMins":43200,"resetsAt":1795748400},"secondary":null,"planType":"free"},
             "rateLimitsByLimitId":null}
            """);

        var result = CodexAppServerClient.MapRateLimits(document.RootElement);

        Assert.Equal("free", result.PlanType);
        var meter = Assert.Single(result.Meters);
        Assert.Equal("monthly", meter.Key);
        Assert.Equal(11, meter.UsedPercent);
    }
}
```

- [ ] **Step 7: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexAppServerClientTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'CodexAppServerClient' could not be found`

- [ ] **Step 8: `CodexAppServerClient` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexAppServerClient.cs`

```csharp
using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

public enum CodexAppServerOutcome { Ok, AuthRequired, Failed, TimedOut }

public sealed record CodexAppServerResult(CodexAppServerOutcome Outcome, string? PlanType, IReadOnlyList<UsageMeter> Meters)
{
    public static CodexAppServerResult Of(CodexAppServerOutcome outcome) => new(outcome, null, Array.Empty<UsageMeter>());
}

/// <summary>
/// Runs `codex -s read-only -a never app-server` and speaks newline-delimited JSON-RPC (no "jsonrpc" field):
/// initialize (8 s) → initialized → account/rateLimits/read {excludeResetCreditDetails:true} (3 s).
/// The app-server refreshes a stale token itself and saves auth.json under its own lock.
/// </summary>
public sealed class CodexAppServerClient
{
    public const string CommandName = "codex";
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    private const int InitializeId = 1;
    private const int RateLimitsId = 2;
    private const string InitializedNotification = "{\"method\":\"initialized\",\"params\":{}}";
    private const string RateLimitsRequest =
        "{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{\"excludeResetCreditDetails\":true}}";

    private static readonly string[] AppServerArguments = ["-s", "read-only", "-a", "never", "app-server"];

    private static readonly string InitializeRequest =
        "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai-token-usage\",\"title\":\"AITokenUsage\",\"version\":\""
        + (typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0")
        + "\"},\"capabilities\":null}}";

    private readonly IProcessRunner _processes;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;

    public CodexAppServerClient(IProcessRunner processes, TimeProvider time, IAppLog log)
    {
        _processes = processes;
        _time = time;
        _log = log;
    }

    /// <summary>codex.exe/.com runs directly; codex.cmd (npm shim) or anything else runs through cmd.exe /d /c codex.</summary>
    public static ProcessSpec BuildSpec(string codexPath)
    {
        var extension = Path.GetExtension(codexPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
            return new ProcessSpec(codexPath, AppServerArguments, null, ProcessTimeout);
        return new ProcessSpec("cmd.exe", ["/d", "/c", CommandName, .. AppServerArguments], null, ProcessTimeout);
    }

    public async Task<CodexAppServerResult> ReadRateLimitsAsync(string codexPath, CancellationToken ct)
    {
        IInteractiveProcess process;
        try
        {
            process = _processes.StartInteractive(BuildSpec(codexPath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Codex app-server could not be started: " + ex.GetType().Name);
            return CodexAppServerResult.Of(CodexAppServerOutcome.Failed);
        }

        try
        {
            await process.WriteLineAsync(InitializeRequest, ct).ConfigureAwait(false);
            var initialize = await ReadReplyAsync(process, InitializeId, InitializeTimeout, ct).ConfigureAwait(false);
            if (initialize.Outcome is not null) return CodexAppServerResult.Of(initialize.Outcome.Value);

            await process.WriteLineAsync(InitializedNotification, ct).ConfigureAwait(false);
            await process.WriteLineAsync(RateLimitsRequest, ct).ConfigureAwait(false);
            var rateLimits = await ReadReplyAsync(process, RateLimitsId, RequestTimeout, ct).ConfigureAwait(false);
            if (rateLimits.Outcome is not null) return CodexAppServerResult.Of(rateLimits.Outcome.Value);

            return MapRateLimits(rateLimits.Result!.Value);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _log.Warn("Codex app-server I/O failed: " + ex.GetType().Name);
            return CodexAppServerResult.Of(CodexAppServerOutcome.Failed);
        }
        finally
        {
            try
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Codex app-server shutdown failed: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>Maps an account/rateLimits/read result: the "codex" family → main meters, other families → additional meters.</summary>
    public static CodexAppServerResult MapRateLimits(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return CodexAppServerResult.Of(CodexAppServerOutcome.Failed);

        var byLimitId = Json.Obj(result, "rateLimitsByLimitId");
        var main = (byLimitId is { } map ? Json.Obj(map, "codex") : null) ?? Json.Obj(result, "rateLimits");
        var mainId = (main is { } m0 ? Json.Str(m0, "limitId") : null) ?? "codex";

        var meters = new List<UsageMeter>();
        string? planType = null;
        if (main is { } snapshot)
        {
            planType = Json.Str(snapshot, "planType");
            meters.AddRange(CodexWindowLabeler.ToMeters(Window(snapshot, "primary"), Window(snapshot, "secondary")));
        }

        if (byLimitId is { } families)
        {
            foreach (var family in families.EnumerateObject())
            {
                if (family.Name == "codex" || family.Name == mainId || family.Value.ValueKind != JsonValueKind.Object) continue;
                var name = Json.Str(family.Value, "limitName");
                meters.AddRange(CodexWindowLabeler.ToMeters(
                    Window(family.Value, "primary"),
                    Window(family.Value, "secondary"),
                    family.Name,
                    string.IsNullOrWhiteSpace(name) ? family.Name : name));
            }
        }

        return new CodexAppServerResult(
            CodexAppServerOutcome.Ok, string.IsNullOrWhiteSpace(planType) ? null : planType, meters);
    }

    private static CodexWindow? Window(JsonElement snapshot, string name)
    {
        if (Json.Obj(snapshot, name) is not { } window) return null;
        var used = Json.Num(window, "usedPercent");
        if (used is null) return null;
        long? minutes = Json.Num(window, "windowDurationMins") is double mins && mins > 0 ? (long)Math.Round(mins) : null;
        DateTimeOffset? resetsAt = Json.Num(window, "resetsAt") is double epoch && epoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)epoch)
            : null;
        return new CodexWindow(used, minutes, resetsAt);
    }

    private async Task<Reply> ReadReplyAsync(IInteractiveProcess process, int id, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        while (true)
        {
            string? line;
            try
            {
                line = await process.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Reply.Fail(CodexAppServerOutcome.TimedOut);
            }

            if (line is null)
            {
                return Reply.Fail(timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                    ? CodexAppServerOutcome.TimedOut
                    : CodexAppServerOutcome.Failed); // stdout closed: the process exited
            }

            if (TryMatch(line, id) is { } reply) return reply;
        }
    }

    // Lines without our id (notifications, server requests, log noise) are skipped.
    private Reply? TryMatch(string line, int id)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _)) return null;
            if (!root.TryGetProperty("id", out var idElement)) return null;
            var matches = idElement.ValueKind switch
            {
                JsonValueKind.Number => idElement.TryGetInt32(out var number) && number == id,
                JsonValueKind.String => idElement.GetString() == id.ToString(CultureInfo.InvariantCulture),
                _ => false,
            };
            if (!matches) return null;

            if (Json.Obj(root, "error") is { } error)
            {
                var message = Json.Str(error, "message") ?? "";
                _log.Warn($"Codex app-server returned an error for request {id}.");
                return Reply.Fail(message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
                    ? CodexAppServerOutcome.AuthRequired
                    : CodexAppServerOutcome.Failed);
            }

            return root.TryGetProperty("result", out var result)
                ? new Reply(result.Clone(), null)
                : Reply.Fail(CodexAppServerOutcome.Failed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct Reply(JsonElement? Result, CodexAppServerOutcome? Outcome)
    {
        public static Reply Fail(CodexAppServerOutcome outcome) => new(null, outcome);
    }
}
```

- [ ] **Step 9: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexAppServerClientTests"`

Expected: `Passed!  - Failed: 0, Passed: 13`

- [ ] **Step 10: `CodexUsageProvider` 실패하는 테스트 작성**

HTTP 요청 헤더는 `StubHttpHandler.When`의 응답 함수 안에서 사전으로 복사해 둔다(요청 메시지는 공급자가 `using`으로 해제하므로). app-server 실행 횟수는 `OnInteractive` 콜백에서 센다.

`tests/AITokenUsage.Core.Tests/Providers/Codex/CodexUsageProviderTests.cs`

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexUsageProviderTests
{
    private const string NpmShim = @"C:\Users\test\AppData\Roaming\npm\codex.cmd";
    private const string RolloutFile =
        CodexTestData.SessionsDir + @"\2026\10\28\rollout-2026-10-28T11-40-00-00000000-0000-4000-8000-000000000001.jsonl";
    private const string InitializeOk = """{"id":1,"result":{"userAgent":"codex_cli_rs/0.142.4"}}""";
    private const string RateLimitsOk =
        """{"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":77,"windowDurationMins":300,"resetsAt":1793163600},"secondary":{"usedPercent":8,"windowDurationMins":10080,"resetsAt":1793415600},"planType":"plus"},"rateLimitsByLimitId":null}}""";
    private const string RateLimitsFailed =
        """{"id":2,"error":{"code":-32603,"message":"failed to fetch codex rate limits: upstream 502"}}""";
    private const string RateLimitsAuthRequired =
        """{"id":2,"error":{"code":-32600,"message":"chatgpt authentication required to read rate limits"}}""";

    private readonly FakeTimeProvider _time = new(CodexTestData.Now);
    private readonly StubHttpHandler _http = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly List<CapturedRequest> _requests = new();
    private readonly List<string> _startedFileNames = new();

    private sealed record CapturedRequest(string Url, IReadOnlyDictionary<string, string> Headers);

    private CodexUsageProvider CreateProvider() => new(TestContexts.Create(_http, _time, _files, _env, _processes));

    private void GivenAuth(string json)
    {
        _files.AddDirectory(CodexTestData.Home);
        if (_files.FileExists(CodexTestData.AuthPath)) _files.Remove(CodexTestData.AuthPath);
        _files.AddFile(CodexTestData.AuthPath, json, CodexTestData.FileTime);
    }

    private void GivenWham(Func<CapturedRequest, HttpResponseMessage> respond)
        => _http.When(_ => true, request =>
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var captured = new CapturedRequest(request.RequestUri!.AbsoluteUri, headers);
            _requests.Add(captured);
            return respond(captured);
        });

    private void GivenCodexInstalled(params string[] stdoutLines)
    {
        _processes.SetPath("codex", NpmShim);
        _processes.OnInteractive(spec =>
        {
            _startedFileNames.Add(spec.FileName);
            var process = new FakeInteractiveProcess();
            foreach (var line in stdoutLines) process.EnqueueOutput(line);
            return process;
        });
    }

    private void GivenRollout(string fixture)
    {
        _files.AddDirectory(CodexTestData.SessionsDir);
        _files.AddFile(RolloutFile, Fixture.Read(fixture), CodexTestData.FileTime);
    }

    private void UseKoreanTime() => _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string StaleAuth() => CodexTestData.AuthJson(accessToken: CodexTestData.AccessJwt(CodexTestData.Now.AddMinutes(2)));

    [Fact]
    public void Metadata_FollowsSpec()
    {
        _env.Set("CODEX_HOME", @"D:\codex-home");

        var provider = CreateProvider();

        Assert.Equal(ProviderIds.Codex, provider.Id);
        Assert.Equal("Codex", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(1), provider.MinInterval);
        Assert.Equal(new[] { @"D:\codex-home\auth.json" }, provider.WatchPaths);
        Assert.Equal(new[] { "5h", "weekly", "daily", "monthly" }, provider.StaticMeterOptions.Select(o => o.Key).ToArray());
    }

    [Fact]
    public async Task NothingInstalled_IsNotInstalled()
    {
        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Equal("설치 안 됨 — codex 설치 후 codex login", snapshot.StatusMessage);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApiKeyMode_IsUnsupported_WithoutAnyCall(bool hasTokens)
    {
        GivenAuth(CodexTestData.AuthJson(authMode: "apikey", openAiApiKey: "sk-test-key", includeTokens: hasTokens));
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal("API 키 로그인 — 플랜 한도 없음", snapshot.StatusMessage);
        Assert.Empty(_requests);
        Assert.Empty(_startedFileNames);
    }

    [Fact]
    public async Task FreshToken_CallsWham_WithSpecHeaders()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-access-token-without-exp"));
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("Codex/wham-plus.json")));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Plus", snapshot.PlanLabel);
        Assert.Null(snapshot.StatusMessage);
        Assert.Equal(CodexTestData.Now, snapshot.FetchedAt);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(42, snapshot.Meters[0].UsedPercent);

        var request = Assert.Single(_requests);
        Assert.Equal(CodexTestData.DefaultUsageUrl, request.Url);
        Assert.Equal("Bearer test-access-token-without-exp", request.Headers["Authorization"]);
        Assert.Equal(CodexTestData.AccountId, request.Headers["chatgpt-account-id"]);
        Assert.StartsWith("AITokenUsage/", request.Headers["User-Agent"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.False(request.Headers.ContainsKey("X-OpenAI-Fedramp"));
        Assert.False(request.Headers.ContainsKey("x-openai-codex-luna-reserve"));
        Assert.False(request.Headers.ContainsKey("originator"));
        Assert.Empty(_startedFileNames);
    }

    [Fact]
    public async Task FreshToken_FedrampAccount_SendsFedrampHeader()
    {
        GivenAuth(CodexTestData.AuthJson(idToken: CodexTestData.IdJwt(fedramp: true)));
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("Codex/wham-plus.json")));

        await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal("true", Assert.Single(_requests).Headers["X-OpenAI-Fedramp"]);
    }

    [Fact]
    public async Task ConfigTomlBaseUrl_ChangesUsageUrl()
    {
        GivenAuth(CodexTestData.AuthJson());
        _files.AddFile(CodexTestData.ConfigPath, "chatgpt_base_url = \"http://localhost:8080\"\n", CodexTestData.FileTime);
        GivenWham(_ => Json(HttpStatusCode.OK, Fixture.Read("Codex/wham-plus.json")));

        await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal("http://localhost:8080/api/codex/usage", Assert.Single(_requests).Url);
    }

    [Fact]
    public async Task Unauthorized_TokenChangedOnDisk_RetriesOnceWithNewToken()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-old-access-token"));
        GivenWham(request =>
        {
            if (request.Headers["Authorization"] == "Bearer test-old-access-token")
            {
                GivenAuth(CodexTestData.AuthJson(accessToken: "test-new-access-token")); // Codex refreshed meanwhile
                return Json(HttpStatusCode.Unauthorized, "{}");
            }
            return Json(HttpStatusCode.OK, Fixture.Read("Codex/wham-plus.json"));
        });

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, _requests.Count);
        Assert.Equal("Bearer test-new-access-token", _requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Unauthorized_TokenUnchanged_NoFallback_WaitsForCli()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-access-token"));
        GivenWham(_ => Json(HttpStatusCode.Unauthorized, "{}"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.WaitingForCliRefresh, snapshot.Status);
        Assert.Equal("Codex 로그인 갱신 대기 — 터미널에서 codex 실행", snapshot.StatusMessage);
        Assert.Single(_requests); // no retry with the same token
    }

    [Fact]
    public async Task Unauthorized_TokenUnchanged_UsesAppServer()
    {
        GivenAuth(CodexTestData.AuthJson(accessToken: "test-access-token"));
        GivenWham(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(77, snapshot.Meters[0].UsedPercent);
        Assert.Single(_requests);
        Assert.Single(_startedFileNames);
    }

    [Fact]
    public async Task StaleToken_SkipsWham_AndUsesAppServerThroughCmd()
    {
        GivenAuth(StaleAuth());
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Plus", snapshot.PlanLabel);
        Assert.Null(snapshot.StatusMessage);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Empty(_requests);
        Assert.Equal(new[] { "cmd.exe" }, _startedFileNames);
    }

    [Fact]
    public async Task AppServer_RunsAtMostOncePerFifteenMinutes_ThenRolloutFallback()
    {
        UseKoreanTime();
        GivenAuth(StaleAuth());
        GivenCodexInstalled(InitializeOk, RateLimitsFailed);
        GivenRollout("Codex/rollout-d.jsonl");
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));
        await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal("로그 기준 · 11:50", first.StatusMessage);
        Assert.Equal("Pro", first.PlanLabel);
        Assert.Equal(42, first.Meters[0].UsedPercent);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        Assert.Equal(2, _startedFileNames.Count); // t=0 and t=15min, not t=5min
    }

    [Fact]
    public async Task StaleToken_NoCodex_UsesRolloutLogs()
    {
        UseKoreanTime();
        GivenAuth(StaleAuth());
        GivenRollout("Codex/rollout-c.jsonl");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("로그 기준 · 09:40", snapshot.StatusMessage);
        Assert.Equal("Plus", snapshot.PlanLabel);
        Assert.Equal(0, snapshot.Meters[0].UsedPercent); // 5h reset passed at 02:00Z
        Assert.Equal(41, snapshot.Meters[1].UsedPercent);
    }

    [Fact]
    public async Task StaleToken_NoCodex_NoLogs_WaitsForCli()
    {
        GivenAuth(StaleAuth());

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.WaitingForCliRefresh, snapshot.Status);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task NoAuthFile_AppServerSaysLoginRequired_IsSignedOut_EvenWhenThrottled()
    {
        GivenCodexInstalled(InitializeOk, RateLimitsAuthRequired);
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, first.Status);
        Assert.Equal("로그인 필요 — 터미널에서 codex login", first.StatusMessage);
        Assert.Equal(ProviderStatus.SignedOut, second.Status);
        Assert.Single(_startedFileNames);
    }

    [Fact]
    public async Task NoAuthFile_KeyringLogin_ReusesAppServerResultWhileThrottled()
    {
        GivenCodexInstalled(InitializeOk, RateLimitsOk);
        var provider = CreateProvider();

        var first = await provider.FetchAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal(ProviderStatus.Ok, second.Status);
        Assert.Equal(first.Meters, second.Meters);
        Assert.Single(_startedFileNames);
    }

    [Fact]
    public async Task TooManyRequests_IsRateLimited_WithRetryAfter()
    {
        UseKoreanTime();
        GivenAuth(CodexTestData.AuthJson());
        GivenWham(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return response;
        });
        GivenCodexInstalled(InitializeOk, RateLimitsOk);

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(20), snapshot.RetryAfter);
        Assert.Equal("요청 제한 — 12:20 이후 재시도", snapshot.StatusMessage);
        Assert.Empty(_startedFileNames);
    }

    [Fact]
    public async Task ServerError_NoFallback_IsErrorWithStatusCode()
    {
        GivenAuth(CodexTestData.AuthJson());
        GivenWham(_ => Json(HttpStatusCode.ServiceUnavailable, "upstream unavailable"));

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("Codex 사용량을 불러오지 못했습니다 (HTTP 503)", snapshot.StatusMessage);
    }

    [Fact]
    public async Task HtmlBody_IsTransientFailure_AndFallsBackToLogs()
    {
        UseKoreanTime();
        GivenAuth(CodexTestData.AuthJson());
        GivenWham(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Just a moment...</html>", Encoding.UTF8, "text/html"),
        });
        GivenRollout("Codex/rollout-d.jsonl");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("로그 기준 · 11:50", snapshot.StatusMessage);
    }

    [Fact]
    public async Task CorruptAuthFile_IsError_NotSignedOut()
    {
        GivenAuth("{\"auth_mode\":\"chatgpt\",\"tokens\":{");

        var snapshot = await CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("auth.json을 읽지 못했습니다 — 잠시 후 다시 시도", snapshot.StatusMessage);
    }
}
```

- [ ] **Step 11: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexUsageProviderTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'CodexUsageProvider' could not be found`

- [ ] **Step 12: `CodexUsageProvider` 구현**

`src/AITokenUsage.Core/Providers/Codex/CodexUsageProvider.cs`

```csharp
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
```

- [ ] **Step 13: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~CodexUsageProviderTests"`

Expected: `Passed!  - Failed: 0, Passed: 20` (손상된 `auth.json` 테스트는 실제 재시도 지연 때문에 약 0.35초 걸린다)

- [ ] **Step 14: 전체 빌드·테스트와 금지 사항 점검**

Run: `dotnet build AITokenUsage.sln` → Expected: `0 Error(s)`, 새 경고 없음.
Run: `dotnet test AITokenUsage.sln` → Expected: 실패 0. Codex만: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~Providers.Codex"` → `Passed: 171`.

금지 사항이 소스에 없는지 확인한다(luna-reserve 옵트인, 토큰 갱신 엔드포인트, 파일 쓰기).

```powershell
git grep -n -i -E "luna|oauth/token|File\.Write|WriteAllText" -- src/AITokenUsage.Core/Providers/Codex
```

Expected: 출력 없음(종료 코드 1).

- [ ] **Step 15: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Codex tests/AITokenUsage.Core.Tests/Providers/Codex tests/AITokenUsage.Core.Tests/Fixtures/Codex
git commit -m "feat(core): Codex rollout 로그·app-server 대체 경로와 CodexUsageProvider 추가" -m "신선한 토큰은 wham/usage로 조회하고, 401은 auth.json 재판독 후 토큰이 바뀐 경우에만 1회 재시도한다. 그 밖에는 app-server(15분 1회)와 rollout 로그(형식 A–D) 순서로 대체하고 상태별 한국어 메시지를 돌려준다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
