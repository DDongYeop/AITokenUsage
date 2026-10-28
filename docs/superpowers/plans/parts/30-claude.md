## Phase 3 — 공급자

### Task 8: Claude 공급자 (`ClaudePaths`, `ClaudeCredentialReader`, `ClaudeVersionProbe`, `ClaudeUsageParser`, `ClaudeUsageProvider`)

스펙 §5.1과 `docs/research/claude-usage-api.md`를 구현한다. 순서는 경로 → 자격 증명 읽기 → 버전 탐지 → 응답 파서 → 공급자 조합이다. 단계마다 TDD로 진행하고 단계마다 커밋한다.

핵심 규칙 요약(구현 중 계속 확인):
- `.credentials.json`은 **읽기 전용**이다. 쓰지 않고 토큰을 갱신하지도 않는다. 읽을 때는 `IFileSystem.ReadAllTextShared`(FileShare.ReadWrite|Delete)만 쓴다.
- 파싱에 실패하면 50 → 150 → 300ms 간격으로 최대 3회 다시 읽는다. 끝까지 실패해도 `SignedOut`이 아니라 `Error`다.
- `expiresAt − now < 5분`이면 HTTP를 보내지 않고 `WaitingForCliRefresh`를 돌려준다.
- 401 → 파일을 다시 읽어 토큰이 바뀌었으면 한 번만 재시도한다. 안 바뀌었으면 `WaitingForCliRefresh`이고 같은 토큰으로는 1시간 동안 호출하지 않는다. 403 + Anthropic 오류 JSON → `Unsupported`, 1시간 보류(토큰이 바뀌면 해제). 그 밖의 403과 429 → `RateLimited`(`Retry-After`가 0이거나 없으면 5분, 최대 1시간).
- 토큰, `Authorization` 헤더, 응답 원문은 로그·예외 메시지·`ToString()`에 남기지 않는다. 보류 상태는 토큰의 SHA-256 해시만 메모리에 둔다.

**Files:**
- Create: `src/AITokenUsage.Core/Providers/Claude/ClaudePaths.cs`
- Create: `src/AITokenUsage.Core/Providers/Claude/ClaudeCredentials.cs`
- Create: `src/AITokenUsage.Core/Providers/Claude/ClaudeCredentialReader.cs`
- Create: `src/AITokenUsage.Core/Providers/Claude/ClaudeVersionProbe.cs`
- Create: `src/AITokenUsage.Core/Providers/Claude/ClaudeUsageParser.cs`
- Create: `src/AITokenUsage.Core/Providers/Claude/ClaudeUsageProvider.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/credentials.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-live.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-limits-null.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-limits-empty.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-scoped.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-malformed-limits.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-extra-enabled.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/error-429.json`
- Create: `tests/AITokenUsage.Core.Tests/Fixtures/claude/error-403-anthropic.json`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeTestData.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudePathsTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeCredentialReaderTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeVersionProbeTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeUsageParserTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeUsageProviderTests.cs`
- Modify (Step 1에서 규칙이 없을 때만): `tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj`

**Interfaces:**

Consumes (Task 2, Task 3에서 만든 것):
- `AITokenUsage.Core.Models`: `ProviderStatus`, `Severity`, `UsageMeter(string Key, string ShortLabel, string LongLabel, double? UsedPercent, DateTimeOffset? ResetsAt, Severity? ServerSeverity = null, string? TextOverride = null)`, `UsageSnapshot(...)` + `UsageSnapshot.WithStatus(string providerId, ProviderStatus status, DateTimeOffset now, string? message = null, TimeSpan? retryAfter = null)`, `ProviderIds.Claude`, `MeterOption(string Key, string Label)`
- `AITokenUsage.Core.Abstractions`: `IFileSystem.FileExists(string)`, `IFileSystem.ReadAllTextShared(string)`, `IEnvironment.GetVariable(string)`, `IEnvironment.UserProfile`, `ProviderContext(HttpClient Http, TimeProvider Time, IFileSystem Files, IEnvironment Env, IProcessRunner Processes, ICredentialStore Credentials, ISecretStore Secrets, IAppLog Log)`, `IAppLog.Info/Warn`
- `AITokenUsage.Core.Providers.IUsageProvider`
- `AITokenUsage.Core.Util.Json.Str/Num/Bool/Obj/Arr`
- 테스트: `FakeFileSystem.AddFile(path, content, DateTime? lastWriteUtc)`, `FakeFileSystem.Remove(path)`, `FakeEnvironment()`(기본 `UserProfile = @"C:\Users\test"`), `FakeEnvironment.Set(name, value?)`, `StubHttpHandler.When(Func<HttpRequestMessage,bool>, Func<HttpRequestMessage,HttpResponseMessage>)`, `StubHttpHandler.Requests`, `TestContexts.Create(StubHttpHandler, FakeTimeProvider, FakeFileSystem?, FakeEnvironment?, ...)`, `Fixture.Read(string relativePath)`, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`

Produces (`namespace AITokenUsage.Core.Providers.Claude`. 아래는 공개 시그니처 요약이며 컴파일 대상 코드는 각 Step에 있다):
```csharp
public static class ClaudePaths
{
    public const string CredentialsFileName = ".credentials.json";
    public const string SecureStorageDirVariable = "CLAUDE_SECURESTORAGE_CONFIG_DIR";
    public const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";
    public static string DefaultStoreDirectory(IEnvironment env);
    public static string ResolveStoreDirectory(IEnvironment env);
    public static string ResolveCredentialsPath(IEnvironment env);
    public static string VsCodeExtensionsDirectory(IEnvironment env);
}
public enum ClaudeCredentialState { Ok, NotInstalled, SignedOut, MissingProfileScope, Unreadable }
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string>? Scopes,
    string? SubscriptionType, string? RateLimitTier); // ToString() never prints the token
public sealed record ClaudeCredentialReadResult(ClaudeCredentialState State, ClaudeCredentials? Credentials = null);
public sealed class ClaudeCredentialReader
{
    public const string ProfileScope = "user:profile";
    public ClaudeCredentialReader(IFileSystem files, Func<TimeSpan, CancellationToken, Task>? delay = null);
    public Task<ClaudeCredentialReadResult> ReadAsync(string path, CancellationToken ct);
    public static bool TryParse(string text, out ClaudeCredentialReadResult result);
}
public sealed partial class ClaudeVersionProbe
{
    public const string FallbackVersion = "2.1.311";
    public ClaudeVersionProbe(IEnvironment env, Func<string, IEnumerable<string>>? listExtensionDirectories = null,
        Func<IEnumerable<string?>>? runningClaudeVersions = null);
    public string GetVersion();
    public static bool TryParseVersion(string? text, out Version? version);
}
public sealed record ClaudeUsageData(IReadOnlyList<UsageMeter> Meters, string? ExtraLine);
public static partial class ClaudeUsageParser
{
    public const string SessionKey = "session", WeeklyKey = "weekly", ScopedKeyPrefix = "weekly:";
    public const string SessionShortLabel = "5h", SessionLongLabel = "현재 세션 (5시간)";
    public const string WeeklyShortLabel = "주", WeeklyLongLabel = "주간 · 모든 모델";
    public static ClaudeUsageData Parse(string json);                       // JsonException if not a JSON object
    public static string? FormatPlanLabel(string? subscriptionType, string? rateLimitTier);
    public static bool IsAnthropicErrorBody(string? body);
    public static Severity? ParseSeverity(string? value);
    public static DateTimeOffset RoundToMinute(DateTimeOffset value);       // nearest minute, UTC offset
    public static string Slug(string displayName);                           // lower-case, whitespace → "-"
}
public sealed class ClaudeUsageProvider : IUsageProvider
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string OAuthBetaHeader = "oauth-2025-04-20";
    public static readonly TimeSpan RefreshMargin;        // 5 min
    public static readonly TimeSpan AuthRejectedHold;     // 1 h
    public static readonly TimeSpan DefaultRateLimitWait; // 5 min
    public static readonly TimeSpan MaxRateLimitWait;     // 1 h
    public ClaudeUsageProvider(ProviderContext ctx);
    public ClaudeUsageProvider(ProviderContext ctx, ClaudeVersionProbe versionProbe);
    // Id "claude", DisplayName "Claude", MinInterval 2 min, WatchPaths [credentials file],
    // StaticMeterOptions [("session","현재 세션 (5시간)"), ("weekly","주간 · 모든 모델")]
}
```

미터 키·라벨(설정·위젯·카드가 참조):

| 키 | ShortLabel | LongLabel | 출처 |
|---|---|---|---|
| `session` | `5h` | `현재 세션 (5시간)` | `limits[kind=session].percent` → 없으면 `five_hour.utilization` |
| `weekly` | `주` | `주간 · 모든 모델` | `limits[kind=weekly_all].percent` → 없으면 `seven_day.utilization` |
| `weekly:<slug>` | `주·<이름 앞 2글자>` (예: `주·Fa`) | `주간 · <display_name>` (예: `주간 · Fable`) | `limits[kind=weekly_scoped]`의 `scope.model.display_name`(없으면 `scope.surface.display_name`) |
| `weekly:sonnet`, `weekly:opus` | `주·So`, `주·Op` | `주간 · Sonnet`, `주간 · Opus` | 레거시 `seven_day_sonnet`/`seven_day_opus`가 null이 아닐 때 |

`StatusMessage`(사용자에게 보이는 한국어):

| 상황 | Status | StatusMessage |
|---|---|---|
| 파일 없음 | `NotInstalled` | `설치 안 됨 — Claude Code 설치 후 claude 실행해 /login` |
| 빈 토큰, `expiresAt == 0`, `claudeAiOauth` 없음 | `SignedOut` | `터미널에서 claude 실행 후 /login` |
| `scopes`에 `user:profile` 없음, 403 + Anthropic 오류 JSON | `Unsupported` | `이 로그인은 사용량 조회 불가` |
| 만료 5분 이내, 401(토큰 그대로) | `WaitingForCliRefresh` | `Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다` |
| 파싱 재시도 모두 실패 | `Error` | `자격 증명 파일을 읽지 못함 — 잠시 후 다시 시도` |
| 429, 비JSON 403 | `RateLimited` | `요청 제한 — 잠시 후 다시 시도` (`RetryAfter` 설정) |
| 네트워크 오류·타임아웃 | `Error` | `네트워크 오류 — 잠시 후 다시 시도` |
| 그 밖의 HTTP 코드 | `Error` | `서버 오류 (HTTP 500)` 형식 |
| 200인데 본문 파싱 실패 | `Error` | `사용량 응답 형식을 해석하지 못함` |
| 200인데 미터 0개 | `Ok` | `서버가 보고한 한도 없음` |

