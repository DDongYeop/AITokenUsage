# AI 사용량 작업 표시줄 위젯 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows 11 작업 표시줄 안에 Claude·Codex·Gemini CLI·Copilot 사용량을 2줄 미니바로 보여주고 N초마다 자동 전환하는 WPF 위젯과, 이를 제어하는 설정 창을 만든다.

**Architecture:** UI 의존이 없는 `AITokenUsage.Core`(공급자·파서·폴링·전환·배치 계산, 전부 단위 테스트)와 WPF 앱 `AITokenUsage`(작업 표시줄 자식 창 임베딩·위젯·상세 카드·설정 창·트레이)로 나눈다. 공급자는 `IUsageProvider`로 통일하고, 외부 의존(HTTP·파일·환경 변수·프로세스·자격 증명·시계)은 `ProviderContext`로 주입한다.

**Tech Stack:** .NET 10 (SDK 10.0.401), C# (LangVersion latest), WPF + WinForms(NotifyIcon 전용), System.Text.Json, System.Windows.Automation(UIA), xUnit 2.9.3, Microsoft.Extensions.TimeProvider.Testing 10.11.0.

**Spec:** `docs/superpowers/specs/2026-10-28-ai-usage-taskbar-widget-design.md` (정본). API 세부는 `docs/research/*.md`.

## Global Constraints

- TFM: 세 프로젝트 모두 `net10.0-windows`. 앱은 `WinExe`, `UseWPF=true`, `UseWindowsForms=true`.
- `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, `AllowUnsafeBlocks=true`(LibraryImport).
- 앱 csproj에서 WinForms 암시적 using 충돌 제거: `<Using Remove="System.Windows.Forms" />`, `<Using Remove="System.Drawing" />`.
- 서드파티 NuGet 금지. 테스트 프로젝트만 xunit 템플릿 패키지 + `Microsoft.Extensions.TimeProvider.Testing` 10.11.0 허용.
- 사용자에게 보이는 문자열은 한국어. 로그·코드 주석은 영어.
- 토큰, `Authorization` 헤더, API 응답 원문을 **로그·캐시·예외 메시지에 절대 남기지 않는다.**
- CLI 자격 증명 파일(`.credentials.json`, `auth.json`, `oauth_creds.json` 등)에 **절대 쓰지 않는다.** 읽기는 항상 `FileShare.ReadWrite | FileShare.Delete`.
- Claude·Codex 토큰을 직접 refresh 하지 않는다. Gemini만 메모리 내 refresh 허용.
- 앱 매니페스트: PerMonitorV2 + `supportedOS {8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}` + `asInvoker`.
- UI 스레드에서 블로킹 I/O 금지(위젯이 explorer와 입력 큐를 공유).
- 저장 위치: 설정 `%APPDATA%\AITokenUsage\settings.json`, 캐시 `%LOCALAPPDATA%\AITokenUsage\cache.json`, Copilot 토큰 `%LOCALAPPDATA%\AITokenUsage\copilot-token.bin`(DPAPI), 로그 `%LOCALAPPDATA%\AITokenUsage\logs\app.log`.
- 커밋 메시지: Conventional Commits 접두사 + 한국어 요약, 마지막 줄에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- 빌드/테스트 명령은 저장소 루트에서: `dotnet build AITokenUsage.sln`, `dotnet test AITokenUsage.sln`.

---

## 공유 계약 (모든 Task가 이 이름·시그니처를 그대로 쓴다)

### 네임스페이스·파일 배치

| 네임스페이스 | 폴더 |
|---|---|
| `AITokenUsage.Core.Models` | `src/AITokenUsage.Core/Models/` |
| `AITokenUsage.Core.Abstractions` | `src/AITokenUsage.Core/Abstractions/` (인터페이스 + 실제 구현 `Real*`, `Windows*`, `Dpapi*`, `FileAppLog`) |
| `AITokenUsage.Core.Util` | `src/AITokenUsage.Core/Util/` |
| `AITokenUsage.Core.Settings` | `src/AITokenUsage.Core/Settings/` |
| `AITokenUsage.Core.Polling` | `src/AITokenUsage.Core/Polling/` |
| `AITokenUsage.Core.Rotation` | `src/AITokenUsage.Core/Rotation/` |
| `AITokenUsage.Core.Taskbar` | `src/AITokenUsage.Core/Taskbar/` |
| `AITokenUsage.Core.Providers` (+ `.Claude`, `.Codex`, `.Gemini`, `.Copilot`) | `src/AITokenUsage.Core/Providers/...` |
| `AITokenUsage` (App, AppHost) | `src/AITokenUsage/` |
| `AITokenUsage.Taskbar` | `src/AITokenUsage/Taskbar/` |
| `AITokenUsage.UI` | `src/AITokenUsage/UI/` |
| `AITokenUsage.Core.Tests` (+ `.Fakes`) | `tests/AITokenUsage.Core.Tests/` (가짜 구현은 `Fakes/`, 샘플은 `Fixtures/<provider>/`) |

### Models (Task 2)

```csharp
namespace AITokenUsage.Core.Models;

