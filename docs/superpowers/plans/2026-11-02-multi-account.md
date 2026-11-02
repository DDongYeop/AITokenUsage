# Claude·Codex 여러 계정 표시 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**목표**: 기본 CLI 계정 외에 위젯 전용으로 로그인한 Claude·Codex 계정을 따로 보여 준다. 같은 종류가 둘 이상이면 팀·개인 태그를 붙인다.

**구조**
- 추가 계정은 패널 ID가 `<종류>#<키>`인 공급자 인스턴스다.
- Claude 추가 계정은 `ClaudeUsageProvider`에 "앱 소유 로그인 출처"를 넣어 만든다. 로그인 정보는 DPAPI에 두고, 앱이 직접 갱신한다.
- Codex 추가 계정은 `CodexUsageProvider`에 전용 `CODEX_HOME`을 넣어 만든다. 갱신은 `codex app-server`에 맡긴다.
- 태그 계산, 로그인 명령, 정리 대상 계산은 Core의 순수 코드로 만든다. 로그인 진행, 설정 화면, 재시작은 App이 맡는다.

**기술 스택**: C#/.NET 10, WPF, xUnit 2.9.3

**설계 문서**: `docs/superpowers/specs/2026-11-02-multi-account-design.md`(이 문서가 기준이다). 이전 설계(v1.0, v1.1, PC 사용량)의 규칙도 그대로 따른다.

## 전체 제약
- 빌드 경고 0. `dotnet build AITokenUsage.sln`과 `dotnet test AITokenUsage.sln`이 통과해야 한다.
- 새 사용자 문구는 리소스에 둔다.
  - Core 문구: `Strings.resx`(영어)와 `Strings.ko.resx`(한국어)
  - App 문구: `UiStrings.resx`와 `UiStrings.ko.resx`
  - 두 언어의 키가 같아야 한다(`ResourceCompletenessTests`).
- 테스트 기본 문화권은 ko-KR이고, 영어 테스트는 `[UseCulture("en-US")]`를 쓴다.
- **보안 규칙**
  - 토큰, 요청·응답 본문, 인증 헤더는 로그·캐시·화면·예외 메시지 어디에도 남기지 않는다. 토큰을 가진 레코드는 `ToString`을 재정의해서 토큰을 뺀다.
  - CLI 기본 로그인 파일(`~/.claude/.credentials.json`, `~/.codex/auth.json`, `~/.gemini/oauth_creds.json`)은 절대 쓰거나 지우지 않는다. 기본 계정의 Claude·Codex 토큰도 절대 갱신하지 않는다.
  - 앱이 갱신해도 되는 것은 Claude **추가 계정**(앱 소유 체인)뿐이다. Codex 토큰은 어떤 경우에도 앱이 갱신하지 않는다.
  - 파일은 `FileShare.ReadWrite | FileShare.Delete`로 읽는다(`IFileSystem.ReadAllTextShared`).
- `AppSettings.CurrentVersion`은 1을 유지하고, 설정 키는 추가만 한다.
- UI 스레드에서 파일·네트워크 I/O를 하지 않는다.
- 실제 입력(마우스·키보드 주입)을 쓰지 않는다. 화면 확인은 PrintWindow로만 한다.
- 사용자 앱(`C:\Users\kyung\AppData\Local\Programs\AITokenUsage\AITokenUsage.exe`)을 멈췄으면 끝나고 다시 실행한다. `settings.json`, `cache.json`, HKCU Run 값 `AITokenUsage`는 백업했다가 바이트 단위로 그대로 복원한다.
- 기존 코드 스타일을 따른다: file-scoped namespace, `sealed`, XML doc 주석 밀도, `ArgumentNullException.ThrowIfNull`
- **구현 담당은 커밋하지 않는다.** 커밋은 Task가 끝날 때마다 컨트롤러가 파일을 지정해서 한다. 추적하지 않는 `docs/superpowers/specs/2026-10-29-cross-platform-core-design.md`는 절대 스테이징하지 않는다.