---

- [ ] **Step 1: 테스트 프로젝트가 Fixtures를 출력 폴더로 복사하는지 확인**

`Fixture.Read`는 `AppContext.BaseDirectory/Fixtures/...`를 읽으므로 샘플 파일이 빌드 출력으로 복사돼야 한다(Task 2에서 넣었어야 하는 규칙이다).

Run (PowerShell): `Select-String -Path tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj -Pattern 'Fixtures'`

Expected: `Fixtures\**\*`에 `CopyToOutputDirectory="PreserveNewest"`를 거는 줄이 1개 이상 출력된다.

출력이 비어 있을 때만 `tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj`의 `</Project>` 바로 위에 다음을 추가한다(SDK가 이미 `None` 항목으로 포함하므로 `Include`가 아니라 `Update`).

```xml
  <ItemGroup>
    <None Update="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: 실패하는 테스트 작성 — `ClaudePaths`**

`tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudePathsTests.cs`:

```csharp
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudePathsTests
{
    [Fact]
    public void Uses_user_profile_dot_claude_when_no_variable_is_set()
    {
        var env = new FakeEnvironment();

        Assert.Equal(Path.Combine(env.UserProfile, ".claude", ".credentials.json"), ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Uses_claude_config_dir_when_set()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");

        Assert.Equal(@"D:\cfg\.credentials.json", ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Ignores_empty_claude_config_dir()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", "");

        Assert.Equal(Path.Combine(env.UserProfile, ".claude", ".credentials.json"), ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Secure_storage_dir_wins_over_claude_config_dir()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");
        env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", @"E:\secure");

        Assert.Equal(@"E:\secure\.credentials.json", ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Defined_but_empty_secure_storage_dir_pins_default_even_with_claude_config_dir()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");
        env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", "");

        Assert.Equal(Path.Combine(env.UserProfile, ".claude", ".credentials.json"), ClaudePaths.ResolveCredentialsPath(env));
    }

    [Fact]
    public void Secure_storage_dir_alone_is_used()
    {
        var env = new FakeEnvironment();
        env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", @"E:\secure");

        Assert.Equal(@"E:\secure", ClaudePaths.ResolveStoreDirectory(env));
    }

    [Fact]
    public void VsCode_extensions_directory_is_under_user_profile()
    {
        var env = new FakeEnvironment();

        Assert.Equal(Path.Combine(env.UserProfile, ".vscode", "extensions"), ClaudePaths.VsCodeExtensionsDirectory(env));
    }
}
```

- [ ] **Step 3: 테스트를 실행해 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudePathsTests"`

Expected: 빌드 실패. `error CS0234: The type or namespace name 'Claude' does not exist in the namespace 'AITokenUsage.Core.Providers'`(또는 `CS0103: The name 'ClaudePaths' does not exist in the current context`).

- [ ] **Step 4: `ClaudePaths` 구현**

`src/AITokenUsage.Core/Providers/Claude/ClaudePaths.cs`:

```csharp
using System.Text;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Resolves Claude Code locations exactly like Claude Code 2.1.311 does
/// (docs/research/claude-usage-api.md §3.1).
/// </summary>
public static class ClaudePaths
{
    public const string CredentialsFileName = ".credentials.json";
    public const string SecureStorageDirVariable = "CLAUDE_SECURESTORAGE_CONFIG_DIR";
    public const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";

    public static string DefaultStoreDirectory(IEnvironment env) => Path.Combine(env.UserProfile, ".claude");

    public static string ResolveStoreDirectory(IEnvironment env)
    {
        string? secure = env.GetVariable(SecureStorageDirVariable);
        if (secure is not null)
        {
            // Defined but empty pins the default store, even when CLAUDE_CONFIG_DIR is set.
            return secure.Length == 0 ? DefaultStoreDirectory(env) : secure.Normalize(NormalizationForm.FormC);
        }

        string? configDir = env.GetVariable(ConfigDirVariable);
        return string.IsNullOrEmpty(configDir)
            ? DefaultStoreDirectory(env)
            : configDir.Normalize(NormalizationForm.FormC);
    }

    public static string ResolveCredentialsPath(IEnvironment env) =>
        Path.Combine(ResolveStoreDirectory(env), CredentialsFileName);

    public static string VsCodeExtensionsDirectory(IEnvironment env) =>
        Path.Combine(env.UserProfile, ".vscode", "extensions");
}
```

- [ ] **Step 5: 테스트를 실행해 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudePathsTests"`

Expected: PASS (7개 통과, 실패 0).

- [ ] **Step 6: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Claude/ClaudePaths.cs tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudePathsTests.cs
git commit -m "feat(core): Claude 자격 증명 경로 해석 추가" -m "CLAUDE_SECURESTORAGE_CONFIG_DIR(정의됐지만 비면 기본 경로) → CLAUDE_CONFIG_DIR → 사용자 폴더의 .claude 순서로 찾는다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

(Step 1에서 csproj를 고쳤다면 `tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj`도 같은 커밋에 `git add` 한다.)

- [ ] **Step 7: 자격 증명 샘플과 테스트 도우미 작성**

`tests/AITokenUsage.Core.Tests/Fixtures/claude/credentials.json` (실제 형태 기반, 값은 모두 가짜. `expiresAt` 1793185200000 = 2026-10-28T11:00:00Z = 테스트 기준 시각 + 8시간. `mcpOAuth`는 알 수 없는 최상위 블록을 무시하는지 확인용):

```json
{
  "claudeAiOauth": {
    "accessToken": "test-access-token",
    "refreshToken": "test-refresh-token",
    "expiresAt": 1793185200000,
    "refreshTokenExpiresAt": 1794241627830,
    "scopes": [
      "user:file_upload",
      "user:inference",
      "user:mcp_servers",
      "user:plugins",
      "user:profile",
      "user:sessions:claude_code"
    ],
    "subscriptionType": "team",
    "subscriptionTypeSource": "sign_in",
    "rateLimitTier": "default_claude_max_5x"
  },
  "mcpOAuth": {
    "test-server|0000": {
      "serverName": "test-server",
      "accessToken": "test-mcp-token",
      "expiresAt": 0
    }
  },
  "organizationUuid": "00000000-0000-4000-8000-000000000001"
}
```

`tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeTestData.cs`:

```csharp
using System.Text.Json.Nodes;

namespace AITokenUsage.Core.Tests.Providers.Claude;

internal static class ClaudeTestData
{
    /// <summary>Fixed test clock start: 2026-10-28T03:00:00Z (epoch ms 1793156400000).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    public const string TokenA = "test-access-token";
    public const string TokenB = "test-access-token-2";

    public static readonly string[] DefaultScopes =
    [
        "user:file_upload",
        "user:inference",
        "user:mcp_servers",
        "user:plugins",
        "user:profile",
        "user:sessions:claude_code",
    ];

    /// <summary>Builds a fake .credentials.json. expiresAtMs defaults to Now + 8 h.</summary>
    public static string Credentials(
        string accessToken = TokenA,
        long? expiresAtMs = null,
        string[]? scopes = null,
        bool includeScopes = true,
        string? subscriptionType = "team",
        string? rateLimitTier = "default_claude_max_5x")
    {
        var oauth = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = "test-refresh-token",
            ["expiresAt"] = expiresAtMs ?? Now.AddHours(8).ToUnixTimeMilliseconds(),
            ["subscriptionType"] = subscriptionType,
            ["rateLimitTier"] = rateLimitTier,
        };

        if (includeScopes)
        {
            oauth["scopes"] = new JsonArray((scopes ?? DefaultScopes).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        var root = new JsonObject
        {
            ["claudeAiOauth"] = oauth,
            ["organizationUuid"] = "00000000-0000-4000-8000-000000000001",
        };
        return root.ToJsonString();
    }
}
```

- [ ] **Step 8: 실패하는 테스트 작성 — `ClaudeCredentialReader`**

`tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeCredentialReaderTests.cs`:

```csharp
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeCredentialReaderTests
{
    private const string CredPath = @"C:\Users\test\.claude\.credentials.json";

    private readonly List<TimeSpan> _delays = [];

    private ClaudeCredentialReader CreateReader(IFileSystem files) =>
        new(files, (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });

    private static FakeFileSystem FilesWith(string content)
    {
        var files = new FakeFileSystem();
        files.AddFile(CredPath, content, null);
        return files;
    }

    [Fact]
    public async Task Reads_valid_credentials_fixture()
    {
        var reader = CreateReader(FilesWith(Fixture.Read("claude/credentials.json")));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.NotNull(result.Credentials);
        Assert.Equal("test-access-token", result.Credentials.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_185_200_000), result.Credentials.ExpiresAt);
        Assert.Equal(ClaudeTestData.Now.AddHours(8), result.Credentials.ExpiresAt);
        Assert.Equal("team", result.Credentials.SubscriptionType);
        Assert.Equal("default_claude_max_5x", result.Credentials.RateLimitTier);
        Assert.NotNull(result.Credentials.Scopes);
        Assert.Contains("user:profile", result.Credentials.Scopes);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Missing_file_is_not_installed()
    {
        ClaudeCredentialReadResult result = await CreateReader(new FakeFileSystem()).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.NotInstalled, result.State);
        Assert.Null(result.Credentials);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_access_token_is_signed_out(string token)
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(accessToken: token)));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.SignedOut, result.State);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Zero_expires_at_is_signed_out()
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(expiresAtMs: 0)));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.SignedOut, result.State);
    }

    [Fact]
    public async Task Missing_claude_oauth_block_is_signed_out()
    {
        const string json = """{"mcpOAuth":{"test-server|0000":{"accessToken":"test-mcp-token"}},"organizationUuid":"00000000-0000-4000-8000-000000000001"}""";
        var reader = CreateReader(FilesWith(json));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.SignedOut, result.State);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Scopes_without_user_profile_cannot_read_usage()
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(scopes: ["user:inference"])));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.MissingProfileScope, result.State);
    }

    [Fact]
    public async Task Absent_scopes_array_is_allowed()
    {
        var reader = CreateReader(FilesWith(ClaudeTestData.Credentials(includeScopes: false)));

        ClaudeCredentialReadResult result = await reader.ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.NotNull(result.Credentials);
        Assert.Null(result.Credentials.Scopes);
    }

    [Fact]
    public async Task Parse_failure_is_retried_and_not_treated_as_logout()
    {
        string valid = ClaudeTestData.Credentials();
        var files = new SequenceFileSystem(() => valid[..20], () => valid);

        ClaudeCredentialReadResult result = await CreateReader(files).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(2, files.ReadCount);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(50) }, _delays);
    }

    [Fact]
    public async Task Persistent_parse_failure_gives_up_after_three_retries_as_unreadable()
    {
        var files = new SequenceFileSystem(() => "");

        ClaudeCredentialReadResult result = await CreateReader(files).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Unreadable, result.State);
        Assert.Null(result.Credentials);
        Assert.Equal(4, files.ReadCount);
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300) },
            _delays);
    }

    [Fact]
    public async Task Io_error_while_reading_is_retried()
    {
        string valid = ClaudeTestData.Credentials();
        var files = new SequenceFileSystem(() => throw new IOException("The process cannot access the file."), () => valid);

        ClaudeCredentialReadResult result = await CreateReader(files).ReadAsync(CredPath, CancellationToken.None);

        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(2, files.ReadCount);
    }

    [Fact]
    public void ToString_never_contains_the_access_token()
    {
        Assert.True(ClaudeCredentialReader.TryParse(ClaudeTestData.Credentials(), out ClaudeCredentialReadResult result));
        Assert.NotNull(result.Credentials);

        Assert.DoesNotContain(ClaudeTestData.TokenA, result.Credentials.ToString());
        Assert.DoesNotContain(ClaudeTestData.TokenA, result.ToString());
    }

    /// <summary>Returns the queued contents one per read; the last entry repeats.</summary>
    private sealed class SequenceFileSystem : IFileSystem
    {
        private readonly Queue<Func<string?>> _reads;

        public SequenceFileSystem(params Func<string?>[] reads)
        {
            _reads = new Queue<Func<string?>>(reads);
        }

        public int ReadCount { get; private set; }

        public bool FileExists(string path) => true;

        public bool DirectoryExists(string path) => true;

        public string? ReadAllTextShared(string path)
        {
            ReadCount++;
            Func<string?> next = _reads.Count > 1 ? _reads.Dequeue() : _reads.Peek();
            return next();
        }

        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
            throw new NotSupportedException();

        public DateTime GetLastWriteTimeUtc(string path) => throw new NotSupportedException();

        public IEnumerable<string> ReadLinesReverse(string path) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 9: 테스트를 실행해 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeCredentialReaderTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'ClaudeCredentialReader' could not be found`, `CS0246 ... 'ClaudeCredentialReadResult'`.

- [ ] **Step 10: 자격 증명 모델 구현**

`src/AITokenUsage.Core/Providers/Claude/ClaudeCredentials.cs`:

```csharp
namespace AITokenUsage.Core.Providers.Claude;

public enum ClaudeCredentialState
{
    Ok,
    NotInstalled,
    SignedOut,
    MissingProfileScope,
    Unreadable,
}

/// <summary>The subset of claudeAiOauth the widget needs. Read-only: never written back.</summary>
public sealed record ClaudeCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string>? Scopes,
    string? SubscriptionType,
    string? RateLimitTier)
{
    // Records print every member by default; never let the token reach a log line.
    public override string ToString() =>
        $"ClaudeCredentials {{ ExpiresAt = {ExpiresAt:O}, SubscriptionType = {SubscriptionType}, RateLimitTier = {RateLimitTier} }}";
}

public sealed record ClaudeCredentialReadResult(ClaudeCredentialState State, ClaudeCredentials? Credentials = null);
```

- [ ] **Step 11: `ClaudeCredentialReader` 구현**

`src/AITokenUsage.Core/Providers/Claude/ClaudeCredentialReader.cs`:

```csharp
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Reads Claude Code's .credentials.json with shared access, retrying parse failures
/// (Claude Code rewrites the file in place with O_TRUNC, so a read can see a partial file).
/// A parse failure is never reported as a logout.
/// </summary>
public sealed class ClaudeCredentialReader
{
    public const string ProfileScope = "user:profile";

    private const long MaxEpochMilliseconds = 253_402_300_799_999; // 9999-12-31T23:59:59.999Z

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
    ];

    private readonly IFileSystem _files;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ClaudeCredentialReader(IFileSystem files, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _files = files;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public async Task<ClaudeCredentialReadResult> ReadAsync(string path, CancellationToken ct)
    {
        if (!_files.FileExists(path))
        {
            return new ClaudeCredentialReadResult(ClaudeCredentialState.NotInstalled);
        }

        for (int attempt = 0; ; attempt++)
        {
            string? text;
            try
            {
                text = _files.ReadAllTextShared(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transient lock (e.g. antivirus scan): retry exactly like a parse failure.
                text = string.Empty;
            }

            if (text is null)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.NotInstalled);
            }

            if (TryParse(text, out ClaudeCredentialReadResult result))
            {
                return result;
            }

            if (attempt >= RetryDelays.Length)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.Unreadable);
            }

            await _delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    /// <summary>False only when the text is not a complete JSON object (caller retries).</summary>
    public static bool TryParse(string text, out ClaudeCredentialReadResult result)
    {
        result = new ClaudeCredentialReadResult(ClaudeCredentialState.Unreadable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            result = Interpret(root);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ClaudeCredentialReadResult Interpret(JsonElement root)
    {
        if (Json.Obj(root, "claudeAiOauth") is not { } oauth)
        {
            return new ClaudeCredentialReadResult(ClaudeCredentialState.SignedOut);
        }

        string? accessToken = Json.Str(oauth, "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new ClaudeCredentialReadResult(ClaudeCredentialState.SignedOut);
        }

        DateTimeOffset? expiresAt = null;
        if (Json.Num(oauth, "expiresAt") is { } expiresAtMs)
        {
            // Claude Code blanks the login to expiresAt 0 after a failed refresh (#91571).
            if (expiresAtMs <= 0)
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.SignedOut);
            }

            if (expiresAtMs <= MaxEpochMilliseconds)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMs);
            }
        }

        IReadOnlyList<string>? scopes = null;
        if (Json.Arr(oauth, "scopes") is { } scopeArray)
        {
            scopes = scopeArray.EnumerateArray()
                .Where(scope => scope.ValueKind == JsonValueKind.String)
                .Select(scope => scope.GetString()!)
                .ToList();

            if (!scopes.Contains(ProfileScope, StringComparer.Ordinal))
            {
                return new ClaudeCredentialReadResult(ClaudeCredentialState.MissingProfileScope);
            }
        }

        var credentials = new ClaudeCredentials(
            accessToken,
            expiresAt,
            scopes,
            Json.Str(oauth, "subscriptionType"),
            Json.Str(oauth, "rateLimitTier"));
        return new ClaudeCredentialReadResult(ClaudeCredentialState.Ok, credentials);
    }
}
```

- [ ] **Step 12: 테스트를 실행해 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeCredentialReaderTests"`

Expected: PASS (12개 통과, 실패 0).

- [ ] **Step 13: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Claude/ClaudeCredentials.cs src/AITokenUsage.Core/Providers/Claude/ClaudeCredentialReader.cs tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeTestData.cs tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeCredentialReaderTests.cs tests/AITokenUsage.Core.Tests/Fixtures/claude/credentials.json
git commit -m "feat(core): Claude 자격 증명 읽기 추가" -m "공유 모드로 읽고 파싱 실패는 50/150/300ms 간격으로 최대 3회 다시 읽는다. 빈 토큰·expiresAt 0은 로그아웃, user:profile 없음은 조회 불가로 판정한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 14: 실패하는 테스트 작성 — `ClaudeVersionProbe`**

`tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeVersionProbeTests.cs`:

```csharp
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeVersionProbeTests
{
    // 2.1.99 sorts above 2.1.350 as text, so this list proves semantic comparison.
    private static readonly string[] ExtensionFolders =
    [
        @"C:\Users\test\.vscode\extensions\anthropic.claude-code-2.1.99-win32-x64",
        @"C:\Users\test\.vscode\extensions\anthropic.claude-code-2.1.350-win32-x64",
        "anthropic.claude-code-2.1.200",
        "ms-dotnettools.csharp-2.50.0-win32-x64",
        "anthropic.claude-code-latest",
    ];

    private static Func<IEnumerable<string?>> Processes(params string?[] versions) => () => versions;

    [Fact]
    public void Picks_highest_semantic_version_from_extension_folders()
    {
        var env = new FakeEnvironment();
        string? listedDirectory = null;
        var probe = new ClaudeVersionProbe(
            env,
            directory =>
            {
                listedDirectory = directory;
                return ExtensionFolders;
            },
            Processes());

        Assert.Equal("2.1.350", probe.GetVersion());
        Assert.Equal(Path.Combine(env.UserProfile, ".vscode", "extensions"), listedDirectory);
    }

    [Fact]
    public void Running_claude_exe_file_version_comes_first()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes("2.1.360.0"));

        Assert.Equal("2.1.360", probe.GetVersion());
    }

    [Fact]
    public void Running_process_is_preferred_even_when_a_folder_is_newer()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes("2.1.200.0"));

        Assert.Equal("2.1.200", probe.GetVersion());
    }

    [Fact]
    public void Highest_running_process_version_is_used()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => Array.Empty<string>(), Processes("2.1.199.0", "2.1.311.0"));

        Assert.Equal("2.1.311", probe.GetVersion());
    }

    [Fact]
    public void Unparseable_process_versions_fall_back_to_folders()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => ExtensionFolders, Processes(null, "", "unknown"));

        Assert.Equal("2.1.350", probe.GetVersion());
    }

    [Fact]
    public void Process_probe_failure_falls_back_to_folders()
    {
        var probe = new ClaudeVersionProbe(
            new FakeEnvironment(),
            _ => ExtensionFolders,
            () => throw new InvalidOperationException("Access is denied."));

        Assert.Equal("2.1.350", probe.GetVersion());
    }

    [Fact]
    public void Folder_listing_failure_falls_back_to_fixed_version()
    {
        var probe = new ClaudeVersionProbe(
            new FakeEnvironment(),
            _ => throw new UnauthorizedAccessException("Access is denied."),
            Processes());

        Assert.Equal(ClaudeVersionProbe.FallbackVersion, probe.GetVersion());
        Assert.Equal("2.1.311", ClaudeVersionProbe.FallbackVersion);
    }

    [Fact]
    public void No_source_returns_fallback()
    {
        var probe = new ClaudeVersionProbe(new FakeEnvironment(), _ => Array.Empty<string>(), Processes());

        Assert.Equal("2.1.311", probe.GetVersion());
    }

    [Theory]
    [InlineData("2.1.311", "2.1.311")]
    [InlineData("2.1.311.0", "2.1.311")]
    [InlineData("2.1.200 (Claude Code)", "2.1.200")]
    [InlineData("v2.2.0", "2.2.0")]
    [InlineData("2.1", null)]
    [InlineData("latest", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_leading_three_part_version(string? text, string? expected)
    {
        bool parsed = ClaudeVersionProbe.TryParseVersion(text, out Version? version);

        Assert.Equal(expected is not null, parsed);
        Assert.Equal(expected, version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}");
    }
}
```

- [ ] **Step 15: 테스트를 실행해 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeVersionProbeTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'ClaudeVersionProbe' could not be found`.

- [ ] **Step 16: `ClaudeVersionProbe` 구현**

기본 프로세스 탐지는 이 PC에서 확인한 사실에 맞춘다. VS Code 확장의 `claude.exe`와 `%USERPROFILE%\.local\bin\claude.exe` 둘 다 버전 리소스가 `ProductName = "Claude Code"`, `FileVersion = "2.1.311.0"` 형식이다. 그래서 `ProductName`에 "Claude"가 있는 것만 믿는다(같은 이름의 다른 exe를 거르기 위해).

`src/AITokenUsage.Core/Providers/Claude/ClaudeVersionProbe.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Picks the version for "User-Agent: claude-code/&lt;ver&gt;":
/// running claude.exe file version, then the highest VS Code extension folder version, then <see cref="FallbackVersion"/>.
/// </summary>
public sealed partial class ClaudeVersionProbe
{
    public const string FallbackVersion = "2.1.311";

    private readonly IEnvironment _env;
    private readonly Func<string, IEnumerable<string>> _listExtensionDirectories;
    private readonly Func<IEnumerable<string?>> _runningClaudeVersions;

    /// <param name="env">Supplies %USERPROFILE%.</param>
    /// <param name="listExtensionDirectories">Given %USERPROFILE%\.vscode\extensions, returns sub-directory names or full paths.</param>
    /// <param name="runningClaudeVersions">Returns raw version strings (e.g. FileVersion "2.1.311.0") of running claude.exe processes.</param>
    public ClaudeVersionProbe(
        IEnvironment env,
        Func<string, IEnumerable<string>>? listExtensionDirectories = null,
        Func<IEnumerable<string?>>? runningClaudeVersions = null)
    {
        _env = env;
        _listExtensionDirectories = listExtensionDirectories ?? ListClaudeExtensionDirectories;
        _runningClaudeVersions = runningClaudeVersions ?? ReadRunningClaudeVersions;
    }

    public string GetVersion()
    {
        Version? best = Highest(Collect(_runningClaudeVersions));
        if (best is null)
        {
            string extensionsDirectory = ClaudePaths.VsCodeExtensionsDirectory(_env);
            best = Highest(Collect(() => _listExtensionDirectories(extensionsDirectory).Select(ExtensionFolderVersion)));
        }

        return best is null ? FallbackVersion : $"{best.Major}.{best.Minor}.{best.Build}";
    }

    /// <summary>Parses the leading "major.minor.patch" of strings like "2.1.311.0" or "2.1.200 (Claude Code)".</summary>
    public static bool TryParseVersion(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = LeadingVersionRegex().Match(text);
        return match.Success && Version.TryParse(match.Groups["ver"].Value, out version);
    }

    private static Version? Highest(IEnumerable<string?> candidates)
    {
        Version? best = null;
        foreach (string? candidate in candidates)
        {
            if (TryParseVersion(candidate, out Version? version) && (best is null || version > best))
            {
                best = version;
            }
        }

        return best;
    }

    private static List<string?> Collect(Func<IEnumerable<string?>> source)
    {
        try
        {
            return source().ToList();
        }
        catch (Exception)
        {
            // Version detection is best-effort; any failure falls through to the next source.
            return [];
        }
    }

    private static string? ExtensionFolderVersion(string entry)
    {
        string name = Path.GetFileName(entry.TrimEnd('\\', '/'));
        Match match = ExtensionFolderRegex().Match(name);
        return match.Success ? match.Groups["ver"].Value : null;
    }

    private static IEnumerable<string> ListClaudeExtensionDirectories(string extensionsDirectory)
    {
        if (!Directory.Exists(extensionsDirectory))
        {
            return [];
        }

        try
        {
            return Directory.GetDirectories(extensionsDirectory, "anthropic.claude-code-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string?> ReadRunningClaudeVersions()
    {
        var versions = new List<string?>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcessesByName("claude"))
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path) || !seenPaths.Add(path))
                    {
                        continue;
                    }

                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                    // Claude Code's native binary reports ProductName "Claude Code" and FileVersion "2.1.311.0".
                    if (info.ProductName?.Contains("Claude", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        versions.Add(info.FileVersion);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException
                                               or UnauthorizedAccessException or IOException)
                {
                    // The process exited or is not accessible; skip it.
                }
            }
        }

        return versions;
    }

    [GeneratedRegex(@"^\s*v?(?<ver>\d{1,9}\.\d{1,9}\.\d{1,9})", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersionRegex();

    [GeneratedRegex(@"^anthropic\.claude-code-(?<ver>\d{1,9}\.\d{1,9}\.\d{1,9})(?:-[A-Za-z0-9_-]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionFolderRegex();
}
```

- [ ] **Step 17: 테스트를 실행해 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeVersionProbeTests"`

Expected: PASS (16개 통과, 실패 0).

- [ ] **Step 18: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Claude/ClaudeVersionProbe.cs tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeVersionProbeTests.cs
git commit -m "feat(core): Claude Code 버전 탐지 추가" -m "User-Agent용 버전을 실행 중인 claude.exe 파일 버전 → VS Code 확장 폴더 최고 버전 → 2.1.311 순서로 고른다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 19: 사용량 응답 샘플 작성**

모양은 `claude-usage-api.md` §2(2026-10-28 실제 응답)와 §2.5 그대로이고 값만 가공했다.

`tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-live.json` (실제 응답 형태 전체, `resets_at`이 1초 정도 흔들린 값 포함):

```json
{
  "five_hour": {
    "utilization": 14,
    "resets_at": "2026-10-28T06:49:59.595952+00:00",
    "limit_dollars": null,
    "used_dollars": null,
    "remaining_dollars": null,
    "locked_reason": null
  },
  "seven_day": {
    "utilization": 50,
    "resets_at": "2026-10-29T20:00:00.478111+00:00",
    "limit_dollars": null,
    "used_dollars": null,
    "remaining_dollars": null,
    "locked_reason": null
  },
  "seven_day_oauth_apps": null,
  "seven_day_opus": null,
  "seven_day_sonnet": null,
  "seven_day_cowork": null,
  "seven_day_omelette": null,
  "tangelo": null,
  "iguana_necktie": null,
  "omelette_promotional": null,
  "nimbus_quill": null,
  "cinder_cove": null,
  "copper_kite": null,
  "brass_thimble": null,
  "harbor_lantern": null,
  "wattle_ember": null,
  "amber_ladder": null,
  "granite_teacup": null,
  "amber_cistern": null,
  "amber_cistern_offer": null,
  "juniper_tide": null,
  "cedar_ember": null,
  "starry_waterfall": null,
  "amber_gauge": null,
  "pace": null,
  "extra_usage": {
    "is_enabled": false,
    "monthly_limit": null,
    "used_credits": null,
    "utilization": null,
    "currency": null,
    "decimal_places": null,
    "disabled_reason": null,
    "user_disabled": false,
    "spend_limit_reached": false,
    "credits_ever_enabled": false,
    "daily": null,
    "weekly": null
  },
  "limits": [
    {
      "kind": "session",
      "group": "session",
      "percent": 14,
      "severity": "normal",
      "resets_at": "2026-10-28T06:49:59.595952+00:00",
      "scope": null,
      "is_active": false
    },
    {
      "kind": "weekly_all",
      "group": "weekly",
      "percent": 50,
      "severity": "normal",
      "resets_at": "2026-10-29T20:00:00.478111+00:00",
      "scope": null,
      "is_active": true
    },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 35,
      "severity": "normal",
      "resets_at": "2026-10-29T20:00:00.478257+00:00",
      "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
      "is_active": false
    }
  ],
  "spend": {
    "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 },
    "limit": null,
    "percent": 0,
    "severity": "normal",
    "enabled": false,
    "disabled_reason": null,
    "cap": null,
    "balance": null,
    "auto_reload": null,
    "disclaimer": "test disclaimer",
    "can_purchase_credits": false,
    "can_toggle": false
  },
  "member_dashboard_available": false,
  "seven_day_breakdown": null,
  "weekly_scoped_shares": [
    { "limit_index": 2, "used_percent_of_weekly": 18, "allowance_percent_of_weekly": 50 }
  ]
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-limits-null.json` (레거시 서버: `limits: null`, 키만 있음):

```json
{
  "five_hour": { "utilization": 26.5, "resets_at": "2026-10-28T08:10:00.120000+00:00" },
  "seven_day": { "utilization": 74.0, "resets_at": "2026-11-01T05:00:00+00:00" },
  "seven_day_oauth_apps": null,
  "seven_day_sonnet": { "utilization": 12, "resets_at": "2026-11-01T05:00:00+00:00" },
  "seven_day_opus": null,
  "extra_usage": null,
  "limits": null
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-limits-empty.json` (`limits: []` → 레거시 창으로 대체):

```json
{
  "five_hour": { "utilization": 5, "resets_at": "2026-10-28T09:00:00+00:00" },
  "seven_day": { "utilization": 20, "resets_at": null },
  "seven_day_sonnet": null,
  "seven_day_opus": null,
  "limits": []
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-scoped.json` (scoped 여러 개, surface 범위, 빈 이름, 대소문자만 다른 중복, 레거시 opus. `five_hour`/`seven_day` 값은 `limits`와 일부러 다르다):

```json
{
  "five_hour": { "utilization": 1, "resets_at": "2026-10-28T06:50:00+00:00" },
  "seven_day": { "utilization": 2, "resets_at": "2026-10-29T20:00:00+00:00" },
  "seven_day_sonnet": null,
  "seven_day_opus": { "utilization": 40, "resets_at": "2026-10-29T20:00:00+00:00" },
  "limits": [
    {
      "kind": "session",
      "group": "session",
      "percent": 62,
      "severity": "warning",
      "resets_at": "2026-10-28T06:50:00.100000+00:00",
      "scope": null,
      "is_active": false
    },
    {
      "kind": "weekly_all",
      "group": "weekly",
      "percent": 91,
      "severity": "critical",
      "resets_at": "2026-10-29T20:00:00.200000+00:00",
      "scope": null,
      "is_active": true
    },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 35,
      "severity": "normal",
      "resets_at": "2026-10-29T20:00:00.300000+00:00",
      "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
      "is_active": false
    },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 12.5,
      "severity": "normal",
      "resets_at": "2026-10-29T20:00:00+00:00",
      "scope": { "model": { "id": "fable-5-1", "display_name": "Fable 5.1" }, "surface": null },
      "is_active": false
    },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 8,
      "severity": "normal",
      "resets_at": null,
      "scope": { "model": null, "surface": { "display_name": "Claude Design" } },
      "is_active": false
    },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 99,
      "severity": "critical",
      "resets_at": null,
      "scope": { "model": { "id": null, "display_name": "" }, "surface": null },
      "is_active": false
    },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 77,
      "severity": "warning",
      "resets_at": null,
      "scope": { "model": { "id": null, "display_name": "fable" }, "surface": null },
      "is_active": false
    }
  ]
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-malformed-limits.json` (잘못된 항목이 정상 항목과 섞임):

```json
{
  "five_hour": { "utilization": 39, "resets_at": "2026-10-28T06:50:00.478089+00:00" },
  "seven_day": { "utilization": 53, "resets_at": "2026-10-29T20:00:00.478111+00:00" },
  "seven_day_sonnet": null,
  "seven_day_opus": null,
  "limits": [
    "unexpected-string-row",
    null,
    42,
    { "kind": 123, "group": "session", "percent": 50, "severity": "normal" },
    { "kind": "session", "group": "session", "percent": "39", "severity": "normal", "resets_at": "2026-10-28T06:50:00+00:00" },
    { "kind": "weekly_all", "group": "weekly", "percent": 54, "severity": "purple", "resets_at": "not-a-date", "scope": null, "is_active": "yes" },
    { "kind": "weekly_scoped", "group": "weekly", "percent": 35, "severity": "normal", "resets_at": null, "scope": "Fable" },
    { "kind": "weekly_scoped", "group": "weekly", "percent": true, "severity": "normal", "resets_at": null, "scope": { "model": { "display_name": "Fable" } } },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 20.5,
      "severity": "warning",
      "resets_at": "2026-10-29T20:00:00+00:00",
      "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
      "is_active": false
    }
  ]
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-extra-enabled.json` (추가 사용량 켜짐, 금액은 센트 단위):

```json
{
  "five_hour": { "utilization": 100, "resets_at": "2026-10-28T06:50:00+00:00" },
  "seven_day": { "utilization": 81, "resets_at": "2026-10-29T20:00:00+00:00" },
  "seven_day_sonnet": null,
  "seven_day_opus": null,
  "extra_usage": {
    "is_enabled": true,
    "monthly_limit": 5000,
    "used_credits": 1240,
    "utilization": 24.8,
    "currency": "USD",
    "decimal_places": 2,
    "disabled_reason": null,
    "user_disabled": false,
    "spend_limit_reached": false,
    "credits_ever_enabled": true,
    "daily": null,
    "weekly": null
  },
  "limits": [
    {
      "kind": "session",
      "group": "session",
      "percent": 100,
      "severity": "critical",
      "resets_at": "2026-10-28T06:50:00+00:00",
      "scope": null,
      "is_active": true
    },
    {
      "kind": "weekly_all",
      "group": "weekly",
      "percent": 81,
      "severity": "warning",
      "resets_at": "2026-10-29T20:00:00+00:00",
      "scope": null,
      "is_active": false
    }
  ]
}
```

`tests/AITokenUsage.Core.Tests/Fixtures/claude/error-429.json`:

```json
{"error":{"message":"Rate limited. Please try again later.","type":"rate_limit_error"}}
```

`tests/AITokenUsage.Core.Tests/Fixtures/claude/error-403-anthropic.json`:

```json
{"type":"error","error":{"type":"permission_error","message":"OAuth token does not meet scope requirement user:profile"}}
```

- [ ] **Step 20: 실패하는 테스트 작성 — `ClaudeUsageParser`**

`tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeUsageParserTests.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeUsageParserTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static UsageMeter Meter(ClaudeUsageData data, string key) => Assert.Single(data.Meters, m => m.Key == key);

    [Fact]
    public void Live_shape_maps_session_weekly_and_scoped_rows()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-live.json"));

        Assert.Equal(new[] { "session", "weekly", "weekly:fable" }, data.Meters.Select(m => m.Key));

        UsageMeter session = data.Meters[0];
        Assert.Equal("5h", session.ShortLabel);
        Assert.Equal("현재 세션 (5시간)", session.LongLabel);
        Assert.Equal(14.0, session.UsedPercent);
        Assert.Equal(Utc(2026, 10, 28, 6, 50), session.ResetsAt);
        Assert.Equal(Severity.Normal, session.ServerSeverity);
        Assert.Null(session.TextOverride);

        UsageMeter weekly = data.Meters[1];
        Assert.Equal("주", weekly.ShortLabel);
        Assert.Equal("주간 · 모든 모델", weekly.LongLabel);
        Assert.Equal(50.0, weekly.UsedPercent);
        Assert.Equal(Utc(2026, 10, 29, 20, 0), weekly.ResetsAt);
        Assert.Equal(Severity.Normal, weekly.ServerSeverity);

        UsageMeter fable = data.Meters[2];
        Assert.Equal("주·Fa", fable.ShortLabel);
        Assert.Equal("주간 · Fable", fable.LongLabel);
        Assert.Equal(35.0, fable.UsedPercent);
        Assert.Equal(Utc(2026, 10, 29, 20, 0), fable.ResetsAt);

        Assert.Null(data.ExtraLine);
    }

    [Fact]
    public void Null_limits_fall_back_to_legacy_window_keys()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-limits-null.json"));

        Assert.Equal(new[] { "session", "weekly", "weekly:sonnet" }, data.Meters.Select(m => m.Key));

        UsageMeter session = Meter(data, "session");
        Assert.Equal(26.5, session.UsedPercent);
        Assert.Equal(Utc(2026, 10, 28, 8, 10), session.ResetsAt);
        Assert.Null(session.ServerSeverity);

        UsageMeter weekly = Meter(data, "weekly");
        Assert.Equal(74.0, weekly.UsedPercent);
        Assert.Equal(Utc(2026, 11, 1, 5, 0), weekly.ResetsAt);

        UsageMeter sonnet = Meter(data, "weekly:sonnet");
        Assert.Equal("주·So", sonnet.ShortLabel);
        Assert.Equal("주간 · Sonnet", sonnet.LongLabel);
        Assert.Equal(12.0, sonnet.UsedPercent);
        Assert.Null(sonnet.ServerSeverity);

        Assert.Null(data.ExtraLine);
    }

    [Fact]
    public void Empty_limits_fall_back_to_legacy_window_keys()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-limits-empty.json"));

        Assert.Equal(new[] { "session", "weekly" }, data.Meters.Select(m => m.Key));
        Assert.Equal(5.0, Meter(data, "session").UsedPercent);
        Assert.Equal(Utc(2026, 10, 28, 9, 0), Meter(data, "session").ResetsAt);
        Assert.Equal(20.0, Meter(data, "weekly").UsedPercent);
        Assert.Null(Meter(data, "weekly").ResetsAt);
    }

    [Fact]
    public void Body_without_any_window_has_no_meters()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(
            """{"five_hour":null,"seven_day":null,"seven_day_sonnet":null,"seven_day_opus":null,"limits":[]}""");

        Assert.Empty(data.Meters);
        Assert.Null(data.ExtraLine);
    }

    [Fact]
    public void Scoped_rows_become_weekly_slug_meters_and_limits_win_over_legacy_keys()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-scoped.json"));

        Assert.Equal(
            new[] { "session", "weekly", "weekly:fable", "weekly:fable-5.1", "weekly:claude-design", "weekly:opus" },
            data.Meters.Select(m => m.Key));

        UsageMeter session = Meter(data, "session");
        Assert.Equal(62.0, session.UsedPercent);
        Assert.Equal(Severity.Warning, session.ServerSeverity);
        Assert.Equal(Utc(2026, 10, 28, 6, 50), session.ResetsAt);

        UsageMeter weekly = Meter(data, "weekly");
        Assert.Equal(91.0, weekly.UsedPercent);
        Assert.Equal(Severity.Critical, weekly.ServerSeverity);

        UsageMeter fable = Meter(data, "weekly:fable");
        Assert.Equal(35.0, fable.UsedPercent);
        Assert.Equal("주·Fa", fable.ShortLabel);
        Assert.Equal("주간 · Fable", fable.LongLabel);
        Assert.Equal(Severity.Normal, fable.ServerSeverity);

        UsageMeter fable51 = Meter(data, "weekly:fable-5.1");
        Assert.Equal(12.5, fable51.UsedPercent);
        Assert.Equal("주·Fa", fable51.ShortLabel);
        Assert.Equal("주간 · Fable 5.1", fable51.LongLabel);

        UsageMeter design = Meter(data, "weekly:claude-design");
        Assert.Equal(8.0, design.UsedPercent);
        Assert.Equal("주·Cl", design.ShortLabel);
        Assert.Equal("주간 · Claude Design", design.LongLabel);
        Assert.Null(design.ResetsAt);

        UsageMeter opus = Meter(data, "weekly:opus");
        Assert.Equal(40.0, opus.UsedPercent);
        Assert.Equal("주·Op", opus.ShortLabel);
        Assert.Equal("주간 · Opus", opus.LongLabel);
        Assert.Null(opus.ServerSeverity);
    }

    [Fact]
    public void Malformed_limits_rows_are_skipped_without_dropping_good_rows()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-malformed-limits.json"));

        Assert.Equal(new[] { "session", "weekly", "weekly:fable" }, data.Meters.Select(m => m.Key));

        // The session row has a string percent, so the legacy five_hour window is used.
        UsageMeter session = Meter(data, "session");
        Assert.Equal(39.0, session.UsedPercent);
        Assert.Null(session.ServerSeverity);
        Assert.Equal(Utc(2026, 10, 28, 6, 50), session.ResetsAt);

        // The weekly_all row is valid; its bad severity and bad date become null individually.
        UsageMeter weekly = Meter(data, "weekly");
        Assert.Equal(54.0, weekly.UsedPercent);
        Assert.Null(weekly.ServerSeverity);
        Assert.Null(weekly.ResetsAt);

        UsageMeter fable = Meter(data, "weekly:fable");
        Assert.Equal(20.5, fable.UsedPercent);
        Assert.Equal(Severity.Warning, fable.ServerSeverity);
        Assert.Equal(Utc(2026, 10, 29, 20, 0), fable.ResetsAt);
    }

    [Fact]
    public void Extra_usage_enabled_is_shown_in_dollars()
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse(Fixture.Read("claude/usage-extra-enabled.json"));

        Assert.Equal("추가 사용 $12.40 / $50.00", data.ExtraLine);
        Assert.Equal(100.0, Meter(data, "session").UsedPercent);
        Assert.Equal(Severity.Critical, Meter(data, "session").ServerSeverity);
    }

    [Theory]
    [InlineData("""{"is_enabled":true,"monthly_limit":100000,"used_credits":0,"currency":null,"decimal_places":null}""", "추가 사용 $0.00 / $1,000.00")]
    [InlineData("""{"is_enabled":true,"monthly_limit":5000,"used_credits":1240,"currency":"EUR","decimal_places":2}""", "추가 사용 12.40 EUR / 50.00 EUR")]
    [InlineData("""{"is_enabled":true,"monthly_limit":null,"used_credits":1240}""", "추가 사용 $12.40")]
    [InlineData("""{"is_enabled":true,"monthly_limit":5000,"used_credits":5000,"currency":"USD","spend_limit_reached":true}""", "추가 사용 $50.00 / $50.00 · 한도 도달")]
    [InlineData("""{"is_enabled":true,"monthly_limit":500,"used_credits":124,"currency":"JPY","decimal_places":0}""", "추가 사용 124 JPY / 500 JPY")]
    public void Extra_usage_converts_minor_units(string extraUsageJson, string expected)
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse("{\"limits\":[],\"extra_usage\":" + extraUsageJson + "}");

        Assert.Equal(expected, data.ExtraLine);
    }

    [Theory]
    [InlineData("""{"is_enabled":false,"monthly_limit":5000,"used_credits":1240}""")]
    [InlineData("""{"monthly_limit":5000,"used_credits":1240}""")]
    [InlineData("null")]
    public void Extra_usage_not_enabled_has_no_line(string extraUsageJson)
    {
        ClaudeUsageData data = ClaudeUsageParser.Parse("{\"limits\":[],\"extra_usage\":" + extraUsageJson + "}");

        Assert.Null(data.ExtraLine);
    }

    [Theory]
    [InlineData("2026-10-28T06:49:59.595952+00:00", "2026-10-28T06:50:00+00:00")]
    [InlineData("2026-10-28T06:50:00.478089+00:00", "2026-10-28T06:50:00+00:00")]
    [InlineData("2026-10-28T06:50:29.999+00:00", "2026-10-28T06:50:00+00:00")]
    [InlineData("2026-10-28T06:50:30+00:00", "2026-10-28T06:51:00+00:00")]
    [InlineData("2026-10-28T15:49:59.9+09:00", "2026-10-28T06:50:00+00:00")]
    public void Resets_at_is_rounded_to_the_nearest_minute(string input, string expected)
    {
        DateTimeOffset rounded = ClaudeUsageParser.RoundToMinute(DateTimeOffset.Parse(input, CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), rounded);
        Assert.Equal(TimeSpan.Zero, rounded.Offset);
    }

    [Theory]
    [InlineData("team", "default_claude_max_5x", "Team · Max 5x")]
    [InlineData("max", "default_claude_max_20x", "Max 20x")]
    [InlineData("pro", "default_claude_pro", "Pro")]
    [InlineData("enterprise", null, "Enterprise")]
    [InlineData(null, "default_claude_max_5x", "Max 5x")]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    public void Plan_label_capitalizes_subscription_and_appends_max_multiplier(string? subscriptionType, string? tier, string? expected)
    {
        Assert.Equal(expected, ClaudeUsageParser.FormatPlanLabel(subscriptionType, tier));
    }

    [Theory]
    [InlineData("normal", Severity.Normal)]
    [InlineData("warning", Severity.Warning)]
    [InlineData("CRITICAL", Severity.Critical)]
    [InlineData("purple", null)]
    [InlineData(null, null)]
    public void Severity_strings_map_to_enum(string? value, Severity? expected)
    {
        Assert.Equal(expected, ClaudeUsageParser.ParseSeverity(value));
    }

    [Theory]
    [InlineData("Fable", "fable")]
    [InlineData("Fable 5.1", "fable-5.1")]
    [InlineData("  Claude   Design ", "claude-design")]
    public void Slug_is_lower_case_with_dashes(string displayName, string expected)
    {
        Assert.Equal(expected, ClaudeUsageParser.Slug(displayName));
    }

    [Fact]
    public void Detects_anthropic_error_bodies()
    {
        Assert.True(ClaudeUsageParser.IsAnthropicErrorBody(Fixture.Read("claude/error-403-anthropic.json")));
        Assert.True(ClaudeUsageParser.IsAnthropicErrorBody(Fixture.Read("claude/error-429.json")));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("<html><body>403 Forbidden</body></html>"));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("error code: 1010"));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody(""));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody(null));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("""{"error":"forbidden"}"""));
        Assert.False(ClaudeUsageParser.IsAnthropicErrorBody("""{"error":{"message":"no type"}}"""));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Non_object_body_throws_json_exception(string body)
    {
        Assert.ThrowsAny<JsonException>(() => ClaudeUsageParser.Parse(body));
    }
}
```

- [ ] **Step 21: 테스트를 실행해 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeUsageParserTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'ClaudeUsageData' could not be found`, `CS0103: The name 'ClaudeUsageParser' does not exist in the current context`.

- [ ] **Step 22: `ClaudeUsageParser` 구현**

`src/AITokenUsage.Core/Providers/Claude/ClaudeUsageParser.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>Meters and the optional extra-usage line parsed from GET /api/oauth/usage.</summary>
public sealed record ClaudeUsageData(IReadOnlyList<UsageMeter> Meters, string? ExtraLine);

/// <summary>
/// Lenient parser for the Claude OAuth usage body (docs/research/claude-usage-api.md §2).
/// limits[] wins, legacy window keys are the fallback, and each limits[] row is parsed in isolation.
/// </summary>
public static partial class ClaudeUsageParser
{
    public const string SessionKey = "session";
    public const string WeeklyKey = "weekly";
    public const string ScopedKeyPrefix = "weekly:";
    public const string SessionShortLabel = "5h";
    public const string SessionLongLabel = "현재 세션 (5시간)";
    public const string WeeklyShortLabel = "주";
    public const string WeeklyLongLabel = "주간 · 모든 모델";

    private const string ScopedShortPrefix = "주·";
    private const string ScopedLongPrefix = "주간 · ";

    private sealed record LimitRow(string Kind, double Percent, DateTimeOffset? ResetsAt, Severity? Severity, string? ScopeName);

    private sealed record Window(double? Utilization, DateTimeOffset? ResetsAt);

    /// <exception cref="JsonException">The body is not JSON or its root is not an object.</exception>
    public static ClaudeUsageData Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Usage body root is not a JSON object.");
        }

        List<LimitRow> rows = ReadLimitRows(root);
        var meters = new List<UsageMeter>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);

        AddHeadline(meters, usedKeys, rows, root, "session", "five_hour", SessionKey, SessionShortLabel, SessionLongLabel);
        AddHeadline(meters, usedKeys, rows, root, "weekly_all", "seven_day", WeeklyKey, WeeklyShortLabel, WeeklyLongLabel);

        foreach (LimitRow row in rows)
        {
            if (row.Kind.Equals("weekly_scoped", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.ScopeName))
            {
                AddScoped(meters, usedKeys, row.ScopeName.Trim(), row.Percent, row.ResetsAt, row.Severity);
            }
        }

        if (ReadWindow(root, "seven_day_sonnet") is { } sonnet)
        {
            AddScoped(meters, usedKeys, "Sonnet", sonnet.Utilization, sonnet.ResetsAt, null);
        }

        if (ReadWindow(root, "seven_day_opus") is { } opus)
        {
            AddScoped(meters, usedKeys, "Opus", opus.Utilization, opus.ResetsAt, null);
        }

        return new ClaudeUsageData(meters, ReadExtraLine(root));
    }

    /// <summary>"team" + "default_claude_max_5x" → "Team · Max 5x"; "max" + "..._max_20x" → "Max 20x".</summary>
    public static string? FormatPlanLabel(string? subscriptionType, string? rateLimitTier)
    {
        string? plan = string.IsNullOrWhiteSpace(subscriptionType) ? null : Capitalize(subscriptionType.Trim());

        string? multiplier = null;
        if (!string.IsNullOrEmpty(rateLimitTier) && MaxTierRegex().Match(rateLimitTier) is { Success: true } match)
        {
            multiplier = "Max " + match.Groups[1].Value.ToLowerInvariant();
        }

        if (plan is null)
        {
            return multiplier;
        }

        if (multiplier is null)
        {
            return plan;
        }

        return plan.Equals("Max", StringComparison.OrdinalIgnoreCase) ? multiplier : plan + " · " + multiplier;
    }

    /// <summary>True for Anthropic-format errors: {"error":{"type":"..."}}.</summary>
    public static bool IsAnthropicErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Json.Obj(root, "error") is { } error
                && !string.IsNullOrEmpty(Json.Str(error, "type"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Severity? ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "normal" => Severity.Normal,
        "warning" => Severity.Warning,
        "critical" => Severity.Critical,
        _ => null,
    };

    /// <summary>resets_at jitters by about 1 s between calls, so round to the nearest minute (UTC).</summary>
    public static DateTimeOffset RoundToMinute(DateTimeOffset value)
    {
        long utcTicks = value.UtcTicks;
        if (utcTicks > DateTimeOffset.MaxValue.UtcTicks - TimeSpan.TicksPerMinute)
        {
            return value.ToUniversalTime();
        }

        long minutes = (utcTicks + TimeSpan.TicksPerMinute / 2) / TimeSpan.TicksPerMinute;
        return new DateTimeOffset(minutes * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    public static string Slug(string displayName) =>
        WhitespaceRegex().Replace(displayName.Trim().ToLowerInvariant(), "-");

    private static void AddHeadline(
        List<UsageMeter> meters,
        HashSet<string> usedKeys,
        List<LimitRow> rows,
        JsonElement root,
        string kind,
        string legacyWindowName,
        string key,
        string shortLabel,
        string longLabel)
    {
        LimitRow? row = rows.Find(r => r.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            meters.Add(new UsageMeter(key, shortLabel, longLabel, row.Percent, row.ResetsAt, row.Severity));
            usedKeys.Add(key);
        }
        else if (ReadWindow(root, legacyWindowName) is { } window)
        {
            meters.Add(new UsageMeter(key, shortLabel, longLabel, window.Utilization, window.ResetsAt));
            usedKeys.Add(key);
        }
    }

    private static void AddScoped(
        List<UsageMeter> meters,
        HashSet<string> usedKeys,
        string name,
        double? percent,
        DateTimeOffset? resetsAt,
        Severity? severity)
    {
        string key = ScopedKeyPrefix + Slug(name);
        if (!usedKeys.Add(key))
        {
            return; // the first row with a given slug wins
        }

        meters.Add(new UsageMeter(
            key,
            ScopedShortPrefix + FirstTwoTextElements(name),
            ScopedLongPrefix + name,
            percent,
            resetsAt,
            severity));
    }

    private static List<LimitRow> ReadLimitRows(JsonElement root)
    {
        var rows = new List<LimitRow>();
        if (Json.Arr(root, "limits") is not { } limits)
        {
            return rows; // null: the server predates limits[]
        }

        foreach (JsonElement entry in limits.EnumerateArray())
        {
            try
            {
                if (TryReadLimitRow(entry) is { } row)
                {
                    rows.Add(row);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or JsonException)
            {
                // One malformed row must not drop its siblings.
            }
        }

        return rows;
    }

    private static LimitRow? TryReadLimitRow(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? kind = Json.Str(entry, "kind");
        double? percent = NumberOnly(entry, "percent");
        if (string.IsNullOrWhiteSpace(kind) || percent is null)
        {
            return null;
        }

        return new LimitRow(
            kind,
            percent.Value,
            ReadResetsAt(entry),
            ParseSeverity(Json.Str(entry, "severity")),
            ReadScopeName(entry));
    }

    private static string? ReadScopeName(JsonElement entry)
    {
        if (Json.Obj(entry, "scope") is not { } scope)
        {
            return null;
        }

        string? modelName = Json.Obj(scope, "model") is { } model ? Json.Str(model, "display_name") : null;
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            return modelName;
        }

        return Json.Obj(scope, "surface") is { } surface ? Json.Str(surface, "display_name") : null;
    }

    private static Window? ReadWindow(JsonElement root, string name) =>
        Json.Obj(root, name) is { } window
            ? new Window(NumberOnly(window, "utilization"), ReadResetsAt(window))
            : null;

    /// <summary>Only JSON numbers count as percentages (strings and booleans are ignored).</summary>
    private static double? NumberOnly(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number)
        && double.IsFinite(number)
            ? number
            : null;

    private static DateTimeOffset? ReadResetsAt(JsonElement obj)
    {
        string? text = Json.Str(obj, "resets_at");
        return text is not null
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
                ? RoundToMinute(value)
                : null;
    }

    /// <summary>extra_usage amounts are minor units of currency; divide by 10^decimal_places (default 100).</summary>
    private static string? ReadExtraLine(JsonElement root)
    {
        if (Json.Obj(root, "extra_usage") is not { } extra || Json.Bool(extra, "is_enabled") != true)
        {
            return null;
        }

        int decimals = 2;
        if (NumberOnly(extra, "decimal_places") is { } places && places >= 0 && places <= 6 && places == Math.Floor(places))
        {
            decimals = (int)places;
        }

        double divisor = Math.Pow(10, decimals);
        string? rawCurrency = Json.Str(extra, "currency");
        string currency = string.IsNullOrWhiteSpace(rawCurrency) ? "USD" : rawCurrency.Trim().ToUpperInvariant();

        double used = (NumberOnly(extra, "used_credits") ?? 0) / divisor;
        string line = "추가 사용 " + Money(used, currency, decimals);

        if (NumberOnly(extra, "monthly_limit") is { } limitMinor)
        {
            line += " / " + Money(limitMinor / divisor, currency, decimals);
        }

        if (Json.Bool(extra, "spend_limit_reached") == true)
        {
            line += " · 한도 도달";
        }

        return line;
    }

    private static string Money(double amount, string currency, int decimals)
    {
        string number = amount.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return currency == "USD" ? "$" + number : number + " " + currency;
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static string FirstTwoTextElements(string name)
    {
        var info = new StringInfo(name);
        return info.LengthInTextElements <= 2 ? name : info.SubstringByTextElements(0, 2);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"max_(\d+x)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaxTierRegex();
}
```

- [ ] **Step 23: 테스트를 실행해 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeUsageParserTests"`

Expected: PASS (39개 통과, 실패 0).

- [ ] **Step 24: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Claude/ClaudeUsageParser.cs tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeUsageParserTests.cs tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-live.json tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-limits-null.json tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-limits-empty.json tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-scoped.json tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-malformed-limits.json tests/AITokenUsage.Core.Tests/Fixtures/claude/usage-extra-enabled.json tests/AITokenUsage.Core.Tests/Fixtures/claude/error-429.json tests/AITokenUsage.Core.Tests/Fixtures/claude/error-403-anthropic.json
git commit -m "feat(core): Claude 사용량 응답 파서 추가" -m "limits[]를 우선하고 없으면 five_hour/seven_day로 대체한다. 항목별로 실패를 격리하고 resets_at은 분 단위로 반올림하며, 추가 사용량(센트→달러)과 플랜 라벨을 만든다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 25: 실패하는 테스트 작성 — `ClaudeUsageProvider`**

`tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeUsageProviderTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeUsageProviderTests
{
    private sealed record SentRequest(
        HttpMethod Method,
        string Url,
        string Query,
        string? Authorization,
        string? Beta,
        string UserAgent,
        string Accept);

    /// <summary>One provider world: fake clock, files, env and a scripted HTTP queue.</summary>
    private sealed class Harness
    {
        private bool _credentialsWritten;

        public Harness()
        {
            Http.When(_ => true, request =>
            {
                Sent.Add(new SentRequest(
                    request.Method,
                    request.RequestUri!.GetLeftPart(UriPartial.Path),
                    request.RequestUri.Query,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? beta) ? string.Join(",", beta) : null,
                    request.Headers.UserAgent.ToString(),
                    request.Headers.Accept.ToString()));
                return Responses.Dequeue()();
            });
        }

        public FakeTimeProvider Time { get; } = new(ClaudeTestData.Now);

        public FakeFileSystem Files { get; } = new();

        public FakeEnvironment Env { get; } = new();

        public StubHttpHandler Http { get; } = new();

        public List<SentRequest> Sent { get; } = [];

        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();

        public string CredentialsPath => Path.Combine(Env.UserProfile, ".claude", ".credentials.json");

        public void WriteCredentials(string json)
        {
            if (_credentialsWritten)
            {
                Files.Remove(CredentialsPath);
            }

            Files.AddFile(CredentialsPath, json, Time.GetUtcNow().UtcDateTime);
            _credentialsWritten = true;
        }

        public ClaudeUsageProvider CreateProvider() => new(
            TestContexts.Create(Http, Time, Files, Env),
            new ClaudeVersionProbe(
                Env,
                _ => new[] { "anthropic.claude-code-2.1.400-win32-x64" },
                () => Array.Empty<string?>()));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Usage() => JsonResponse(HttpStatusCode.OK, Fixture.Read("claude/usage-live.json"));

    [Fact]
    public void Exposes_id_interval_watch_path_and_static_meters()
    {
        var h = new Harness();
        ClaudeUsageProvider provider = h.CreateProvider();

        Assert.Equal("claude", provider.Id);
        Assert.Equal("Claude", provider.DisplayName);
        Assert.Equal(TimeSpan.FromMinutes(2), provider.MinInterval);
        Assert.Equal(new[] { h.CredentialsPath }, provider.WatchPaths);
        Assert.Equal(new[] { "session", "weekly" }, provider.StaticMeterOptions.Select(o => o.Key));
        Assert.Equal(new[] { "현재 세션 (5시간)", "주간 · 모든 모델" }, provider.StaticMeterOptions.Select(o => o.Label));
    }

    [Fact]
    public void Watch_path_follows_secure_storage_override()
    {
        var h = new Harness();
        h.Env.Set("CLAUDE_CONFIG_DIR", @"D:\cfg");
        h.Env.Set("CLAUDE_SECURESTORAGE_CONFIG_DIR", @"E:\secure");

        Assert.Equal(new[] { @"E:\secure\.credentials.json" }, h.CreateProvider().WatchPaths);
    }

    [Fact]
    public async Task Missing_credentials_file_is_not_installed_without_http()
    {
        var h = new Harness();

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotInstalled, snapshot.Status);
        Assert.Empty(snapshot.Meters);
        Assert.Empty(h.Http.Requests);
    }

    [Theory]
    [InlineData("", 1_793_185_200_000L)]
    [InlineData("test-access-token", 0L)]
    public async Task Empty_token_or_zero_expiry_is_signed_out_without_http(string token, long expiresAtMs)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(accessToken: token, expiresAtMs: expiresAtMs));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.SignedOut, snapshot.Status);
        Assert.Equal("터미널에서 claude 실행 후 /login", snapshot.StatusMessage);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Missing_user_profile_scope_is_unsupported_without_http()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(scopes: ["user:inference"]));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
        Assert.Equal("이 로그인은 사용량 조회 불가", snapshot.StatusMessage);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Unparseable_credentials_are_an_error_not_a_logout()
    {
        var h = new Harness();
        h.WriteCredentials("{\"claudeAiOauth\": {\"accessTo");

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Empty(h.Http.Requests);
    }

    [Theory]
    [InlineData(299)]
    [InlineData(0)]
    [InlineData(-3600)]
    public async Task Token_expiring_within_five_minutes_waits_for_cli_without_any_http(int secondsLeft)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(
            expiresAtMs: ClaudeTestData.Now.AddSeconds(secondsLeft).ToUnixTimeMilliseconds()));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.WaitingForCliRefresh, snapshot.Status);
        Assert.Equal("Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다", snapshot.StatusMessage);
        Assert.Equal("Team · Max 5x", snapshot.PlanLabel);
        Assert.Empty(h.Http.Requests);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Token_with_more_than_five_minutes_left_is_used()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials(expiresAtMs: ClaudeTestData.Now.AddSeconds(301).ToUnixTimeMilliseconds()));
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task Ok_response_maps_meters_and_plan_and_sends_expected_request()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("claude", snapshot.ProviderId);
        Assert.Equal("Team · Max 5x", snapshot.PlanLabel);
        Assert.Equal(ClaudeTestData.Now, snapshot.FetchedAt);
        Assert.Null(snapshot.StatusMessage);
        Assert.Null(snapshot.RetryAfter);
        Assert.Null(snapshot.ExtraLine);
        Assert.Equal(new[] { "session", "weekly", "weekly:fable" }, snapshot.Meters.Select(m => m.Key));
        Assert.Equal(14.0, snapshot.Meters[0].UsedPercent);

        SentRequest sent = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", sent.Url);
        Assert.Equal("", sent.Query);
        Assert.Equal("Bearer test-access-token", sent.Authorization);
        Assert.Equal("oauth-2025-04-20", sent.Beta);
        Assert.Equal("claude-code/2.1.400", sent.UserAgent);
        Assert.Equal("application/json", sent.Accept);
    }

    [Fact]
    public async Task Extra_usage_line_is_passed_through()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.OK, Fixture.Read("claude/usage-extra-enabled.json")));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal("추가 사용 $12.40 / $50.00", snapshot.ExtraLine);
    }

    [Fact]
    public async Task Ok_response_without_meters_reports_message()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.OK, """{"five_hour":null,"seven_day":null,"limits":[]}"""));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Empty(snapshot.Meters);
        Assert.Equal("서버가 보고한 한도 없음", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Unauthorized_with_unchanged_token_waits_and_holds_that_token_for_one_hour()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        ClaudeUsageProvider provider = h.CreateProvider();
        h.Responses.Enqueue(() => JsonResponse(
            HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid token"}}"""));

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, first.Status);
        Assert.Single(h.Sent);

        h.Time.Advance(TimeSpan.FromMinutes(59));
        UsageSnapshot held = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, held.Status);
        Assert.Single(h.Sent);

        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Responses.Enqueue(Usage);
        UsageSnapshot afterHold = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, afterHold.Status);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Unauthorized_then_changed_token_on_reread_retries_once_with_new_token()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() =>
        {
            // Claude Code refreshed the login while our request was in flight.
            h.WriteCredentials(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
            return JsonResponse(HttpStatusCode.Unauthorized, "{}");
        });
        h.Responses.Enqueue(Usage);

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("Bearer test-access-token", h.Sent[0].Authorization);
        Assert.Equal("Bearer test-access-token-2", h.Sent[1].Authorization);
    }

    [Fact]
    public async Task Unauthorized_retry_happens_only_once()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        ClaudeUsageProvider provider = h.CreateProvider();
        h.Responses.Enqueue(() =>
        {
            h.WriteCredentials(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
            return JsonResponse(HttpStatusCode.Unauthorized, "{}");
        });
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.Unauthorized, "{}"));

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, first.Status);
        Assert.Equal(2, h.Sent.Count);

        UsageSnapshot second = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, second.Status);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Forbidden_with_anthropic_error_is_unsupported_until_token_changes()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        ClaudeUsageProvider provider = h.CreateProvider();
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.Forbidden, Fixture.Read("claude/error-403-anthropic.json")));

        UsageSnapshot first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Unsupported, first.Status);
        Assert.Equal("이 로그인은 사용량 조회 불가", first.StatusMessage);

        h.Time.Advance(TimeSpan.FromMinutes(30));
        UsageSnapshot held = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Unsupported, held.Status);
        Assert.Single(h.Sent);

        h.WriteCredentials(ClaudeTestData.Credentials(accessToken: ClaudeTestData.TokenB));
        h.Responses.Enqueue(Usage);
        UsageSnapshot afterChange = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderStatus.Ok, afterChange.Status);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("Bearer test-access-token-2", h.Sent[1].Authorization);
    }

    [Theory]
    [InlineData("<html><body>403 Forbidden</body></html>")]
    [InlineData("error code: 1010")]
    [InlineData("")]
    [InlineData("""{"message":"forbidden"}""")]
    public async Task Forbidden_without_anthropic_error_is_rate_limited_for_five_minutes(string body)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        });

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.RetryAfter);
        Assert.Equal("Team · Max 5x", snapshot.PlanLabel);
    }

    [Theory]
    [InlineData(1200, 1200)]
    [InlineData(0, 300)]
    [InlineData(null, 300)]
    [InlineData(7200, 3600)]
    public async Task Too_many_requests_honors_retry_after(int? retryAfterSeconds, int expectedSeconds)
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() =>
        {
            HttpResponseMessage response = JsonResponse(HttpStatusCode.TooManyRequests, Fixture.Read("claude/error-429.json"));
            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        });

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), snapshot.RetryAfter);
        Assert.Equal("요청 제한 — 잠시 후 다시 시도", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Too_many_requests_honors_retry_after_http_date()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() =>
        {
            HttpResponseMessage response = JsonResponse(HttpStatusCode.TooManyRequests, Fixture.Read("claude/error-429.json"));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(ClaudeTestData.Now.AddMinutes(12));
            return response;
        });

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(12), snapshot.RetryAfter);
    }

    [Fact]
    public async Task Server_error_is_error_with_status_code()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.InternalServerError, "{}"));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("서버 오류 (HTTP 500)", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Network_failure_is_error()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => throw new HttpRequestException("No connection could be made."));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("네트워크 오류 — 잠시 후 다시 시도", snapshot.StatusMessage);
    }

    [Fact]
    public async Task Unparseable_ok_body_is_error()
    {
        var h = new Harness();
        h.WriteCredentials(ClaudeTestData.Credentials());
        h.Responses.Enqueue(() => JsonResponse(HttpStatusCode.OK, "<html>maintenance</html>"));

        UsageSnapshot snapshot = await h.CreateProvider().FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Equal("사용량 응답 형식을 해석하지 못함", snapshot.StatusMessage);
    }
}
```

- [ ] **Step 26: 테스트를 실행해 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeUsageProviderTests"`

Expected: 빌드 실패. `error CS0246: The type or namespace name 'ClaudeUsageProvider' could not be found`.

- [ ] **Step 27: `ClaudeUsageProvider` 구현**

`src/AITokenUsage.Core/Providers/Claude/ClaudeUsageProvider.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;

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

    private const string MsgNotInstalled = "설치 안 됨 — Claude Code 설치 후 claude 실행해 /login";
    private const string MsgSignedOut = "터미널에서 claude 실행 후 /login";
    private const string MsgNoUsageAccess = "이 로그인은 사용량 조회 불가";
    private const string MsgWaitingForCli = "Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다";
    private const string MsgUnreadable = "자격 증명 파일을 읽지 못함 — 잠시 후 다시 시도";
    private const string MsgRateLimited = "요청 제한 — 잠시 후 다시 시도";
    private const string MsgNetwork = "네트워크 오류 — 잠시 후 다시 시도";
    private const string MsgBadBody = "사용량 응답 형식을 해석하지 못함";
    private const string MsgNoMeters = "서버가 보고한 한도 없음";

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

    public IReadOnlyList<MeterOption> StaticMeterOptions { get; } =
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
                    return Status(ProviderStatus.Error, $"서버 오류 (HTTP {code})", plan);
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
```

- [ ] **Step 28: 테스트를 실행해 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~ClaudeUsageProviderTests"`

Expected: PASS (30개 통과, 실패 0). `Unparseable_credentials_are_an_error_not_a_logout`는 실제 재시도 대기(50+150+300ms) 때문에 약 0.5초 걸린다.

- [ ] **Step 29: 전체 빌드·테스트로 회귀 확인**

Run: `dotnet build AITokenUsage.sln`

Expected: `Build succeeded`, 오류 0개(`0 Error(s)`), 새 경고 없음.

Run: `dotnet test AITokenUsage.sln`

Expected: 실패 0. Claude 관련 테스트 104개(Paths 7, CredentialReader 12, VersionProbe 16, Parser 39, Provider 30)를 포함해 이전 Task의 테스트도 모두 통과한다.

추가 점검(눈으로 확인):
- `src/AITokenUsage.Core/Providers/Claude/` 안에 `File.Write`, `WriteAllText`, `FileMode.Create` 같은 쓰기 호출이 없어야 한다. Run (PowerShell): `Select-String -Path src/AITokenUsage.Core/Providers/Claude/*.cs -Pattern 'Write(All)?Text|FileMode\.(Create|Truncate|Append)|File\.(Replace|Move|Delete)'` → 출력 없음.
- 로그 호출에 토큰·본문 변수가 들어가지 않아야 한다. Run: `Select-String -Path src/AITokenUsage.Core/Providers/Claude/*.cs -Pattern '_ctx\.Log\.'` → 출력된 줄의 인자가 모두 고정 문자열이거나 `{code}`만 들어 있는지 확인한다.

- [ ] **Step 30: 커밋**

```bash
git add src/AITokenUsage.Core/Providers/Claude/ClaudeUsageProvider.cs tests/AITokenUsage.Core.Tests/Providers/Claude/ClaudeUsageProviderTests.cs
git commit -m "feat(core): Claude 사용량 공급자 추가" -m "만료 5분 이내면 호출하지 않고 CLI 갱신을 기다린다. 401은 파일을 다시 읽어 토큰이 바뀐 경우에만 한 번 재시도하고, 거부된 토큰은 1시간 보류한다. 403/429와 Retry-After를 처리한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

실제 API 1회 호출 확인(스펙 §7.2)은 조회 결과를 볼 화면이 생기는 Task 14(트레이 툴팁에 Claude 값)와 Task 20 최종 점검에서 한다. 이 Task에서는 실제 엔드포인트를 호출하지 않는다(429 버킷 보호).