public enum ProviderStatus { Ok, NotInstalled, SignedOut, WaitingForCliRefresh, Unsupported, RateLimited, Error }
public enum Severity { Normal, Warning, Critical }

public sealed record UsageMeter(
    string Key,
    string ShortLabel,
    string LongLabel,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    Severity? ServerSeverity = null,
    string? TextOverride = null);

public sealed record UsageSnapshot(
    string ProviderId,
    ProviderStatus Status,
    string? PlanLabel,
    IReadOnlyList<UsageMeter> Meters,
    DateTimeOffset FetchedAt,
    string? StatusMessage = null,
    TimeSpan? RetryAfter = null,
    string? ExtraLine = null)
{
    public static UsageSnapshot WithStatus(string providerId, ProviderStatus status, DateTimeOffset now,
        string? message = null, TimeSpan? retryAfter = null)
        => new(providerId, status, null, Array.Empty<UsageMeter>(), now, message, retryAfter);
}

public sealed record ProviderView(string ProviderId, string DisplayName, UsageSnapshot Current, UsageSnapshot? LastGood)
{
    public bool IsStale => Current.Status != ProviderStatus.Ok && LastGood is not null;
    public IReadOnlyList<UsageMeter> DisplayMeters =>
        Current.Status == ProviderStatus.Ok ? Current.Meters : (LastGood?.Meters ?? Current.Meters);
    public string? DisplayPlan => Current.PlanLabel ?? LastGood?.PlanLabel;
    public bool HasAnyData => DisplayMeters.Count > 0;
}

public static class ProviderIds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";
    public const string Copilot = "copilot";
    public static readonly IReadOnlyList<string> All = [Claude, Codex, Gemini, Copilot];
}

public sealed record MeterOption(string Key, string Label);
```

### Abstractions (Task 2)

```csharp
namespace AITokenUsage.Core.Abstractions;

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    /// <summary>FileShare.ReadWrite|Delete로 전체를 읽는다. 파일이 없으면 null. 그 외 IO 오류는 IOException.</summary>
    string? ReadAllTextShared(string path);
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive);
    DateTime GetLastWriteTimeUtc(string path);
    /// <summary>공유 모드로 열어 마지막 줄부터 거꾸로 돌려준다(JSONL 꼬리 스캔용).</summary>
    IEnumerable<string> ReadLinesReverse(string path);
}

public interface IEnvironment
{
    /// <summary>정의되지 않았으면 null, 정의됐지만 비었으면 "".</summary>
    string? GetVariable(string name);
    string UserProfile { get; }
    string LocalAppData { get; }
    string RoamingAppData { get; }
}

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null, // 값이 null이면 해당 변수 제거
    TimeSpan? Timeout = null);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface IInteractiveProcess : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken ct);
    /// <summary>EOF면 null.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
    IInteractiveProcess StartInteractive(ProcessSpec spec);
    /// <summary>PATH와 PATHEXT로 찾은 전체 경로, 없으면 null.</summary>
    string? FindOnPath(string commandName);
}

public sealed record StoredCredential(string TargetName, string? UserName, string Secret);

public interface ICredentialStore
{
    /// <summary>CRED_TYPE_GENERIC 하나를 읽어 UTF-8 blob을 문자열로. 없으면 null.</summary>
    string? ReadGeneric(string targetName);
    /// <summary>CredEnumerate(filter). 예: "copilot-cli*". 없으면 빈 목록.</summary>
    IReadOnlyList<StoredCredential> EnumerateGeneric(string filter);
}