---

### Task 1: Core: 계정 ID, 설정, 종류별 분기, 태그, 로그인 명령, 정리 대상

**Files**
- 새로 만들기 (`src/AITokenUsage.Core/Accounts/`): `AccountIds.cs`, `AccountStorage.cs`, `AccountLoginCommand.cs`
- 새로 만들기 (`src/AITokenUsage.Core/Presentation/`): `PlanCategory.cs`, `AccountTags.cs`
- 수정
  - `Models/ProviderIds.cs`(`KindOf`), `Models/ProviderView.cs`
  - `Settings/AppSettings.cs`, `Settings/ProviderDefaults.cs`
  - `Presentation/WidgetPanelBuilder.cs`, `DetailCardBuilder.cs`, `ProviderStatusText.cs`, `ConnectionGuideBuilder.cs`
  - `Util/TrayTextFormatter.cs`
  - `Resources/Strings.resx`, `Strings.ko.resx`
- 테스트: `tests/AITokenUsage.Core.Tests/Accounts/*Tests.cs`, `Presentation/AccountTagsTests.cs`, `PlanCategoryTests.cs`, 그리고 영향받는 기존 테스트

**인터페이스 (뒤 Task가 쓴다)**
```csharp
// Models/ProviderIds.cs
public static string KindOf(string id);   // "claude#k3x9q2" → "claude", "claude" → "claude", "system" → "system"

// Accounts/AccountIds.cs
public static class AccountIds
{
    public const char Separator = '#';
    public const int KeyLength = 6;                                   // [a-z0-9]
    public static IReadOnlyList<string> SupportedKinds { get; }       // [claude, codex]
    public static bool IsAccountId(string? id);                       // ^(claude|codex)#[a-z0-9]{6}$ (Ordinal, 소문자만)
    public static string Create(string kind, Func<int, int>? nextInt = null); // nextInt(maxExclusive); 기본은 RandomNumberGenerator.GetInt32
    public static string KeyOf(string accountId);                     // "#" 뒤 6자
}

// Accounts/AccountStorage.cs
public static class AccountStorage
{
    public const string ClaudeSecretPrefix = "claude-account-";
    public const string CodexHomePrefix = "codex-";
    public static string ClaudeSecretName(string accountId);          // "claude-account-k3x9q2"
    public static string CodexHomeName(string accountId);             // "codex-a1b2c3"
    /// <summary>secretNames(확장자 없는 이름) 중 ClaudeSecretPrefix로 시작하고, 설정에 없는 키인 것.</summary>
    public static IReadOnlyList<string> OrphanClaudeSecrets(IEnumerable<string> secretNames, IEnumerable<string> settingsIds);
    /// <summary>directoryNames 중 CodexHomePrefix로 시작하고, 설정에 없는 키인 것.</summary>
    public static IReadOnlyList<string> OrphanCodexHomes(IEnumerable<string> directoryNames, IEnumerable<string> settingsIds);
}

// Accounts/AccountLoginCommand.cs
public static class AccountLoginCommand
{
    public const string ForbiddenCharacters = "\"%^&|<>;";
    /// <summary>설계 §2.2·§3 명령. 경로에 금지 문자가 있거나 종류가 claude/codex가 아니면 null.</summary>
    public static string? Build(string kind, string directory);
    // claude: set "CLAUDE_SECURESTORAGE_CONFIG_DIR=" && set "CLAUDE_CONFIG_DIR=<dir>" && claude auth login
    // codex:  set "CODEX_HOME=<dir>" && codex login
}

// Models/ProviderView.cs (record에 추가)
public string? AccountTag { get; init; }
public string Title => AccountTag is null ? DisplayName : DisplayName + " · " + AccountTag;
public string ShortName => AccountTag ?? DisplayName;

// Presentation/PlanCategory.cs
public enum PlanCategory { Unknown, Personal, Team }
public static class PlanCategories { public static PlanCategory FromPlan(string? plan); } // 설계 §4 규칙

// Presentation/AccountTags.cs
public static class AccountTags
{
    /// <summary>
    /// 같은 순서의 새 목록을 돌려준다. KindOf가 claude/codex이고 그 종류가 2개 이상인 뷰에만 AccountTag를 붙인다
    /// (그 밖의 뷰는 같은 인스턴스). 태그: Team → Strings.AccountTagTeam, Personal → Strings.AccountTagPersonal,
    /// Unknown → "#n". 같은 종류 안에서 겹치면 두 번째부터 " n"을 붙인다. n은 그 종류 안에서 1부터 센 위치다.
    /// </summary>
    public static IReadOnlyList<ProviderView> Apply(IReadOnlyList<ProviderView> enabledInOrder);
}
```

**동작**
- `AppSettings.NormalizeProviders`
  - `ProviderIds.All`에 있거나 `AccountIds.IsAccountId`인 ID를 유지한다. 계정 ID는 Trim·소문자화한 뒤 검사한다.
  - 중복은 첫 항목만 남긴다. 순서는 그대로다. 빠진 기본 4개는 지금처럼 뒤에 붙인다.
  - `ProviderDefaults.DefaultLines`와 `CreateDefault`는 `KindOf(id)`로 고른다. 그래서 계정 ID도 받는다.
- `WidgetPanelBuilder`의 패널 이름은 `view.ShortName`이다. `DetailCardBuilder`의 제목과 `TrayTextFormatter`의 이름은 `view.Title`이다.
- `ProviderStatusText`의 CLI 이름은 `KindOf`로 고른다. 계정 ID의 `SignedOut` 문구는 스냅숏 메시지가 있으면 그것을 쓴다(공급자가 `AccountReauth`를 넣는다).
- `ConnectionGuideBuilder.Build`: 계정 ID면 `null`이다(설정 카드가 계정 동작을 대신 보여 준다).
- 새 문자열(Core)

  | 키 | en | ko |
  |---|---|---|
  | AccountTagTeam | Team | 팀 |
  | AccountTagPersonal | Personal | 개인 |
  | AccountReauth | Sign in again · Settings → AI connections | 다시 로그인하세요 · 설정 → AI 연결 |

- [ ] 테스트를 먼저 쓴다. 설계 §9의 Core 항목 중 이 Task 범위를 다룬다.
  - 계정 ID, 설정 정규화, `KindOf`, 기본 줄
  - 태그: 1개면 태그 없음, 2개 Team/Personal, Unknown `#2`, 겹침 `개인 2`, gemini·system 미적용, 영어
  - `Title`·`ShortName`, 위젯·카드·툴팁 이름
  - 로그인 명령 문자열(공백 있는 경로 포함), 금지 문자 거부, 정리 대상
- [ ] 구현하고 `dotnet test AITokenUsage.sln`을 통과시킨다.

---

### Task 2: Core: Claude 앱 소유 로그인 (저장·가져오기·갱신·출처)

**Files**
- 새로 만들기 (`src/AITokenUsage.Core/Providers/Claude/`)
  - `ClaudeOwnedCredentials.cs`, `ClaudeLoginImporter.cs`, `ClaudeTokenRenewer.cs`
  - `IClaudeCredentialSource.cs`, `ClaudeCliCredentialSource.cs`, `ClaudeOwnedCredentialSource.cs`
- 수정: `ClaudeCredentials.cs`(상태 추가), `ClaudeUsageProvider.cs`
- 테스트: `tests/AITokenUsage.Core.Tests/Providers/Claude/` 아래 새 테스트. 기존 `ClaudeUsageProviderTests`는 그대로 통과해야 한다.