public interface ISecretStore
{
    void Save(string name, string secret);
    string? Load(string name);
    void Delete(string name);
}

public interface IAppLog
{
    void Info(string message);
    void Warn(string message, Exception? ex = null);
    void Error(string message, Exception? ex = null);
}

public sealed record ProviderContext(
    HttpClient Http,
    TimeProvider Time,
    IFileSystem Files,
    IEnvironment Env,
    IProcessRunner Processes,
    ICredentialStore Credentials,
    ISecretStore Secrets,
    IAppLog Log);
```

실제 구현(Task 2): `RealFileSystem`, `RealEnvironment`, `RealProcessRunner`(CreateNoWindow, UTF-8 stdout/stderr, 타임아웃 시 Kill(entireProcessTree)), `WindowsCredentialStore`(CredReadW/CredEnumerateW/CredFree P/Invoke), `DpapiSecretStore(string directory)`(파일명 `<name>.bin`, `ProtectedData`는 `System.Security.Cryptography.ProtectedData` — net10.0-windows 공유 프레임워크에 없으면 `CryptProtectData` P/Invoke로 구현; NuGet 금지 규칙 우선), `FileAppLog(string directory)`(1MB 회전, 3개 보존), `NullAppLog`.

`HttpClient` 생성 헬퍼(Task 2): `public static class HttpClientFactory { public static HttpClient Create(); }` — `SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) }`, `Timeout = 15s`.

### 테스트 가짜 (Task 2, `tests/AITokenUsage.Core.Tests/Fakes/`)

```csharp
namespace AITokenUsage.Core.Tests.Fakes;
public sealed class FakeFileSystem : IFileSystem      // AddFile(path, content, DateTime? lastWriteUtc), Remove(path), AddDirectory(path); 경로 비교는 OrdinalIgnoreCase
public sealed class FakeEnvironment : IEnvironment    // Set(name, value?), UserProfile 등 생성자 인자(기본 @"C:\Users\test")
public sealed class FakeProcessRunner : IProcessRunner // OnRun(Func<ProcessSpec, ProcessResult>), OnInteractive(Func<ProcessSpec, FakeInteractiveProcess>), SetPath(command, fullPath?) ; Calls 목록 기록
public sealed class FakeInteractiveProcess : IInteractiveProcess // EnqueueOutput(line), Written 목록, 무응답 시 ct 취소까지 대기
public sealed class FakeCredentialStore : ICredentialStore // Add(target, userName, secret)
public sealed class FakeSecretStore : ISecretStore
public sealed class StubHttpHandler : HttpMessageHandler // When(Func<HttpRequestMessage,bool>, Func<HttpRequestMessage,HttpResponseMessage>), Requests 목록(헤더 포함 기록), 미매칭 시 예외
public sealed class NullLog : IAppLog
public static class Fixture { public static string Read(string relativePath); } // AppContext.BaseDirectory/Fixtures/...
public static class TestContexts { public static ProviderContext Create(StubHttpHandler http, FakeTimeProvider time, FakeFileSystem? files = null, FakeEnvironment? env = null, FakeProcessRunner? proc = null, FakeCredentialStore? creds = null, FakeSecretStore? secrets = null); }
```

### Util (Task 3)

```csharp
namespace AITokenUsage.Core.Util;
public static class Jwt
{
    public static bool TryReadPayload(string? jwt, out System.Text.Json.JsonElement payload); // base64url, 서명 검증 없음
    public static DateTimeOffset? GetExpiry(string? jwt);   // "exp" (epoch seconds)
}
public sealed class ResetTimeFormatter
{
    public ResetTimeFormatter(TimeZoneInfo zone);
    public string FormatRelative(DateTimeOffset resetsAt, DateTimeOffset now); // "3시간 12분 후 · 오늘 15:50" / "1일 8시간 후 · 10/30(목) 05:00" / "곧 리셋"
    public string FormatShortDate(DateTimeOffset at);   // "11/1"
    public string FormatAgo(DateTimeOffset at, DateTimeOffset now); // "방금", "3분 전", "2시간 전"
}
public static class SeverityRules
{
    public const double WarningPercent = 75, CriticalPercent = 90;
    public static Severity Effective(UsageMeter meter); // ServerSeverity 우선, 없으면 UsedPercent 기준
    public static double? Clamp(double? percent);      // 0..100
}
public static class Json   // JsonElement 안전 접근
{
    public static string? Str(System.Text.Json.JsonElement e, string name);
    public static double? Num(System.Text.Json.JsonElement e, string name);   // Number 또는 숫자 문자열
    public static bool? Bool(System.Text.Json.JsonElement e, string name);
    public static System.Text.Json.JsonElement? Obj(System.Text.Json.JsonElement e, string name); // Object만
    public static System.Text.Json.JsonElement? Arr(System.Text.Json.JsonElement e, string name); // Array만
    public static DateTimeOffset? Iso(System.Text.Json.JsonElement e, string name);
    public static DateTimeOffset? EpochSeconds(System.Text.Json.JsonElement e, string name);
}
public static class SharedJson { public static readonly System.Text.Json.JsonDocumentOptions Lenient; } // 주석 Skip + 끝 쉼표 허용
```

### Settings (Task 4)

```csharp
namespace AITokenUsage.Core.Settings;
public enum WidgetPlacement { LeftEdge, BeforeStart, BeforeTray }
public enum ValueMode { Used, Remaining }
public enum TextColorMode { Auto, Light, Dark }

public sealed class ProviderSettings
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Line1Key { get; set; } = "";
    public string Line2Key { get; set; } = "";
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<ProviderSettings> Providers { get; set; } = ProviderDefaults.CreateDefaultProviders();
    public bool RotationEnabled { get; set; } = true;
    public int RotationSeconds { get; set; } = 5;          // 2..60
    public bool PauseOnHover { get; set; } = true;
    public bool SkipUnavailable { get; set; } = true;
    public string? MonitorDeviceName { get; set; }         // null = 주 모니터 (예: @"\\.\DISPLAY2")
    public WidgetPlacement Placement { get; set; } = WidgetPlacement.LeftEdge;
    public ValueMode ValueMode { get; set; } = ValueMode.Used;
    public TextColorMode TextColor { get; set; } = TextColorMode.Auto;
    public int RefreshMinutes { get; set; } = 5;           // 2, 5, 10, 15
    public bool StartWithWindows { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public AppSettings Normalize();                        // 범위 보정, 누락 공급자 추가, 중복 제거, 알 수 없는 Id 제거
}

public static class ProviderDefaults
{
    // claude: session/weekly, codex: 5h/weekly, gemini: pro/flash, copilot: monthly/reset
    public static List<ProviderSettings> CreateDefaultProviders();
    public static (string Line1, string Line2) DefaultLines(string providerId);
    public static readonly int[] RefreshChoicesMinutes = [2, 5, 10, 15];
}