**인터페이스**
```csharp
public enum ClaudeCredentialState { Ok, NotInstalled, SignedOut, MissingProfileScope, Unreadable, ReauthRequired, RenewFailed }

public sealed record ClaudeOwnedCredentials(
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset? RefreshTokenExpiresAt,
    IReadOnlyList<string> Scopes, string? SubscriptionType, string? RateLimitTier, string? Email, string? OrganizationName)
{
    public string ToJson();                                   // 설계 §2.1 키, 시각은 ms epoch
    public static ClaudeOwnedCredentials? FromJson(string? json); // 형식 오류, 토큰 없음, user:profile 없음 → null
    public ClaudeCredentials ToCredentials();                 // 사용량 요청용
    public override string ToString();                        // 토큰 제외
}

public static class ClaudeLoginImporter
{
    /// <summary>
    /// 설계 §2.2-3 완료 조건을 만족하면 가져온 로그인 정보, 아니면 null.
    /// claudeJson(.claude.json, 없으면 null)에서 oauthAccount.emailAddress·organizationName을 읽는다.
    /// </summary>
    public static ClaudeOwnedCredentials? TryRead(string? credentialsJson, string? claudeJson);
}

public enum ClaudeRenewOutcome { Renewed, ReauthRequired, Failed }
public sealed record ClaudeRenewResult(ClaudeRenewOutcome Outcome, ClaudeOwnedCredentials? Credentials);

public sealed class ClaudeTokenRenewer
{
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string DefaultScope = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public ClaudeTokenRenewer(HttpClient http, TimeProvider time, IAppLog log);
    /// <summary>설계 §2.3. 400·401 → ReauthRequired, 그 밖의 실패 → Failed. 예외를 던지지 않는다(취소 제외).</summary>
    public Task<ClaudeRenewResult> RenewAsync(ClaudeOwnedCredentials current, string userAgent, CancellationToken ct);
}

public interface IClaudeCredentialSource
{
    IReadOnlyList<string> WatchPaths { get; }
    /// <summary>True면 만료 직전 토큰을 스스로 갱신한다(앱 소유). False면 CLI가 갱신하기를 기다린다.</summary>
    bool RenewsItself { get; }
    Task<ClaudeCredentialReadResult> ReadAsync(string userAgent, CancellationToken ct);
    /// <summary>
    /// rejected 토큰이 401을 받은 뒤 부른다. 다시 시도할 Ok 결과, 실패 상태 결과, 또는 새것이 없으면 null.
    /// CLI: 파일을 다시 읽어 토큰이 바뀌었으면 Ok, 아니면 null. 앱 소유: 이번 ReadAsync에서 이미 갱신했으면 null, 아니면 강제 갱신.
    /// </summary>
    Task<ClaudeCredentialReadResult?> RecoverAsync(ClaudeCredentials rejected, string userAgent, CancellationToken ct);
}

public sealed class ClaudeCliCredentialSource : IClaudeCredentialSource
{
    public ClaudeCliCredentialSource(IFileSystem files, string credentialsPath); // 기존 ClaudeCredentialReader 사용
}

public sealed class ClaudeOwnedCredentialSource : IClaudeCredentialSource
{
    public ClaudeOwnedCredentialSource(ISecretStore secrets, string secretName, ClaudeTokenRenewer renewer, TimeProvider time, IAppLog log);
    /// <summary>처음 불러온 뒤의 이메일(설정 카드 표시용). 불러오기 전에는 null.</summary>
    public string? Email { get; }
    /// <summary>다시 로그인: 저장하고, 메모리를 바꾸고, ReauthRequired 상태를 지운다. ReadAsync와 같은 잠금을 쓴다.</summary>
    public Task ReplaceAsync(ClaudeOwnedCredentials credentials, CancellationToken ct);
}

// ClaudeUsageProvider
public ClaudeUsageProvider(ProviderContext ctx, string id, IClaudeCredentialSource source, ClaudeVersionProbe? versionProbe = null);
public static ClaudeUsageProvider ForAccount(ProviderContext ctx, string accountId); // AccountStorage.ClaudeSecretName, ctx.Secrets, new ClaudeTokenRenewer(ctx.Http, ctx.Time, ctx.Log)
public IClaudeCredentialSource Source { get; }
public string? AccountEmail => (Source as ClaudeOwnedCredentialSource)?.Email;
```

**동작**
- 기존 생성자 `(ctx)`, `(ctx, versionProbe)`는 `(ctx, ProviderIds.Claude, new ClaudeCliCredentialSource(ctx.Files, ClaudePaths.ResolveCredentialsPath(ctx.Env)), versionProbe)`로 넘긴다. `WatchPaths`는 `Source.WatchPaths`다.
- 사용자 에이전트(`claude-code/<버전>`)는 `ReadAsync`보다 먼저 구한다.
- 만료가 가까울 때 `WaitingForCliRefresh`로 두는 검사는 `!Source.RenewsItself`일 때만 한다.
- 401 처리: `RecoverAsync` 결과
  - 다른 토큰의 Ok면 한 번 다시 시도한다.
  - Ok가 아닌 결과면 `FromCredentialState`로 바꾼다.
  - null이면 1시간 보류한다. 앱 소유는 `SignedOut` + `Strings.AccountReauth`, CLI는 지금처럼 `WaitingForCliRefresh`다.
- `FromCredentialState`
  - `ReauthRequired` → `SignedOut` + `Strings.AccountReauth`
  - `RenewFailed` → `Error` + 네트워크 문구
- **앱 소유 출처**
  - 처음 `ReadAsync`에서 저장소를 읽는다. 백그라운드 스레드에서 불리므로 괜찮다. 없거나 깨졌으면 `ReauthRequired`다.
  - `ReauthRequired` 표시가 서 있으면 요청 없이 바로 그것을 돌려준다. `ReplaceAsync`만 이 표시를 지운다.
  - `ExpiresAt - now < ClaudeUsageProvider.RefreshMargin`이면 갱신한다.
    - `RefreshTokenExpiresAt <= now`면 요청 없이 `ReauthRequired`다.
    - `Renewed`면 먼저 `secrets.Save` 하고(실패하면 로그만 남김) 메모리를 바꾼다.
    - `ReauthRequired`면 표시를 세운다. `Failed`면 `RenewFailed`를 돌려준다(토큰 유지).
  - `RecoverAsync`: 이번 `ReadAsync`에서 이미 갱신했으면 null을 돌려준다. 아니면 위와 같은 갱신을 한 번 한다.
- **갱신기**: 응답 JSON 필드와 저장 규칙은 설계 §2.3을 따른다. `expires_in`이나 `access_token`이 없으면 `Failed`다. 로그에는 결과 종류와 HTTP 코드만 남긴다.
- [ ] 테스트를 먼저 쓴다.
  - `StubHttpHandler`, `FakeSecretStore`, `FakeTimeProvider`(또는 기존 시간 가짜)를 쓴다.
  - 설계 §9의 Claude 항목을 모두 다룬다. 갱신 요청 본문의 JSON 키와 값, 헤더, 순서(저장 후 조회)를 확인한다.
  - 기록용 로그(`RecordingLog`)에 토큰 문자열이 없음을 확인한다.
- [ ] 구현하고 전체 테스트를 통과시킨다.

---

### Task 3: Core: Codex 계정과 공급자 목록

**Files**
- 수정 (`src/AITokenUsage.Core/Providers/`)
  - `Codex/CodexAppServerClient.cs`, `Codex/CodexUsageProvider.cs`, `Codex/CodexAuth.cs`, `Codex/CodexAuthReader.cs`
  - `ProviderCatalog.cs`
- 새로 만들기: `Codex/CodexLoginDetector.cs`
- 테스트: `tests/AITokenUsage.Core.Tests/Providers/Codex/`, `Providers/ProviderCatalogTests.cs`(없으면 새로)