public sealed class SettingsStore
{
    public SettingsStore(string filePath, IAppLog log);
    public string FilePath { get; }
    public AppSettings Load();          // 없음/손상 → 기본값(손상 파일은 settings.json.bak로 보관)
    public void Save(AppSettings settings); // 임시 파일 + File.Replace/Move 원자적 저장
}
```

### Polling (Task 5)

```csharp
namespace AITokenUsage.Core.Polling;
public sealed class BackoffPolicy
{
    // Ok → baseInterval. RateLimited → max(RetryAfter ?? 5분, 5분) * 2^(n-1), 최대 60분.
    // Error → max(baseInterval, 2분) * 2^(n-1), 최대 30분. 그 외 상태 → baseInterval.
    public TimeSpan NextDelay(UsageSnapshot result, TimeSpan baseInterval, int consecutiveFailures);
    public static TimeSpan Jitter(TimeSpan delay, double unit01); // ±10%
}
public sealed class SnapshotCache
{
    public SnapshotCache(string filePath, IAppLog log);
    public IReadOnlyDictionary<string, UsageSnapshot> Load();
    public void Save(IReadOnlyDictionary<string, UsageSnapshot> lastGood);
}
public sealed class UsageService : IAsyncDisposable
{
    public UsageService(IReadOnlyList<IUsageProvider> providers, Func<TimeSpan> refreshInterval,
        SnapshotCache cache, TimeProvider time, IAppLog log);
    public event EventHandler<ProviderView>? ViewChanged;   // 백그라운드 스레드에서 발생 — 구독자가 마샬링
    public IReadOnlyList<ProviderView> Views { get; }        // 공급자 등록 순서
    public ProviderView? GetView(string providerId);
    public void Start();                                     // 캐시 로드 → 즉시 1회 조회 → 루프
    public Task RefreshNowAsync(string? providerId = null);  // 레이트 리밋 차단 중이면 건너뜀
    public void NotifyPathChanged(string fullPath);          // WatchPaths와 일치(동일 또는 하위)하면 1.5초 디바운스 후 조회
    public ValueTask DisposeAsync();
}
```

### Provider 인터페이스 (Task 2)

```csharp
namespace AITokenUsage.Core.Providers;
public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    TimeSpan MinInterval { get; }
    IReadOnlyList<string> WatchPaths { get; }       // 절대 경로(파일 또는 디렉터리)
    IReadOnlyList<MeterOption> StaticMeterOptions { get; } // 설정 드롭다운 기본 항목. 동적 항목은 스냅샷 Meters에서 추가
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);
}
public static class ProviderCatalog   // Task 13
{
    public static IReadOnlyList<IUsageProvider> CreateAll(ProviderContext ctx); // 순서: claude, codex, gemini, copilot
}
```

공급자 클래스 이름(생성자는 모두 `(ProviderContext ctx)`):
`Claude.ClaudeUsageProvider`, `Codex.CodexUsageProvider`, `Gemini.GeminiUsageProvider`, `Copilot.CopilotUsageProvider`.
Copilot 추가 공개 멤버(Task 12): `string? SignedInLogin { get; }`, `Task SignInWithTokenAsync(string token, CancellationToken ct)`(검증 후 ISecretStore "copilot-token" 저장, login 조회), `void SignOut()`.
기기 코드(Task 13): `Copilot.GitHubDeviceFlow(HttpClient http, TimeProvider time)` — `Task<DeviceCodeInfo> StartAsync(CancellationToken)`, `Task<string> WaitForTokenAsync(DeviceCodeInfo code, CancellationToken)`(성공 시 access_token, 실패 시 `DeviceFlowException(DeviceFlowError)`); `record DeviceCodeInfo(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval)`; `enum DeviceFlowError { Expired, Denied, Disabled, Network, Unknown }`.

미터 키(설정 Line1Key/Line2Key가 참조):
- Claude: `session`(5h), `weekly`(주), `weekly:<slug>`(주·Fa 등; slug = display_name 소문자, 공백→`-`), 레거시 `weekly:sonnet`, `weekly:opus`
- Codex: `5h`, `daily`, `weekly`, `monthly`, `annual`, 알 수 없는 창 `primary`/`secondary`; 추가 한도는 `<metered_feature>:<위 키>`
- Gemini: `pro`, `flash`, `flash-lite`
- Copilot: `monthly`(주 미터), `reset`(TextOverride "↻ 11/1" 텍스트 줄), `completions`(Free)

### Rotation (Task 6)

```csharp
namespace AITokenUsage.Core.Rotation;
public sealed class RotationChangedEventArgs(string? currentId, int direction) : EventArgs
{
    public string? CurrentId { get; } = currentId;
    public int Direction { get; } = direction; // +1 다음, -1 이전, 0 목록 변경으로 인한 교체
}
public sealed class RotationController
{
    public RotationController(TimeProvider time);
    public void Configure(bool enabled, TimeSpan interval, bool skipUnavailable);
    public void UpdateProviders(IReadOnlyList<ProviderView> enabledInOrder);
    public IReadOnlyList<string> EligibleIds { get; }
    public string? CurrentId { get; }
    public int CurrentIndex { get; }
    public bool IsUserPaused { get; }
    public event EventHandler<RotationChangedEventArgs>? CurrentChanged;
    public void OnTick();                 // 앱의 DispatcherTimer(250ms)가 호출. 간격 경과 시 Next
    public void Next();                   // 수동: 타이머 리셋
    public void Previous();
    public void SetHoverPaused(bool paused); // 해제 시 타이머 리셋
    public void SetUserPaused(bool paused);
}
```
적격 규칙: `skipUnavailable`이면 `Current.Status ∈ {NotInstalled, SignedOut, Unsupported} && LastGood == null`인 공급자 제외. 결과가 비면 `enabledInOrder`의 첫 항목만. `enabledInOrder`도 비면 `CurrentId = null`.

### Taskbar 순수 계산 (Task 7)

```csharp
namespace AITokenUsage.Core.Taskbar;
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
    public bool IsInside(PixelRect outer) => Left >= outer.Left && Top >= outer.Top && Right <= outer.Right && Bottom <= outer.Bottom;
}
public sealed record TaskbarGeometry(
    PixelRect Taskbar, uint Dpi, bool IsHorizontal, bool IsCentered,
    PixelRect? Start, PixelRect? Widgets, PixelRect? AppButtons, PixelRect? Tray); // 전부 화면 좌표(물리 px)