**인터페이스**
```csharp
// CodexAppServerClient
public static ProcessSpec BuildSpec(string codexPath, string? codexHome = null);   // codexHome이 있으면 EnvironmentOverrides["CODEX_HOME"] = codexHome
public Task<CodexAppServerResult> ReadRateLimitsAsync(string codexPath, string? codexHome, CancellationToken ct); // 기존 (codexPath, ct)는 null로 넘김

// CodexAuth (record에 init 속성 추가)
public string? Email { get; init; }   // id_token 페이로드의 "email". ToString에 넣지 않는다

// CodexUsageProvider
public CodexUsageProvider(ProviderContext ctx, string id, CodexPaths paths, bool isAccount);
public static CodexUsageProvider ForAccount(ProviderContext ctx, string accountId, string accountsDirectory); // home = Path.Combine(accountsDirectory, AccountStorage.CodexHomeName(accountId))
public bool IsAccount { get; }
public string? AccountEmail { get; }  // 마지막으로 읽은 auth.json의 Email (Volatile)

// CodexLoginDetector
public static class CodexLoginDetector
{
    /// <summary>ChatGPT 로그인이고 access_token이 있으며, previousAccessToken과 다르면 true.</summary>
    public static bool IsSignedIn(CodexAuth? auth, string? previousAccessToken);
}

// ProviderCatalog
public static IReadOnlyList<IUsageProvider> CreateAll(ProviderContext ctx, IEnumerable<string> accountIds, string accountsDirectory);
// 기본 4개 다음에 accountIds 순서대로(IsAccountId가 아닌 것과 중복은 건너뜀). 기존 CreateAll(ctx)는 빈 목록으로 넘긴다.
```

**동작**
- 기존 생성자 `CodexUsageProvider(ctx)`는 `(ctx, ProviderIds.Codex, CodexPaths.Resolve(ctx.Env), false)`와 같다.
- `IsAccount`일 때
  - app-server를 `codexHome = paths.Home`으로 띄운다.
  - 로그인 파일이 없으면 Codex 설치 여부와 상관없이 `SignedOut` + `Strings.AccountReauth`다. app-server가 AuthRequired를 돌려줄 때도 같다.
  - 나머지 상태는 기본 계정과 같다.
- 기본 계정의 동작과 기존 테스트는 바뀌지 않는다. 기본 계정은 app-server에 환경 변수를 넘기지 않는다.
- [ ] 테스트를 먼저 쓴다: `CODEX_HOME` 전달, 계정 경로, 로그인 없음 문구, 이메일 파싱(`TestJwt`), 로그인 판정, 목록 순서·건너뛰기
- [ ] 구현하고 전체 테스트를 통과시킨다.

---

### Task 4: App: 계정 공급자 연결, 태그 표시, 시작 시 정리

**Files**
- 수정 (`src/AITokenUsage/`)
  - `AppPaths.cs`, `AppHost.cs`
  - `UI/ProviderGlyphs.cs`, `UI/TrayIconRenderer.cs`, `UI/ProviderPanel.xaml.cs`
  - `UI/DetailFlyoutController.cs`, `UI/TrayIconController.cs`, `UI/WidgetViewModel.cs`(필요한 곳만)
- 새로 만들기: `AccountDataSweeper.cs`

**동작**
- `AppPaths.AccountsDirectory = LocalDirectory\accounts`, `AppPaths.LoginDirectory = LocalDirectory\login`
- `AppHost`
  - `Providers = ProviderCatalog.CreateAll(ProviderContext, Settings.Providers.Select(p => p.Id).Where(AccountIds.IsAccountId), AppPaths.AccountsDirectory)`
  - `EnabledViewsInOrder()`는 켜진 AI 뷰에 `AccountTags.Apply`를 적용한 뒤 PC 뷰를 붙인다.
  - 새 메서드 `ProviderView? LabeledView(string providerId)`는 `EnabledViewsInOrder()`에서 찾고, 없으면 `Usage.GetView(providerId)`를 돌려준다.
  - 화면으로 가는 뷰(상세 카드, 트레이 툴팁, 위젯 패널)가 `Usage.GetView`를 직접 쓰면 `LabeledView`로 바꾼다. 위젯 줄 설정을 찾는 곳은 그대로 ID로 찾는다.
- `Start()`에서 백그라운드로 `AccountDataSweeper.Sweep(settingsIds, log)`를 실행한다(설계 §6).
  - `AppPaths.LoginDirectory` 전체를 지운다.
  - `AppPaths.LocalDirectory`의 `*.bin` 가운데 `AccountStorage.OrphanClaudeSecrets`가 고른 것을 지운다.
  - `AppPaths.AccountsDirectory`의 하위 폴더 가운데 `OrphanCodexHomes`가 고른 것을 지운다.
  - 예외는 로그만 남긴다.
- `ProviderGlyphs`와 `TrayIconRenderer`의 종류별 분기는 `ProviderIds.KindOf`를 쓴다(아이콘·색이 계정에도 나오게).
- `ProviderPanel`의 `ProviderNameMaxWidth`를 46으로 바꾼다.
- [ ] 빌드·테스트를 통과시킨다.
- [ ] 수동 확인(실제 입력 없음): 설정에 `claude#test01`을 넣고 실행해서 위젯에 `#2` 태그 패널이 보이는지 확인한다. 뒤에 Task 6에서 캡처한다.

---

### Task 5: App: 설정 화면 (계정 추가·다시 로그인·삭제), 설치 제거 문구, README

**Files**
- 새로 만들기 (`src/AITokenUsage/`)
  - `Accounts/AccountLoginSession.cs`
  - `UI/Settings/AccountsSection.xaml(.cs)`(계정 추가 구역)
- 수정
  - `UI/Settings/ProvidersPage.xaml(.cs)`, `UI/Settings/ProviderCard.xaml(.cs)`
  - `Resources/UiStrings.resx`, `UiStrings.ko.resx`
  - `installer/AITokenUsage.iss`(삭제 안내 문구), `README.md`

**AccountLoginSession** (UI 스레드에서 만들고 이벤트도 UI 스레드에서 낸다)
```csharp
public sealed class AccountLoginSession : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    public AccountLoginSession(AppHost host, string accountId, bool isNew);
    public void Start();     // 폴더 준비 → 명령 만들기 → TerminalLauncher.Launch → 폴링
    public void Cancel();    // 폴링을 멈추고 정리(Claude 임시 폴더, 새 Codex 계정 폴더)
    public event EventHandler? Completed;
    public event EventHandler<AccountLoginFailure>? Failed;
}
public enum AccountLoginFailure { InvalidPath, TerminalFailed, TimedOut, SaveFailed }
```
- **Claude**
  - 폴더는 `LoginDirectory\claude-<키>`다. 시작할 때 비운다.
  - 폴링할 때마다 `Task.Run`에서 `.credentials.json`과 `.claude.json`을 공유 읽기로 읽고 `ClaudeLoginImporter.TryRead`를 부른다.
  - 처음 null이 아니게 되면 `SettleDelay` 뒤에 한 번 더 읽어서 최종으로 쓴다.
  - 새 계정이면 `ProviderContext.Secrets.Save(AccountStorage.ClaudeSecretName(id), creds.ToJson())`(백그라운드)를 한다.
  - 다시 로그인이면 해당 `ClaudeUsageProvider`의 `(Source as ClaudeOwnedCredentialSource).ReplaceAsync`를 부르고 `Usage.RefreshNowAsync(id)`를 한다.
  - 그다음 폴더를 지운다(실패하면 로그만).
- **Codex**
  - 폴더는 `AccountsDirectory\codex-<키>`다.
  - 시작할 때 기존 `auth.json`의 access_token을 기억한다(다시 로그인용).
  - 폴링할 때 `CodexAuthReader.TryParse` 다음 `CodexLoginDetector.IsSignedIn`으로 판정한다. 완료되면 폴더를 그대로 둔다(계정 저장소). 다시 로그인이면 파일 감시가 새로 고친다.