public static class TaskbarLayoutCalculator
{
    public const double DesiredWidthDip = 176, DesiredHeightDip = 40, PaddingDip = 8;
    /// <summary>작업 표시줄 기준(부모 상대) 물리 px 사각형. 공간 부족·세로 작업 표시줄이면 null.</summary>
    public static PixelRect? Compute(TaskbarGeometry g, WidgetPlacement placement);
}
```

### 앱 계층 계약 (Task 14–19)

```csharp
namespace AITokenUsage;
public sealed class AppHost : IAsyncDisposable          // Task 14 생성, 이후 Task가 확장
{
    public AppHost(System.Windows.Threading.Dispatcher dispatcher);
    public AppSettings Settings { get; }
    public SettingsStore SettingsStore { get; }
    public ProviderContext ProviderContext { get; }
    public IReadOnlyList<IUsageProvider> Providers { get; }
    public UsageService Usage { get; }
    public RotationController Rotation { get; }
    public IAppLog Log { get; }
    public event EventHandler? SettingsChanged;          // UI 스레드에서 발생
    public event EventHandler<ProviderView>? ViewChanged; // UI 스레드로 마샬링된 UsageService.ViewChanged
    public void ApplySettings();                         // Normalize → Save → Rotation.Configure/UpdateProviders → 시작 프로그램 레지스트리 → SettingsChanged
    public IReadOnlyList<ProviderView> EnabledViewsInOrder(); // 설정 순서 ∩ Enabled
    public void Start();
    public Task RefreshNowAsync();
    public void Shutdown();                              // 저장·정리 후 Application.Current.Shutdown()
    public ValueTask DisposeAsync();
}
```
- `AITokenUsage.Taskbar`: `NativeMethods`(internal static partial), `TaskbarLocator`, `ThemeWatcher`(`bool IsTaskbarLight`, `bool IsAppLight`, `event EventHandler? Changed`), `ShellEventWindow`(`event TaskbarCreated`, `DisplayChanged`, `ImmersiveColorSetChanged`), `TaskbarGeometryReader`, `FullscreenWatcher`, `TaskbarHost`(`TaskbarHost(Func<WidgetWindow> createWindow, AppHost host, ShellEventWindow shell)`, `Start()`, `Dispose()`, `bool IsEmbedded`, `event EventHandler<WidgetWindow>? WindowCreated`).
- `AITokenUsage.UI`: `WidgetWindow(WidgetViewModel vm)`(속성 `FrameworkElement RootElement`), `WidgetViewModel(AppHost host, ThemeWatcher theme)`, `ProviderPanel`(UserControl), `ProviderGlyphs`(아이콘 Geometry + 브랜드 색), `DetailFlyout`, `SettingsWindow(AppHost host, ThemeWatcher theme)`, `TrayIconController(AppHost host)`.

---

## 작업 목록 (Phase / Task / 산출물)

**Phase 0 — 저장소**
- Task 1: git init, `.gitignore`, `Directory.Build.props`, 솔루션과 3개 프로젝트, `app.manifest`, 스모크 테스트 1개, 빈 WPF App(창 없이 시작·종료 가능). 첫 커밋에 `docs/` 포함.

**Phase 1 — Core 기반**
- Task 2: Models + Abstractions + 실제 구현(RealFileSystem 등) + `HttpClientFactory` + 테스트 가짜 전부. 테스트: `RealFileSystem.ReadAllTextShared`가 다른 핸들이 `FileShare.ReadWrite`로 쓰기 중이어도 읽히는지, `ReadLinesReverse`, `DpapiSecretStore` 왕복, `ProviderView` 규칙.
- Task 3: Util (`Jwt`, `ResetTimeFormatter`, `SeverityRules`, `Json`, `SharedJson`). 테스트 다수.
- Task 4: Settings (`AppSettings`, `ProviderDefaults`, `SettingsStore`, `Normalize`). 테스트.

**Phase 2 — Core 엔진**
- Task 5: Polling (`BackoffPolicy`, `SnapshotCache`, `UsageService`). FakeTimeProvider 테스트.
- Task 6: `RotationController`. 테스트.
- Task 7: `PixelRect`, `TaskbarGeometry`, `TaskbarLayoutCalculator`. 테스트.

**Phase 3 — 공급자**
- Task 8: Claude (`ClaudePaths`, `ClaudeCredentialReader`, `ClaudeVersionProbe`, `ClaudeUsageParser`, `ClaudeUsageProvider`) + Fixtures + 테스트.
- Task 9: Codex 1 (`CodexPaths`, `CodexAuthReader`, `CodexWindowLabeler`, `CodexUsageParser`(wham)) + 테스트.
- Task 10: Codex 2 (`CodexRolloutReader`(A–D), `CodexAppServerClient`, `CodexUsageProvider` 조합) + 테스트.
- Task 11: Gemini (`GeminiPaths`, `GeminiCredentialReader`, `GeminiTokenRefresher`, `GeminiQuotaParser`, `GeminiUsageProvider`) + 테스트.
- Task 12: Copilot (`CopilotTokenSources`, `CopilotUsageParser`, `CopilotUsageProvider` + 로그인 멤버) + 테스트.
- Task 13: `GitHubDeviceFlow` + `ProviderCatalog` + 테스트.

**Phase 4 — 앱**
- Task 14: `App.xaml`(ShutdownMode=OnExplicitShutdown), 단일 인스턴스(Mutex + 이름 있는 이벤트), `AppHost`(합성 루트), 전역 예외 처리, `TrayIconController`(트레이 아이콘 + 메뉴 `지금 새로고침`·`자동 전환 일시정지`·`종료`, 숫자 아이콘 렌더링). 수동 확인: 실행 시 트레이 아이콘·툴팁에 Claude 값.
- Task 15: `NativeMethods`, `TaskbarLocator`(explorer 소유 확인, 보조 작업 표시줄, 모니터 매칭), `ThemeWatcher`, `ShellEventWindow`. 수동 확인용 디버그 로그.
- Task 16: `TaskbarGeometryReader`(UIA 전용 스레드), `FullscreenWatcher`, `TaskbarHost`(자식 부착, 500ms 조정 루프, 재생성, overlay 대체). 이 시점엔 임시 위젯 창 대신 Task 17의 `WidgetWindow`가 필요하므로 Task 16은 `WidgetWindow`의 최소 버전(빈 루트 Border, `#01000000`, 176×40)을 만들고 Task 17이 확장한다.
- Task 17: `ProviderGlyphs`, `ProviderPanel`, `WidgetViewModel`, `WidgetWindow` 완성(2줄 미니바, 점, 상태별 표시, 슬라이드 전환, 클릭/휠/호버, 우클릭 메뉴). 스크린샷 확인.
- Task 18: `DetailFlyout`(호버 400ms/200ms, 위젯 위 배치, 한도별 넓은 바·리셋 시각·상태 줄).
- Task 19: `SettingsWindow`(모든 설정 즉시 반영, AI 순서 ▲▼, 줄 선택, Copilot 기기 코드 로그인 UI, 시작 프로그램 레지스트리) + 트레이/위젯 메뉴에 `설정…` 추가 + 두 번째 실행 시 설정 창 열기.
- Task 20: 앱 아이콘(.ico 생성), `README.md`(설치·실행·한계), `publish` 명령 검증, 최종 수동 점검 체크리스트(실제 Claude 조회, 전환, 호버, 설정 반영, 탐색기 재시작 복구는 사용자 동의 후).