- **새 계정 완료**
  - `Settings.Providers`에서 같은 종류의 마지막 항목 뒤에 `ProviderDefaults.CreateDefault(id)`를 넣고 `ApplySettings()`를 한다.
  - 다시 시작 안내를 보여 준다.

**화면** (설계 §5. 기존 SettingsCard·버튼 스타일을 쓴다)
- `AccountsSection`은 ProvidersPage 카드 목록 아래에 둔다.
  - 버튼 두 개: [Claude 계정 추가], [Codex 계정 추가]
  - `FindOnPath("claude"/"codex")`가 null이면 그 버튼을 끄고 설명 문구를 보여 준다. 검사는 백그라운드에서 한다.
  - 진행 중에는 진행 문구와 [취소]를 보여 준다. 끝나면 결과 문구를 보여 준다.
  - 새 계정 완료 문구는 "추가했어요. 다시 시작하면 위젯에 나타나요"와 [지금 다시 시작]이다.
- `ProviderCard`(계정 ID일 때)
  - 이름은 `Claude · 태그`(`Host.LabeledView`의 `Title`, 없으면 DisplayName)다. 이름 아래에 이메일을 흐린 글자로 보여 준다.
  - 연결 안내 대신 [다시 로그인]을 둔다. 상태가 SignedOut이면 강조 스타일이다.
  - 펼친 영역에 [계정 삭제]를 둔다.
    - 확인 대화 상자(`MessageBox`, 예/아니요, 기본 아니요)를 띄운다.
    - 예면 설정에서 빼고, `ApplySettings()` 다음 `LanguageBootstrap.Restart(Host)`를 한다.
- 기본 계정 카드: 같은 종류가 2개 이상이면 이름 아래에 "이 PC의 Claude Code" / "이 PC의 Codex"를 보여 준다.
- 다시 로그인과 계정 추가는 한 번에 하나만 진행한다. 진행 중에는 다른 버튼을 끈다.
- 새 문자열(App): 위 문구들의 한국어·영어. 영어 문구는 짧고 평이하게 쓴다.
- 설치 제거 안내(`DeleteUserData`, 두 언어)에 "추가한 계정 로그인"을 넣는다.
  - ko 예: "앱이 저장한 데이터(앱에 저장된 Copilot 로그인과 추가한 계정 로그인 포함)를 지웁니다."
- README: "여러 계정" 절을 넣는다. 추가 방법, 팀·개인 표시, 위험(설계 §7)을 짧게 쓴다.
- [ ] 빌드·테스트를 통과시킨다.

---

### Task 6: 실행 확인 (캡처)

- 기존 검증 규칙을 따른다.
  - 사용자 앱을 멈췄으면 다시 실행한다.
  - `settings.json`·`cache.json`·Run 값은 백업했다가 바이트 단위로 복원한다.
  - 실제 입력을 보내지 않고, PrintWindow로만 캡처한다.
- 시험용 설정: 기존 설정에 `{"Id":"claude#test01","Enabled":true}`를 Claude 바로 뒤에 넣는다.
- 확인할 것
  1. 위젯: Claude 기본 패널은 `팀`(또는 해당 플랜 태그), 추가 패널은 `#2`와 "로그인 필요" 짧은 문구
  2. 상세 카드 제목 `Claude · #2`와 설명 "다시 로그인하세요 · 설정 → AI 연결"(DEBUG `--pin-detail-card`)
  3. 설정 → AI 연결(DEBUG `--settings-page=<n>`): 계정 추가 구역, 계정 카드의 [다시 로그인]·이메일 자리, 기본 카드의 "이 PC의 Claude Code"
- 캡처 파일은 스크래치패드에 두고, 결과를 보고서에 적는다.
- 끝나면 시험용 항목을 없앤 원래 설정을 복원한다. 정리 동작 때문에 `claude-account-test01.bin`이 생기지는 않는다. 생겼다면 지운다.
