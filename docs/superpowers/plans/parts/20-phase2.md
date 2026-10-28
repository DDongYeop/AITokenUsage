## Phase 2 — Core 엔진

이 Phase는 UI와 무관한 세 엔진을 만든다. 세 Task 모두 `AITokenUsage.Core` 안에서 끝나고, 시간이 들어가는 동작은 전부 `FakeTimeProvider`로 결정적으로 검증한다.

- **Task 5**: 공급자 폴링(`UsageService`), 재시도 간격(`BackoffPolicy`), 마지막 정상 값 캐시(`SnapshotCache`)
- **Task 6**: AI 자동 전환 상태 기계(`RotationController`)
- **Task 7**: 작업 표시줄 안 위젯 사각형 계산(`TaskbarLayoutCalculator`)

**이 Phase가 기대하는 Phase 0–1 산출물**
- Task 1: 테스트 프로젝트가 `xunit` 2.9.3과 `Microsoft.Extensions.TimeProvider.Testing` 10.11.0을 참조한다. 아래 테스트 파일은 모두 `using Xunit;`을 직접 적으므로, 템플릿의 전역 using 유무와 상관없이 컴파일된다(중복이어도 경고 없음).
- Task 2: `AITokenUsage.Core.Models`(`UsageSnapshot`, `UsageMeter`, `ProviderStatus`, `Severity`, `ProviderView`, `MeterOption`), `AITokenUsage.Core.Abstractions.IAppLog`, `AITokenUsage.Core.Providers.IUsageProvider`, 테스트 가짜 `AITokenUsage.Core.Tests.Fakes.NullLog`.
- Task 4: `AITokenUsage.Core.Settings.WidgetPlacement`.

---

### Task 5: Polling — `BackoffPolicy`, `SnapshotCache`, `UsageService`

**Files:**
- Create: `src/AITokenUsage.Core/Polling/BackoffPolicy.cs`
- Create: `src/AITokenUsage.Core/Polling/SnapshotCache.cs`
- Create: `src/AITokenUsage.Core/Polling/WatchPathMatcher.cs`
- Create: `src/AITokenUsage.Core/Polling/UsageService.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fakes/FakeUsageProvider.cs`
- Create: `tests/AITokenUsage.Core.Tests/Fakes/ViewRecorder.cs`
- Test: `tests/AITokenUsage.Core.Tests/Polling/BackoffPolicyTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Polling/SnapshotCacheTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Polling/WatchPathMatcherTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Polling/UsageServiceTests.cs`

**Interfaces:**
- Consumes:
  - `record UsageSnapshot(string ProviderId, ProviderStatus Status, string? PlanLabel, IReadOnlyList<UsageMeter> Meters, DateTimeOffset FetchedAt, string? StatusMessage = null, TimeSpan? RetryAfter = null, string? ExtraLine = null)`와 `static UsageSnapshot WithStatus(string providerId, ProviderStatus status, DateTimeOffset now, string? message = null, TimeSpan? retryAfter = null)` (Task 2)
  - `record UsageMeter(string Key, string ShortLabel, string LongLabel, double? UsedPercent, DateTimeOffset? ResetsAt, Severity? ServerSeverity = null, string? TextOverride = null)` (Task 2)
  - `record ProviderView(string ProviderId, string DisplayName, UsageSnapshot Current, UsageSnapshot? LastGood)` — `IsStale`, `DisplayMeters`, `DisplayPlan`, `HasAnyData` (Task 2)
  - `interface IUsageProvider { string Id; string DisplayName; TimeSpan MinInterval; IReadOnlyList<string> WatchPaths; IReadOnlyList<MeterOption> StaticMeterOptions; Task<UsageSnapshot> FetchAsync(CancellationToken ct); }` (Task 2)
  - `interface IAppLog { void Info(string); void Warn(string, Exception? ex = null); void Error(string, Exception? ex = null); }`, 테스트용 `NullLog` (Task 2)
- Produces:
  - `public sealed class BackoffPolicy`
    - `public TimeSpan NextDelay(UsageSnapshot result, TimeSpan baseInterval, int consecutiveFailures)`
    - `public static TimeSpan Jitter(TimeSpan delay, double unit01)` — `unit01` 0 → 0.9배, 0.5 → 1.0배, 1 → 1.1배(범위 밖은 클램프)
    - `public static readonly TimeSpan RateLimitFloor`(5분), `RateLimitCap`(60분), `ErrorFloor`(2분), `ErrorCap`(30분); `public const double JitterFraction = 0.10`
  - `public sealed partial class SnapshotCache`
    - `public SnapshotCache(string filePath, IAppLog log)`
    - `public IReadOnlyDictionary<string, UsageSnapshot> Load()` — 없음·손상·버전 불일치면 빈 사전, 절대 throw 안 함
    - `public void Save(IReadOnlyDictionary<string, UsageSnapshot> lastGood)` — 임시 파일 + `File.Move(overwrite)`, 실패는 Warn 로그만
    - `public const int FormatVersion = 1`
  - `public static class WatchPathMatcher { public static bool Matches(string? fullPath, string? watchPath); }`
  - `public sealed class UsageService : IAsyncDisposable`
    - `public UsageService(IReadOnlyList<IUsageProvider> providers, Func<TimeSpan> refreshInterval, SnapshotCache cache, TimeProvider time, IAppLog log, Func<double>? random = null)` — 공유 계약에 선택 인자 `random` 추가(지터 결정성). 기본값 `Random.Shared.NextDouble`
    - `public event EventHandler<ProviderView>? ViewChanged` — 백그라운드 스레드에서 발생
    - `public IReadOnlyList<ProviderView> Views { get; }` — 등록 순서
    - `public ProviderView? GetView(string providerId)`
    - `public void Start()`
    - `public Task RefreshNowAsync(string? providerId = null)`
    - `public void NotifyPathChanged(string fullPath)`
    - `public ValueTask DisposeAsync()`
    - `public const string LoadingMessage = "불러오는 중…"`, `public const string UnknownErrorMessage = "알 수 없는 오류"`
    - `public static readonly TimeSpan DebounceDelay`(1.5초), `MinimumDelay`(30초), `DefaultRefreshInterval`(5분)
  - 테스트 가짜: `FakeUsageProvider(string id, TimeProvider time, TimeSpan? minInterval = null, IReadOnlyList<string>? watchPaths = null)` — `ThenOk(double usedPercent, string plan = "Pro")`, `ThenStatus(ProviderStatus status, TimeSpan? retryAfter = null, string? message = null)`, `ThenThrow(Exception)`, `int CallCount`, `static UsageSnapshot OkSnapshot(string providerId, double usedPercent, DateTimeOffset now, string plan = "Pro")`. `ViewRecorder(UsageService service)` — `Task<ProviderView> NextAsync()`, `Task AssertNoneAsync(int milliseconds = 300)`, `int Count`.
  - **다음 Task가 지켜야 할 의미 규칙**
    - 캐시가 없는 공급자의 초기 뷰는 `Current = WithStatus(id, Ok, now, LoadingMessage)`(미터 0개), `LastGood = null`이다. UI(Task 17·18)는 `Status == Ok && Meters.Count == 0`이면 `StatusMessage`("불러오는 중…")를 보여 준다.
    - `RateLimited`·`Error` 결과의 `Current.RetryAfter`는 서비스가 **실제 다음 자동 조회까지의 지연**으로 바꿔 넣는다. 상세 카드·설정 창의 "요청 제한 — 14:20 이후 재시도"는 `Current.FetchedAt + Current.RetryAfter`로 계산한다.
    - `FileSystemWatcher`는 앱(Task 14 `AppHost`)이 만든다. 공급자의 각 `WatchPaths` 항목이 파일이면 부모 디렉터리 + 파일명 필터로, 디렉터리면 `IncludeSubdirectories = true`로 감시하고, `Changed`/`Created`/`Renamed`/`Deleted`에서 `Usage.NotifyPathChanged(e.FullPath)`를 호출한다. 디바운스·매칭·레이트 리밋 판단은 서비스가 한다.

**동작 규칙** (구현과 리뷰의 기준. 아래 코드가 이 규칙을 그대로 구현한다)

| # | 규칙 |
|---|---|
| 1 | `Start()`는 즉시 반환한다. 백그라운드(`Task.Run`)에서 `SnapshotCache.Load()` → 캐시에 있는 공급자마다 `Current = LastGood = 캐시 스냅샷`으로 바꾸고 `ViewChanged` → 공급자마다 `Task.Run` 루프 1개를 띄운다. 루프는 첫 조회를 바로 한다. UI 스레드는 캐시 파일 I/O도 하지 않는다. |
| 2 | 간격: `base = max(refreshInterval(), provider.MinInterval)`. `refreshInterval()`은 매 주기 다시 읽는다(설정 변경이 다음 주기부터 반영). 0 이하이거나 예외면 5분. |
| 3 | `policy = BackoffPolicy.NextDelay(result, base, 연속 실패 수)`, `delay = Jitter(policy, random())`. **RateLimited**면 `delay = max(delay, policy)`이고 `BlockedUntil = now + policy`다(차단은 지터로 줄지 않는다). 그 외에는 `delay = max(delay, MinInterval)`이고 차단을 푼다. 마지막에 `max(delay, 30초)`. |
| 4 | 연속 실패 수: 결과가 `RateLimited`나 `Error`면 +1, 그 밖의 상태면 0으로 초기화. |
| 5 | `BackoffPolicy`: Ok·기타 → `base`. RateLimited → `max(RetryAfter ?? 5분, 5분) × 2^(n−1)`, 최대 60분. Error → `max(base, 2분) × 2^(n−1)`, 최대 30분. |
| 6 | 병합: `Current`는 항상 최신 결과. `LastGood`는 결과가 Ok일 때만 바뀐다. RateLimited/Error 결과는 `RetryAfter`를 실제 지연으로 바꿔 넣는다. |
| 7 | Ok 결과 직후 모든 공급자의 `LastGood`를 `SnapshotCache.Save`로 저장한다(잠금으로 직렬화). 실패 결과는 캐시를 건드리지 않는다. |
| 8 | 공급자 예외(동기·비동기, `HttpClient` 타임아웃 포함)는 `Error` + `"알 수 없는 오류"`가 된다. 로그에는 공급자 id와 예외만 남긴다. 종료 중 취소는 조용히 루프를 끝낸다. |
| 9 | `ViewChanged`는 매 조회 후와 캐시 복원 때 백그라운드 스레드에서 발생한다. 구독자를 하나씩 따로 호출하고, 구독자 예외는 로그만 남긴다. **다음 타이머를 먼저 건 뒤에** 이벤트를 올린다(테스트 결정성). |
| 10 | `RefreshNowAsync(id?)`: 차단 중인 공급자는 건너뛴다. 나머지는 루프를 깨워 즉시 조회하고, 그 결과가 게시되면 완료된다. 자동 조회 타이머는 그 조회 시점부터 다시 센다. 조회 중에 들어온 요청은 그다음 조회가 처리한다. `Start` 전과 `Dispose` 후에는 아무 일도 하지 않는다. |
| 11 | `NotifyPathChanged(path)`: `WatchPathMatcher.Matches`(같은 경로 또는 `WatchPath + 구분자`로 시작, 대소문자 무시, `/`와 `\` 동일, 끝 구분자 무시)가 맞는 공급자마다 1.5초 디바운스한다. 디바운스 중 새 변경이 오면 1.5초를 다시 센다. 발화 시점에 차단 중이면 버린다. |
| 12 | 공급자당 동시 요청은 1개다. 조회는 루프 안에서만 일어난다. |
| 13 | `DisposeAsync`: 취소 → 루프 종료를 최대 5초 기다림 → 남은 요청을 완료 처리. 두 번 불러도 안전하다. 이후 `Start()`는 `ObjectDisposedException`. |

**`cache.json` 형태** (`%LOCALAPPDATA%\AITokenUsage\cache.json`; Ok 스냅샷만 저장하므로 `status`·`retryAfter`는 없다. null 값은 생략. 실제 파일에서는 비ASCII 문자가 기본 인코더 때문에 `\uXXXX`로 이스케이프되지만 왕복에는 영향이 없다)

```json
{
  "version": 1,
  "providers": {
    "claude": {
      "planLabel": "Team · Max 5x",
      "fetchedAt": "2026-10-28T03:00:00+00:00",
      "extraLine": "추가 사용 $12.40 / $50.00",
      "meters": [
        {
          "key": "session",
          "shortLabel": "5h",
          "longLabel": "현재 세션 (5시간)",
          "usedPercent": 14,
          "resetsAt": "2026-10-28T06:50:00+00:00",
          "serverSeverity": "normal"
        },
        {
          "key": "weekly",
          "shortLabel": "주",
          "longLabel": "주간 · 모든 모델",
          "usedPercent": 50,
          "resetsAt": "2026-10-30T05:00:00+00:00"
        }
      ]
    },
    "copilot": {
      "planLabel": "Copilot Pro+",
      "fetchedAt": "2026-10-28T03:00:00+00:00",
      "meters": [
        { "key": "reset", "shortLabel": "↻", "longLabel": "리셋", "textOverride": "↻ 11/1" }
      ]
    }
  }
}
```

- 직렬화는 **소스 생성 `JsonSerializerContext`**를 쓴다. 리플렉션 기반 직렬화가 꺼진 환경(트리밍/AOT, .NET 10 파일 기반 앱 기본값)에서도 같은 코드가 동작한다. 실제로 리플렉션 옵션 방식은 그런 환경에서 `InvalidOperationException: Reflection-based serialization has been disabled`로 실패한다.
- `serverSeverity`는 `"normal"|"warning"|"critical"` 문자열로 직접 매핑한다. 모르는 값은 그 미터의 severity만 `null`이 되고, 캐시 전체를 버리지 않는다.
- 손상(잘못된 JSON, 타입 불일치, 날짜 형식 오류)이면 빈 캐시로 시작하고 `Warn` 로그에는 **예외 타입 이름만** 남긴다(파일 내용은 절대 로그에 넣지 않는다). 다음 Ok 조회가 파일을 새로 쓴다.

**테스트 타이밍 규칙** (`UsageServiceTests` 상단 주석에도 적는다)
- 가짜 시계는 직전 조회가 관찰된 뒤(`ViewRecorder.NextAsync()` 또는 `await RefreshNowAsync()`)에만 움직인다.
- `Advance` 한 번으로 예정 시각을 둘 이상 넘기지 않는다.
- "조회가 일어나지 않음"은 `AssertNoneAsync()`(실시간 300ms 대기)로 확인한다.
- 서비스는 다음 타이머를 건 **뒤에** `ViewChanged`를 올리므로, 이 규칙만 지키면 모든 일정이 결정적이다.

- [ ] **Step 1: `BackoffPolicy` 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Polling/BackoffPolicyTests.cs`:

```csharp
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

public class BackoffPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private readonly BackoffPolicy _policy = new();

    private static UsageSnapshot Snap(ProviderStatus status, TimeSpan? retryAfter = null) =>
        UsageSnapshot.WithStatus("claude", status, Now, null, retryAfter);

    [Fact]
    public void Ok_ReturnsBaseInterval()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.Ok), FiveMinutes, 0));
    }

    [Theory]
    [InlineData(ProviderStatus.NotInstalled)]
    [InlineData(ProviderStatus.SignedOut)]
    [InlineData(ProviderStatus.WaitingForCliRefresh)]
    [InlineData(ProviderStatus.Unsupported)]
    public void OtherStatuses_ReturnBaseInterval(ProviderStatus status)
    {
        Assert.Equal(TimeSpan.FromMinutes(10), _policy.NextDelay(Snap(status), TimeSpan.FromMinutes(10), 3));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(50, 60)]
    public void RateLimited_WithoutRetryAfter_StartsAtFiveMinutesAndDoublesToSixtyMinuteCap(int failures, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            _policy.NextDelay(Snap(ProviderStatus.RateLimited), TimeSpan.FromMinutes(2), failures));
    }

    [Fact]
    public void RateLimited_ZeroRetryAfter_UsesFiveMinuteFloor()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.RateLimited, TimeSpan.Zero), FiveMinutes, 1));
    }

    [Fact]
    public void RateLimited_RetryAfterBelowFloor_UsesFiveMinutes()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.RateLimited, TimeSpan.FromSeconds(30)), FiveMinutes, 1));
    }

    [Fact]
    public void RateLimited_LongRetryAfter_IsHonouredThenDoubledAndCapped()
    {
        var snapshot = Snap(ProviderStatus.RateLimited, TimeSpan.FromMinutes(20));
        Assert.Equal(TimeSpan.FromMinutes(20), _policy.NextDelay(snapshot, FiveMinutes, 1));
        Assert.Equal(TimeSpan.FromMinutes(40), _policy.NextDelay(snapshot, FiveMinutes, 2));
        Assert.Equal(TimeSpan.FromMinutes(60), _policy.NextDelay(snapshot, FiveMinutes, 3));
    }

    [Fact]
    public void RateLimited_ZeroFailureCount_IsTreatedAsFirstFailure()
    {
        Assert.Equal(FiveMinutes, _policy.NextDelay(Snap(ProviderStatus.RateLimited), FiveMinutes, 0));
    }

    [Theory]
    [InlineData(5, 1, 5)]
    [InlineData(5, 2, 10)]
    [InlineData(5, 3, 20)]
    [InlineData(5, 4, 30)]
    [InlineData(1, 1, 2)]
    [InlineData(1, 2, 4)]
    [InlineData(15, 2, 30)]
    public void Error_StartsAtMaxOfBaseAndTwoMinutes_DoublesToThirtyMinuteCap(int baseMinutes, int failures, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            _policy.NextDelay(Snap(ProviderStatus.Error), TimeSpan.FromMinutes(baseMinutes), failures));
    }

    [Theory]
    [InlineData(0.0, 270)]
    [InlineData(0.25, 285)]
    [InlineData(0.5, 300)]
    [InlineData(1.0, 330)]
    [InlineData(-3.0, 270)]
    [InlineData(7.0, 330)]
    public void Jitter_MapsUnitIntervalToPlusMinusTenPercent(double unit, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackoffPolicy.Jitter(FiveMinutes, unit));
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~BackoffPolicyTests"`
Expected: 빌드 실패. `error CS0234: The type or namespace name 'Polling' does not exist in the namespace 'AITokenUsage.Core'`

- [ ] **Step 3: `BackoffPolicy` 구현**

`src/AITokenUsage.Core/Polling/BackoffPolicy.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Polling;

/// <summary>
/// Decides how long to wait before the next automatic fetch of one provider.
/// </summary>
public sealed class BackoffPolicy
{
    public static readonly TimeSpan RateLimitFloor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RateLimitCap = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan ErrorFloor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ErrorCap = TimeSpan.FromMinutes(30);
    public const double JitterFraction = 0.10;

    // 2^16 already exceeds both caps by far; clamping the exponent keeps TimeSpan math from overflowing.
    private const int MaxExponent = 16;

    /// <param name="result">The snapshot that was just fetched.</param>
    /// <param name="baseInterval">max(configured refresh interval, provider.MinInterval).</param>
    /// <param name="consecutiveFailures">Number of consecutive RateLimited/Error results including this one (values below 1 count as 1).</param>
    public TimeSpan NextDelay(UsageSnapshot result, TimeSpan baseInterval, int consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(result);
        int n = Math.Max(1, consecutiveFailures);

        return result.Status switch
        {
            ProviderStatus.RateLimited => Exponential(Max(result.RetryAfter ?? RateLimitFloor, RateLimitFloor), n, RateLimitCap),
            ProviderStatus.Error => Exponential(Max(baseInterval, ErrorFloor), n, ErrorCap),
            _ => baseInterval,
        };
    }

    /// <summary>Scales <paramref name="delay"/> into [0.9x, 1.1x]. unit01 = 0 → 0.9x, 0.5 → 1.0x, 1 → 1.1x.</summary>
    public static TimeSpan Jitter(TimeSpan delay, double unit01)
    {
        double u = double.IsNaN(unit01) ? 0.5 : Math.Clamp(unit01, 0.0, 1.0);
        double factor = 1.0 + ((u * 2.0) - 1.0) * JitterFraction;
        return TimeSpan.FromTicks((long)Math.Round(delay.Ticks * factor, MidpointRounding.AwayFromZero));
    }

    private static TimeSpan Exponential(TimeSpan start, int n, TimeSpan cap)
    {
        double factor = Math.Pow(2, Math.Min(n - 1, MaxExponent));
        double ticks = Math.Min(start.Ticks * factor, cap.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
}
```

- [ ] **Step 4: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~BackoffPolicyTests"`
Expected: `Passed!  - Failed: 0, Passed: 28`

- [ ] **Step 5: `SnapshotCache` 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Polling/SnapshotCacheTests.cs`:

```csharp
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

public sealed class SnapshotCacheTests : IDisposable
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AITokenUsage.Tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingLog _log = new();

    private string CachePath => Path.Combine(_dir, "nested", "cache.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static UsageSnapshot ClaudeSnapshot() => new(
        "claude",
        ProviderStatus.Ok,
        "Team · Max 5x",
        [
            new UsageMeter("session", "5h", "현재 세션 (5시간)", 14.5, new DateTimeOffset(2026, 10, 28, 6, 50, 0, TimeSpan.Zero), Severity.Normal),
            new UsageMeter("weekly", "주", "주간 · 모든 모델", 50, new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero), null),
            new UsageMeter("weekly:fable", "주·Fa", "주간 · Fable", 91, null, Severity.Critical, null),
        ],
        FetchedAt,
        "로그 기준 · 12:00",
        null,
        "추가 사용 $12.40 / $50.00");

    private static UsageSnapshot CopilotSnapshot() => new(
        "copilot",
        ProviderStatus.Ok,
        "Copilot Pro+",
        [new UsageMeter("reset", "↻", "리셋", null, null, null, "↻ 11/1")],
        FetchedAt);

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var cache = new SnapshotCache(CachePath, _log);

        Assert.Empty(cache.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var cache = new SnapshotCache(CachePath, _log);
        var claude = ClaudeSnapshot();
        var copilot = CopilotSnapshot();

        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = claude, ["copilot"] = copilot });
        var loaded = new SnapshotCache(CachePath, _log).Load();

        Assert.Equal(2, loaded.Count);
        var c = loaded["claude"];
        Assert.Equal("claude", c.ProviderId);
        Assert.Equal(ProviderStatus.Ok, c.Status);
        Assert.Equal("Team · Max 5x", c.PlanLabel);
        Assert.Equal(FetchedAt, c.FetchedAt);
        Assert.Equal("로그 기준 · 12:00", c.StatusMessage);
        Assert.Equal("추가 사용 $12.40 / $50.00", c.ExtraLine);
        Assert.Null(c.RetryAfter);
        Assert.Equal(claude.Meters, c.Meters);
        Assert.Equal("↻ 11/1", Assert.Single(loaded["copilot"].Meters).TextOverride);
    }

    [Fact]
    public void Save_WritesDocumentedJsonShape()
    {
        var cache = new SnapshotCache(CachePath, _log);

        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = ClaudeSnapshot() });

        using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var claude = root.GetProperty("providers").GetProperty("claude");
        Assert.Equal("Team · Max 5x", claude.GetProperty("planLabel").GetString());
        Assert.Equal(FetchedAt, claude.GetProperty("fetchedAt").GetDateTimeOffset());
        var meter = claude.GetProperty("meters")[0];
        Assert.Equal("session", meter.GetProperty("key").GetString());
        Assert.Equal("5h", meter.GetProperty("shortLabel").GetString());
        Assert.Equal(14.5, meter.GetProperty("usedPercent").GetDouble());
        Assert.Equal("normal", meter.GetProperty("serverSeverity").GetString());
        Assert.False(claude.TryGetProperty("status", out _));
        Assert.False(File.Exists(CachePath + ".tmp"));
    }

    [Fact]
    public void Save_OverwritesPreviousContent()
    {
        var cache = new SnapshotCache(CachePath, _log);
        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = ClaudeSnapshot() });

        cache.Save(new Dictionary<string, UsageSnapshot> { ["copilot"] = CopilotSnapshot() });

        var loaded = cache.Load();
        Assert.Equal("copilot", Assert.Single(loaded).Key);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\":\"one-secret-looking-value\"}")]
    [InlineData("{\"version\":1,\"providers\":{\"claude\":{\"fetchedAt\":\"not a date\"}}}")]
    public void Load_CorruptFile_ReturnsEmptyAndWarnsWithoutContent(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, content);

        var loaded = new SnapshotCache(CachePath, _log).Load();

        Assert.Empty(loaded);
        string warning = Assert.Single(_log.Warnings);
        Assert.DoesNotContain("one-secret-looking-value", warning);
        Assert.DoesNotContain("not a date", warning);
    }

    [Fact]
    public void Load_UnknownSeverity_KeepsMeterWithNullSeverity()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath,
            "{\"version\":1,\"providers\":{\"claude\":{\"fetchedAt\":\"2026-10-28T03:00:00+00:00\",\"meters\":[{\"key\":\"session\",\"serverSeverity\":\"bogus\"},{\"key\":\"weekly\",\"serverSeverity\":\"warning\"}]}}}");

        var meters = new SnapshotCache(CachePath, _log).Load()["claude"].Meters;

        Assert.Equal(2, meters.Count);
        Assert.Null(meters[0].ServerSeverity);
        Assert.Equal(Severity.Warning, meters[1].ServerSeverity);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Load_UnknownVersion_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, "{\"version\":99,\"providers\":{\"claude\":{\"fetchedAt\":\"2026-10-28T03:00:00+00:00\",\"meters\":[]}}}");

        Assert.Empty(new SnapshotCache(CachePath, _log).Load());
    }

    [Fact]
    public void Load_SkipsNullEntriesAndMetersWithoutKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, """
            {
              "version": 1,
              "providers": {
                "claude": {
                  "planLabel": "Pro",
                  "fetchedAt": "2026-10-28T03:00:00+00:00",
                  "meters": [
                    null,
                    { "shortLabel": "x" },
                    { "key": "weekly", "shortLabel": "주", "longLabel": "주간 · 모든 모델", "usedPercent": 50, "futureField": true }
                  ]
                },
                "codex": null
              },
              "unknownTopLevel": 1
            }
            """);

        var loaded = new SnapshotCache(CachePath, _log).Load();

        var claude = Assert.Single(loaded).Value;
        var meter = Assert.Single(claude.Meters);
        Assert.Equal("weekly", meter.Key);
        Assert.Equal(50, meter.UsedPercent);
        Assert.Null(meter.ResetsAt);
    }

    [Fact]
    public void Save_UnwritableLocation_DoesNotThrowAndWarns()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "file, not a directory");
        var cache = new SnapshotCache(Path.Combine(blocker, "cache.json"), _log);

        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = ClaudeSnapshot() });

        Assert.Single(_log.Warnings);
    }

    private sealed class CapturingLog : IAppLog
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) =>
            Warnings.Add(ex is null ? message : message + " " + ex.Message);

        public void Error(string message, Exception? ex = null) =>
            Warnings.Add(ex is null ? message : message + " " + ex.Message);
    }
}
```

- [ ] **Step 6: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~SnapshotCacheTests"`
Expected: 빌드 실패. `error CS0246: The type or namespace name 'SnapshotCache' could not be found`

- [ ] **Step 7: `SnapshotCache` 구현**

`src/AITokenUsage.Core/Polling/SnapshotCache.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Polling;

/// <summary>
/// Persists the last good (Ok) snapshot of each provider to cache.json.
/// Only display data is stored: never tokens, headers or raw API bodies.
/// Never throws: a missing or corrupt file loads as an empty cache, and write failures are logged.
/// Uses a source-generated serializer context, so it also works where reflection-based JSON is disabled (trim/AOT).
/// </summary>
public sealed partial class SnapshotCache
{
    public const int FormatVersion = 1;

    private readonly string _filePath;
    private readonly IAppLog _log;
    private readonly object _writeGate = new();

    public SnapshotCache(string filePath, IAppLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(log);
        _filePath = filePath;
        _log = log;
    }

    public IReadOnlyDictionary<string, UsageSnapshot> Load()
    {
        var result = new Dictionary<string, UsageSnapshot>(StringComparer.Ordinal);
        CacheFileDto? file;
        try
        {
            if (!File.Exists(_filePath))
            {
                return result;
            }

            string json = File.ReadAllText(_filePath);
            file = JsonSerializer.Deserialize(json, CacheJsonContext.Default.CacheFileDto);
        }
        catch (JsonException ex)
        {
            // Log only the exception type: never echo file content.
            _log.Warn($"Snapshot cache is corrupt and was ignored ({ex.GetType().Name}).");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warn("Snapshot cache could not be read.", ex);
            return result;
        }

        if (file is null || file.Version != FormatVersion || file.Providers is null)
        {
            return result;
        }

        foreach (var (providerId, dto) in file.Providers)
        {
            if (string.IsNullOrWhiteSpace(providerId) || dto is null)
            {
                continue;
            }

            result[providerId] = ToSnapshot(providerId, dto);
        }

        return result;
    }

    public void Save(IReadOnlyDictionary<string, UsageSnapshot> lastGood)
    {
        ArgumentNullException.ThrowIfNull(lastGood);
        var file = new CacheFileDto
        {
            Version = FormatVersion,
            Providers = lastGood.ToDictionary(kv => kv.Key, kv => ToDto(kv.Value), StringComparer.Ordinal),
        };

        lock (_writeGate)
        {
            string tempPath = _filePath + ".tmp";
            try
            {
                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tempPath, JsonSerializer.Serialize(file, CacheJsonContext.Default.CacheFileDto));
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.Warn("Snapshot cache could not be written.", ex);
            }
        }
    }

    private static UsageSnapshot ToSnapshot(string providerId, CachedSnapshotDto dto)
    {
        var meters = new List<UsageMeter>();
        foreach (CachedMeterDto m in dto.Meters ?? [])
        {
            if (m is null || string.IsNullOrEmpty(m.Key))
            {
                continue;
            }

            meters.Add(new UsageMeter(
                m.Key,
                m.ShortLabel ?? "",
                m.LongLabel ?? "",
                m.UsedPercent,
                m.ResetsAt,
                ParseSeverity(m.ServerSeverity),
                m.TextOverride));
        }

        // Only Ok snapshots are ever cached, so the restored status is always Ok.
        return new UsageSnapshot(providerId, ProviderStatus.Ok, dto.PlanLabel, meters, dto.FetchedAt,
            dto.StatusMessage, RetryAfter: null, dto.ExtraLine);
    }

    private static CachedSnapshotDto ToDto(UsageSnapshot s) => new()
    {
        PlanLabel = s.PlanLabel,
        FetchedAt = s.FetchedAt,
        StatusMessage = s.StatusMessage,
        ExtraLine = s.ExtraLine,
        Meters = s.Meters.Select(m => new CachedMeterDto
        {
            Key = m.Key,
            ShortLabel = m.ShortLabel,
            LongLabel = m.LongLabel,
            UsedPercent = m.UsedPercent,
            ResetsAt = m.ResetsAt,
            ServerSeverity = FormatSeverity(m.ServerSeverity),
            TextOverride = m.TextOverride,
        }).ToList(),
    };

    private static string? FormatSeverity(Severity? severity) => severity switch
    {
        Severity.Normal => "normal",
        Severity.Warning => "warning",
        Severity.Critical => "critical",
        _ => null,
    };

    // Unknown strings become null (the meter falls back to percent-based severity) instead of discarding the cache.
    private static Severity? ParseSeverity(string? value) => value switch
    {
        "normal" => Severity.Normal,
        "warning" => Severity.Warning,
        "critical" => Severity.Critical,
        _ => null,
    };

    private sealed class CacheFileDto
    {
        public int Version { get; set; }
        public Dictionary<string, CachedSnapshotDto>? Providers { get; set; }
    }

    private sealed class CachedSnapshotDto
    {
        public string? PlanLabel { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
        public string? StatusMessage { get; set; }
        public string? ExtraLine { get; set; }
        public List<CachedMeterDto>? Meters { get; set; }
    }

    private sealed class CachedMeterDto
    {
        public string? Key { get; set; }
        public string? ShortLabel { get; set; }
        public string? LongLabel { get; set; }
        public double? UsedPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public string? ServerSeverity { get; set; }
        public string? TextOverride { get; set; }
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(CacheFileDto))]
    private sealed partial class CacheJsonContext : JsonSerializerContext
    {
    }
}
```

- [ ] **Step 8: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~SnapshotCacheTests"`
Expected: `Passed!  - Failed: 0, Passed: 13`

- [ ] **Step 9: `WatchPathMatcher` 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Polling/WatchPathMatcherTests.cs`:

```csharp
using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

public class WatchPathMatcherTests
{
    [Theory]
    [InlineData(@"C:\Users\test\.claude\.credentials.json", @"C:\Users\test\.claude\.credentials.json", true)]
    [InlineData(@"c:\users\TEST\.CLAUDE\.credentials.json", @"C:\Users\test\.claude\.credentials.json", true)]
    [InlineData(@"C:\Users\test\.codex\auth.json", @"C:\Users\test\.codex", true)]
    [InlineData(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-1.jsonl", @"C:\Users\test\.codex\", true)]
    [InlineData(@"C:\Users\test\.codex", @"C:\Users\test\.codex\", true)]
    [InlineData("C:/Users/test/.gemini/oauth_creds.json", @"C:\Users\test\.gemini", true)]
    [InlineData(@"C:\Users\test\.codex-old\auth.json", @"C:\Users\test\.codex", false)]
    [InlineData(@"C:\Users\test\.claude.json", @"C:\Users\test\.claude", false)]
    [InlineData(@"C:\Users\test", @"C:\Users\test\.claude", false)]
    [InlineData(@"D:\Users\test\.claude\.credentials.json", @"C:\Users\test\.claude", false)]
    [InlineData("", @"C:\Users\test\.claude", false)]
    [InlineData(@"C:\Users\test\.claude\x", "", false)]
    public void Matches_EqualOrDescendant_CaseInsensitive(string fullPath, string watchPath, bool expected)
    {
        Assert.Equal(expected, WatchPathMatcher.Matches(fullPath, watchPath));
    }

    [Fact]
    public void Matches_NullArguments_ReturnFalse()
    {
        Assert.False(WatchPathMatcher.Matches(null, @"C:\a"));
        Assert.False(WatchPathMatcher.Matches(@"C:\a", null));
    }
}
```

- [ ] **Step 10: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~WatchPathMatcherTests"`
Expected: 빌드 실패. `error CS0103: The name 'WatchPathMatcher' does not exist in the current context`

- [ ] **Step 11: `WatchPathMatcher` 구현**

`src/AITokenUsage.Core/Polling/WatchPathMatcher.cs`:

```csharp
namespace AITokenUsage.Core.Polling;

/// <summary>Matches a changed file-system path against a provider's watch path.</summary>
public static class WatchPathMatcher
{
    /// <summary>
    /// True when <paramref name="fullPath"/> equals <paramref name="watchPath"/> or starts with watchPath followed by
    /// a directory separator. Case-insensitive; forward and back slashes are equivalent; trailing separators are ignored.
    /// </summary>
    public static bool Matches(string? fullPath, string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(watchPath))
        {
            return false;
        }

        string path = Normalize(fullPath);
        string watch = Normalize(watchPath);
        if (watch.Length == 0)
        {
            return false;
        }

        if (string.Equals(path, watch, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Length > watch.Length
            && path[watch.Length] == '\\'
            && path.StartsWith(watch, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
```

- [ ] **Step 12: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~WatchPathMatcherTests"`
Expected: `Passed!  - Failed: 0, Passed: 13`

- [ ] **Step 13: 테스트 가짜 `FakeUsageProvider`, `ViewRecorder` 작성**

스크립트대로 결과를 돌려주는 가짜 공급자다. 호출마다 다음 단계를 꺼내고, 스크립트가 끝나면 마지막 단계를 반복한다(빈 스크립트는 Ok 0%).

`tests/AITokenUsage.Core.Tests/Fakes/FakeUsageProvider.cs`:

```csharp
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted provider for UsageService tests. Each FetchAsync call consumes the next scripted step;
/// once the script is exhausted the last step repeats (an empty script returns Ok with 0%).
/// </summary>
public sealed class FakeUsageProvider : IUsageProvider
{
    private readonly object _gate = new();
    private readonly Queue<Func<DateTimeOffset, UsageSnapshot>> _script = new();
    private readonly TimeProvider _time;
    private Func<DateTimeOffset, UsageSnapshot>? _last;
    private int _callCount;

    public FakeUsageProvider(string id, TimeProvider time, TimeSpan? minInterval = null, IReadOnlyList<string>? watchPaths = null)
    {
        Id = id;
        _time = time;
        MinInterval = minInterval ?? TimeSpan.FromMinutes(1);
        WatchPaths = watchPaths ?? [];
    }

    public string Id { get; }
    public string DisplayName => "Fake " + Id;
    public TimeSpan MinInterval { get; }
    public IReadOnlyList<string> WatchPaths { get; }
    public IReadOnlyList<MeterOption> StaticMeterOptions { get; } = [new MeterOption("session", "세션")];
    public int CallCount => Volatile.Read(ref _callCount);

    public static UsageSnapshot OkSnapshot(string providerId, double usedPercent, DateTimeOffset now, string plan = "Pro") =>
        new(providerId, ProviderStatus.Ok, plan,
            [new UsageMeter("session", "5h", "현재 세션 (5시간)", usedPercent, now.AddHours(3), Severity.Normal)],
            now);

    public FakeUsageProvider ThenOk(double usedPercent, string plan = "Pro") =>
        Enqueue(now => OkSnapshot(Id, usedPercent, now, plan));

    public FakeUsageProvider ThenStatus(ProviderStatus status, TimeSpan? retryAfter = null, string? message = null) =>
        Enqueue(now => UsageSnapshot.WithStatus(Id, status, now, message, retryAfter));

    public FakeUsageProvider ThenThrow(Exception exception) =>
        Enqueue(_ => throw exception);

    public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Func<DateTimeOffset, UsageSnapshot> step;
        lock (_gate)
        {
            if (_script.Count > 0)
            {
                _last = _script.Dequeue();
            }

            step = _last ?? (now => OkSnapshot(Id, 0, now));
        }

        Interlocked.Increment(ref _callCount);
        return Task.FromResult(step(_time.GetUtcNow()));
    }

    private FakeUsageProvider Enqueue(Func<DateTimeOffset, UsageSnapshot> step)
    {
        lock (_gate)
        {
            _script.Enqueue(step);
        }

        return this;
    }
}
```

`ViewChanged`(백그라운드 스레드)를 순서대로 기다리게 해 주는 도우미다.

`tests/AITokenUsage.Core.Tests/Fakes/ViewRecorder.cs`:

```csharp
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Records UsageService.ViewChanged events (raised on background threads) and lets tests await them in order.</summary>
public sealed class ViewRecorder
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly List<ProviderView> _views = [];
    private readonly SemaphoreSlim _available = new(0);
    private int _consumed;

    public ViewRecorder(UsageService service)
    {
        service.ViewChanged += (_, view) =>
        {
            lock (_gate)
            {
                _views.Add(view);
            }

            _available.Release();
        };
    }

    public int Count
    {
        get { lock (_gate) { return _views.Count; } }
    }

    /// <summary>Returns the next unconsumed event, waiting up to 5 s (real time) for it.</summary>
    public async Task<ProviderView> NextAsync()
    {
        if (!await _available.WaitAsync(DefaultTimeout))
        {
            throw new TimeoutException("ViewChanged was not raised within 5 seconds.");
        }

        lock (_gate)
        {
            return _views[_consumed++];
        }
    }

    /// <summary>Asserts that no further event arrives within <paramref name="milliseconds"/> of real time.</summary>
    public async Task AssertNoneAsync(int milliseconds = 300)
    {
        bool raised = await _available.WaitAsync(milliseconds);
        Assert.False(raised, "ViewChanged was raised unexpectedly.");
    }
}
```

- [ ] **Step 14: `UsageService` 실패하는 테스트 작성**

다룰 사례: 등록 순서·초기 뷰, 중복 id, 즉시 조회 후 주기 조회, `MinInterval` 우선, 간격 재평가, 지터 ±10%, 음의 지터도 `MinInterval` 아래로 못 감, Error 지수 백오프와 Ok 복귀, RateLimited 차단(수동·경로 변경 무시) 후 해제, 반복 차단 2배, 차단은 지터로 줄지 않음, LastGood/Current 병합, 예외 → "알 수 없는 오류", 수동 새로고침(전체·단일·Start 전), 경로 변경 디바운스(일치·연속 변경·무관 경로), 캐시 저장·복원, 구독자 예외, Dispose.

`tests/AITokenUsage.Core.Tests/Polling/UsageServiceTests.cs`:

```csharp
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

/// <summary>
/// Timing rule for these tests: advance the fake clock only after the previous fetch was observed
/// (ViewRecorder.NextAsync or an awaited RefreshNowAsync), and never past more than one due time per Advance.
/// UsageService arms its next timer before raising ViewChanged, so this keeps every schedule deterministic.
/// </summary>
public sealed class UsageServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private const string CredentialsPath = @"C:\Users\test\.claude\.credentials.json";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AITokenUsage.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(T0);
    private readonly List<UsageService> _services = [];
    private TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);
    private double _random = 0.5;

    private string CachePath => Path.Combine(_dir, "cache.json");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private UsageService CreateService(params IUsageProvider[] providers)
    {
        var service = new UsageService(providers, () => _refreshInterval, new SnapshotCache(CachePath, new NullLog()),
            _time, new NullLog(), () => _random);
        _services.Add(service);
        return service;
    }

    private FakeUsageProvider Provider(string id = "claude", TimeSpan? minInterval = null) =>
        new(id, _time, minInterval, [CredentialsPath]);

    [Fact]
    public void Constructor_ExposesLoadingViewsInRegistrationOrder()
    {
        var service = CreateService(Provider("codex"), Provider("claude"));

        Assert.Equal(new[] { "codex", "claude" }, service.Views.Select(v => v.ProviderId));
        var view = service.GetView("claude")!;
        Assert.Equal("Fake claude", view.DisplayName);
        Assert.Equal(ProviderStatus.Ok, view.Current.Status);
        Assert.Equal(UsageService.LoadingMessage, view.Current.StatusMessage);
        Assert.Null(view.LastGood);
        Assert.Null(service.GetView("gemini"));
    }

    [Fact]
    public void Constructor_DuplicateIds_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService(Provider("claude"), Provider("claude")));
    }

    [Fact]
    public async Task Start_FetchesImmediately_ThenEveryInterval()
    {
        var provider = Provider().ThenOk(10).ThenOk(20).ThenOk(30);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var first = await views.NextAsync();
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(10, first.Current.Meters[0].UsedPercent);
        Assert.Same(first.Current, first.LastGood);

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(20, (await views.NextAsync()).Current.Meters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(30, (await views.NextAsync()).Current.Meters[0].UsedPercent);
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task Interval_UsesProviderMinIntervalWhenLarger()
    {
        _refreshInterval = TimeSpan.FromMinutes(1);
        var provider = Provider(minInterval: TimeSpan.FromMinutes(2));
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Interval_ReadsRefreshIntervalEveryCycle()
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _refreshInterval = TimeSpan.FromMinutes(10);
        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(5));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();
        Assert.Equal(3, provider.CallCount);
    }

    [Theory]
    [InlineData(0.0, 270)]
    [InlineData(1.0, 330)]
    public async Task Interval_AppliesJitter(double random, int expectedSeconds)
    {
        _random = random;
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromSeconds(expectedSeconds - 1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Interval_NegativeJitterNeverGoesBelowMinInterval()
    {
        _random = 0.0;
        _refreshInterval = TimeSpan.FromMinutes(2);
        var provider = Provider(minInterval: TimeSpan.FromMinutes(2));
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromSeconds(108)); // 0.9 x 2 min: too early
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(12));
        await views.NextAsync();
    }

    [Fact]
    public async Task Error_BacksOffExponentially_AndOkResetsToBaseInterval()
    {
        var provider = Provider()
            .ThenStatus(ProviderStatus.Error, message: "네트워크 오류")
            .ThenStatus(ProviderStatus.Error, message: "네트워크 오류")
            .ThenOk(40)
            .ThenOk(41);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var first = await views.NextAsync();
        Assert.Equal(ProviderStatus.Error, first.Current.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), first.Current.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await views.NextAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), second.Current.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var third = await views.NextAsync();
        Assert.Equal(ProviderStatus.Ok, third.Current.Status);
        Assert.Null(third.Current.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(41, (await views.NextAsync()).Current.Meters[0].UsedPercent);
    }

    [Fact]
    public async Task RateLimited_BlocksManualAndPathRefreshUntilBackoffElapses()
    {
        var provider = Provider()
            .ThenStatus(ProviderStatus.RateLimited, retryAfter: TimeSpan.Zero)
            .ThenOk(15)
            .ThenOk(16);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var limited = await views.NextAsync();
        Assert.Equal(ProviderStatus.RateLimited, limited.Current.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), limited.Current.RetryAfter);

        await service.RefreshNowAsync();
        Assert.Equal(1, provider.CallCount);

        service.NotifyPathChanged(CredentialsPath);
        _time.Advance(UsageService.DebounceDelay);
        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);

        _time.Advance(TimeSpan.FromMinutes(5) - UsageService.DebounceDelay);
        Assert.Equal(ProviderStatus.Ok, (await views.NextAsync()).Current.Status);

        await service.RefreshNowAsync();
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task RateLimited_RepeatedBlocksDouble()
    {
        var provider = Provider().ThenStatus(ProviderStatus.RateLimited);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        Assert.Equal(TimeSpan.FromMinutes(5), (await views.NextAsync()).Current.RetryAfter);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(10), (await views.NextAsync()).Current.RetryAfter);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(20), (await views.NextAsync()).Current.RetryAfter);
    }

    [Fact]
    public async Task RateLimited_NegativeJitterDoesNotShortenBlock()
    {
        _random = 0.0;
        var provider = Provider().ThenStatus(ProviderStatus.RateLimited).ThenOk(1);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        Assert.Equal(TimeSpan.FromMinutes(5), (await views.NextAsync()).Current.RetryAfter);
        _time.Advance(TimeSpan.FromSeconds(270));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        await views.NextAsync();
    }

    [Fact]
    public async Task Merge_LastGoodChangesOnlyOnOk_CurrentIsAlwaysLatest()
    {
        var provider = Provider()
            .ThenOk(10)
            .ThenStatus(ProviderStatus.WaitingForCliRefresh, message: "갱신 대기")
            .ThenStatus(ProviderStatus.Error)
            .ThenOk(25);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var ok = await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(5));
        var waiting = await views.NextAsync();
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, waiting.Current.Status);
        Assert.Same(ok.Current, waiting.LastGood);
        Assert.True(waiting.IsStale);
        Assert.Equal(10, waiting.DisplayMeters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        var error = await views.NextAsync();
        Assert.Equal(ProviderStatus.Error, error.Current.Status);
        Assert.Same(ok.Current, error.LastGood);

        _time.Advance(TimeSpan.FromMinutes(5));
        var recovered = await views.NextAsync();
        Assert.False(recovered.IsStale);
        Assert.Equal(25, recovered.LastGood!.Meters[0].UsedPercent);
        Assert.Same(recovered, service.GetView("claude"));
    }

    [Fact]
    public async Task ProviderException_BecomesErrorSnapshot_AndLoopKeepsRunning()
    {
        var provider = Provider()
            .ThenOk(10)
            .ThenThrow(new InvalidOperationException("boom"))
            .ThenOk(12);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        var failed = await views.NextAsync();
        Assert.Equal(ProviderStatus.Error, failed.Current.Status);
        Assert.Equal("알 수 없는 오류", failed.Current.StatusMessage);
        Assert.Equal(10, failed.LastGood!.Meters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ProviderStatus.Ok, (await views.NextAsync()).Current.Status);
    }

    [Fact]
    public async Task RefreshNow_FetchesAllProviders_AndRestartsTheirTimers()
    {
        var claude = Provider("claude");
        var codex = Provider("codex");
        var service = CreateService(claude, codex);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(2));
        await service.RefreshNowAsync();
        Assert.Equal(2, claude.CallCount);
        Assert.Equal(2, codex.CallCount);
        await views.NextAsync();
        await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(3)); // T0+5: the old schedule, now cancelled
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromMinutes(2)); // T0+7 = refresh + 5 min
        await views.NextAsync();
        await views.NextAsync();
        Assert.Equal(3, claude.CallCount);
        Assert.Equal(3, codex.CallCount);
    }

    [Fact]
    public async Task RefreshNow_WithProviderId_FetchesOnlyThatProvider()
    {
        var claude = Provider("claude");
        var codex = Provider("codex");
        var service = CreateService(claude, codex);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        await views.NextAsync();
        await service.RefreshNowAsync("codex");

        Assert.Equal(1, claude.CallCount);
        Assert.Equal(2, codex.CallCount);
    }

    [Fact]
    public async Task RefreshNow_BeforeStart_IsNoOp()
    {
        var provider = Provider();
        var service = CreateService(provider);

        await service.RefreshNowAsync();

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task NotifyPathChanged_MatchingPath_FetchesAfterDebounce()
    {
        var provider = new FakeUsageProvider("claude", _time, null, [@"C:\Users\test\.claude"]);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();

        service.NotifyPathChanged(@"C:\USERS\TEST\.claude\.credentials.json");
        _time.Advance(TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task NotifyPathChanged_BurstOfChanges_IsDebouncedIntoOneFetch()
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();

        service.NotifyPathChanged(CredentialsPath);
        _time.Advance(TimeSpan.FromSeconds(1));
        service.NotifyPathChanged(CredentialsPath);
        _time.Advance(TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await views.NextAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        await views.AssertNoneAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Theory]
    [InlineData(@"C:\Users\test\.claude.json")]
    [InlineData(@"C:\Users\test\.claude\.credentials.json.bak")]
    [InlineData(@"C:\Users\test\.codex\auth.json")]
    public async Task NotifyPathChanged_UnrelatedPath_DoesNothing(string path)
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        service.NotifyPathChanged(path);
        _time.Advance(TimeSpan.FromSeconds(2));

        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Cache_IsSavedAfterOk_AndNotOverwrittenByFailures()
    {
        var provider = Provider().ThenOk(33).ThenStatus(ProviderStatus.Error);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        var saved = new SnapshotCache(CachePath, new NullLog()).Load();
        Assert.Equal(33, saved["claude"].Meters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();
        var afterError = new SnapshotCache(CachePath, new NullLog()).Load();
        Assert.Equal(ProviderStatus.Ok, afterError["claude"].Status);
        Assert.Equal(33, afterError["claude"].Meters[0].UsedPercent);
    }

    [Fact]
    public async Task Start_RestoresCachedSnapshotBeforeFirstFetch()
    {
        var cachedAt = T0.AddHours(-1);
        new SnapshotCache(CachePath, new NullLog()).Save(new Dictionary<string, UsageSnapshot>
        {
            ["claude"] = FakeUsageProvider.OkSnapshot("claude", 77, cachedAt, "Max 5x"),
            ["retired-provider"] = FakeUsageProvider.OkSnapshot("retired-provider", 1, cachedAt),
        });
        var provider = Provider().ThenStatus(ProviderStatus.RateLimited);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var restored = await views.NextAsync();
        Assert.Equal(ProviderStatus.Ok, restored.Current.Status);
        Assert.Equal(cachedAt, restored.Current.FetchedAt);
        Assert.Equal(77, restored.LastGood!.Meters[0].UsedPercent);

        var limited = await views.NextAsync();
        Assert.Equal(ProviderStatus.RateLimited, limited.Current.Status);
        Assert.True(limited.IsStale);
        Assert.Equal(77, limited.DisplayMeters[0].UsedPercent);
        Assert.Equal("Max 5x", limited.DisplayPlan);
        Assert.Single(service.Views);
    }

    [Fact]
    public async Task ViewChangedHandlerException_DoesNotStopPolling()
    {
        var provider = Provider();
        var service = CreateService(provider);
        service.ViewChanged += (_, _) => throw new InvalidOperationException("handler bug");
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Dispose_StopsPolling_AndLaterCallsAreNoOps()
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        await service.DisposeAsync();

        _time.Advance(TimeSpan.FromMinutes(30));
        await service.RefreshNowAsync();
        service.NotifyPathChanged(CredentialsPath);
        _time.Advance(TimeSpan.FromSeconds(2));

        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);
        Assert.Throws<ObjectDisposedException>(() => service.Start());
    }
}
```

- [ ] **Step 15: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~UsageServiceTests"`
Expected: 빌드 실패. `error CS0246: The type or namespace name 'UsageService' could not be found` (`ViewRecorder.cs`와 `UsageServiceTests.cs` 양쪽에서)

- [ ] **Step 16: `UsageService` 구현**

공급자마다 `ProviderWorker` 하나가 상태(뷰, 연속 실패 수, 차단 시각, 대기 중 요청, 깨우기 신호, 디바운스 CTS)를 갖고, 모든 필드는 `Gate` 잠금으로 보호한다. 루프의 한 주기는 다음과 같다.
1. 대기 중 요청을 가져온다.
2. 조회한다.
3. 병합하고 다음 지연을 계산한다.
4. Ok면 캐시에 저장한다.
5. 타이머를 건다.
6. 이벤트를 올린다.
7. 요청을 완료 처리한다.
8. 타이머와 깨우기 신호 중 먼저 오는 것을 기다린다.

`src/AITokenUsage.Core/Polling/UsageService.cs`:

```csharp
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers;

namespace AITokenUsage.Core.Polling;

/// <summary>
/// Runs one background polling loop per provider and merges results into <see cref="ProviderView"/>s.
/// <list type="bullet">
/// <item>Interval: max(refreshInterval(), MinInterval) through <see cref="BackoffPolicy"/>, ±10% jitter, never below MinInterval.</item>
/// <item>LastGood changes only on Ok; Current is always the latest result.</item>
/// <item>For RateLimited/Error results, Current.RetryAfter is replaced by the actual delay until the next automatic fetch.</item>
/// <item>A RateLimited result blocks RefreshNowAsync/NotifyPathChanged until the backoff delay has passed.</item>
/// </list>
/// ViewChanged is raised on a background thread after every fetch and once per provider restored from the cache.
/// </summary>
public sealed class UsageService : IAsyncDisposable
{
    public const string LoadingMessage = "불러오는 중…";
    public const string UnknownErrorMessage = "알 수 없는 오류";
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<ProviderWorker> _workers;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly SnapshotCache _cache;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;
    private readonly Func<double> _random;
    private readonly BackoffPolicy _backoff = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _cacheGate = new();
    private Task _runTask = Task.CompletedTask;
    private int _started;
    private int _disposed;

    /// <param name="random">Returns a value in [0, 1) used for jitter. Defaults to Random.Shared.NextDouble; tests inject a constant.</param>
    public UsageService(IReadOnlyList<IUsageProvider> providers, Func<TimeSpan> refreshInterval,
        SnapshotCache cache, TimeProvider time, IAppLog log, Func<double>? random = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(refreshInterval);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        DateTimeOffset now = time.GetUtcNow();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var workers = new List<ProviderWorker>(providers.Count);
        foreach (IUsageProvider provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider, nameof(providers));
            if (!ids.Add(provider.Id))
            {
                throw new ArgumentException($"Duplicate provider id '{provider.Id}'.", nameof(providers));
            }

            var loading = UsageSnapshot.WithStatus(provider.Id, ProviderStatus.Ok, now, LoadingMessage);
            workers.Add(new ProviderWorker(provider, new ProviderView(provider.Id, provider.DisplayName, loading, null)));
        }

        _workers = workers;
        _refreshInterval = refreshInterval;
        _cache = cache;
        _time = time;
        _log = log;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>Raised on a background thread. Subscribers marshal to the UI thread themselves.</summary>
    public event EventHandler<ProviderView>? ViewChanged;

    /// <summary>Current views in provider registration order.</summary>
    public IReadOnlyList<ProviderView> Views => _workers.Select(w => w.View).ToArray();

    public ProviderView? GetView(string providerId) => Find(providerId)?.View;

    /// <summary>Loads the cache, fetches every provider once immediately, then keeps polling. Returns at once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        CancellationToken ct = _shutdown.Token;
        _runTask = Task.Run(() => RunAllAsync(ct));
    }

    /// <summary>
    /// Fetches the given provider (or all when null) now and completes once the result is published.
    /// Providers inside a rate-limit block are skipped. Before Start or after disposal this is a no-op.
    /// </summary>
    public Task RefreshNowAsync(string? providerId = null)
    {
        if (!IsRunning)
        {
            return Task.CompletedTask;
        }

        var requests = new List<Task>();
        foreach (ProviderWorker worker in _workers)
        {
            if (providerId is null || string.Equals(worker.Provider.Id, providerId, StringComparison.Ordinal))
            {
                requests.Add(RequestFetch(worker));
            }
        }

        return Task.WhenAll(requests);
    }

    /// <summary>
    /// Called by the app's FileSystemWatcher. When the path equals or lies under one of a provider's WatchPaths,
    /// that provider is fetched after a 1.5 s debounce (unless rate-limit blocked at that moment).
    /// </summary>
    public void NotifyPathChanged(string fullPath)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        foreach (ProviderWorker worker in _workers)
        {
            if (worker.Provider.WatchPaths.Any(watch => WatchPathMatcher.Matches(fullPath, watch)))
            {
                ScheduleDebouncedFetch(worker);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            await _runTask.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (TimeoutException)
        {
            _log.Warn("Usage polling did not stop within the shutdown timeout.");
        }
        catch (Exception ex)
        {
            _log.Warn("Usage polling stopped with an error.", ex);
        }

        foreach (ProviderWorker worker in _workers)
        {
            worker.Shutdown();
        }
    }

    private bool IsRunning => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _disposed) == 0;

    private ProviderWorker? Find(string providerId) =>
        _workers.FirstOrDefault(w => string.Equals(w.Provider.Id, providerId, StringComparison.Ordinal));

    private async Task RunAllAsync(CancellationToken ct)
    {
        RestoreFromCache();

        var loops = new Task[_workers.Count];
        for (int i = 0; i < _workers.Count; i++)
        {
            ProviderWorker worker = _workers[i];
            loops[i] = Task.Run(() => RunLoopAsync(worker, ct), CancellationToken.None);
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private void RestoreFromCache()
    {
        IReadOnlyDictionary<string, UsageSnapshot> cached = _cache.Load();
        foreach (ProviderWorker worker in _workers)
        {
            if (!cached.TryGetValue(worker.Provider.Id, out UsageSnapshot? snapshot))
            {
                continue;
            }

            var view = new ProviderView(worker.Provider.Id, worker.Provider.DisplayName, snapshot, snapshot);
            worker.View = view;
            Publish(view);
        }
    }

    private async Task RunLoopAsync(ProviderWorker worker, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TaskCompletionSource? served;
            Task wake;
            lock (worker.Gate)
            {
                served = worker.Pending;
                worker.Pending = null;
                worker.Wake = NewSignal();
                wake = worker.Wake.Task;
            }

            UsageSnapshot result = await FetchSafelyAsync(worker.Provider, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                served?.TrySetResult();
                return;
            }

            (ProviderView view, TimeSpan delay) = Apply(worker, result);
            if (result.Status == ProviderStatus.Ok)
            {
                SaveCache();
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Arm the timer before publishing: whoever observes ViewChanged sees a fully scheduled loop.
            Task delayTask = Task.Delay(delay, _time, delayCts.Token);
            Publish(view);
            served?.TrySetResult();

            while (true)
            {
                Task winner = await Task.WhenAny(delayTask, wake).ConfigureAwait(false);
                if (winner == delayTask || ct.IsCancellationRequested || !IsBlocked(worker))
                {
                    break;
                }

                // A request queued while the last fetch was running, but that fetch started a rate-limit block: drop it.
                lock (worker.Gate)
                {
                    worker.Pending?.TrySetResult();
                    worker.Pending = null;
                    worker.Wake = NewSignal();
                    wake = worker.Wake.Task;
                }
            }

            delayCts.Cancel();
        }
    }

    private async Task<UsageSnapshot> FetchSafelyAsync(IUsageProvider provider, CancellationToken ct)
    {
        try
        {
            UsageSnapshot? snapshot = await provider.FetchAsync(ct).ConfigureAwait(false);
            return snapshot ?? UnknownError(provider.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UnknownError(provider.Id);
        }
        catch (Exception ex)
        {
            _log.Warn($"Fetch failed for provider '{provider.Id}'.", ex);
            return UnknownError(provider.Id);
        }
    }

    private UsageSnapshot UnknownError(string providerId) =>
        UsageSnapshot.WithStatus(providerId, ProviderStatus.Error, _time.GetUtcNow(), UnknownErrorMessage);

    private (ProviderView View, TimeSpan Delay) Apply(ProviderWorker worker, UsageSnapshot result)
    {
        IUsageProvider provider = worker.Provider;
        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan baseInterval = Max(ConfiguredInterval(), provider.MinInterval);
        bool failed = result.Status is ProviderStatus.RateLimited or ProviderStatus.Error;

        lock (worker.Gate)
        {
            worker.ConsecutiveFailures = failed ? worker.ConsecutiveFailures + 1 : 0;
            TimeSpan policyDelay = _backoff.NextDelay(result, baseInterval, worker.ConsecutiveFailures);
            TimeSpan delay = BackoffPolicy.Jitter(policyDelay, _random());

            if (result.Status == ProviderStatus.RateLimited)
            {
                // Never retry before the block ends: only the upward half of the jitter applies.
                delay = Max(delay, policyDelay);
                worker.BlockedUntil = now + policyDelay;
            }
            else
            {
                delay = Max(delay, provider.MinInterval);
                worker.BlockedUntil = null;
            }

            delay = Max(delay, MinimumDelay);

            UsageSnapshot current = failed ? result with { RetryAfter = delay } : result;
            UsageSnapshot? lastGood = result.Status == ProviderStatus.Ok ? result : worker.View.LastGood;
            var view = new ProviderView(provider.Id, provider.DisplayName, current, lastGood);
            worker.View = view;
            return (view, delay);
        }
    }

    private TimeSpan ConfiguredInterval()
    {
        try
        {
            TimeSpan configured = _refreshInterval();
            return configured > TimeSpan.Zero ? configured : DefaultRefreshInterval;
        }
        catch (Exception ex)
        {
            _log.Warn("Refresh interval callback failed; using the default.", ex);
            return DefaultRefreshInterval;
        }
    }

    private void SaveCache()
    {
        lock (_cacheGate)
        {
            var lastGood = new Dictionary<string, UsageSnapshot>(StringComparer.Ordinal);
            foreach (ProviderWorker worker in _workers)
            {
                if (worker.View.LastGood is { } snapshot)
                {
                    lastGood[worker.Provider.Id] = snapshot;
                }
            }

            _cache.Save(lastGood);
        }
    }

    private void Publish(ProviderView view)
    {
        EventHandler<ProviderView>? handlers = ViewChanged;
        if (handlers is null)
        {
            return;
        }

        // Invoke subscribers one by one so a throwing handler cannot starve the others or kill the loop.
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ProviderView>)handler)(this, view);
            }
            catch (Exception ex)
            {
                _log.Warn("A ViewChanged handler threw.", ex);
            }
        }
    }

    private Task RequestFetch(ProviderWorker worker)
    {
        lock (worker.Gate)
        {
            if (worker.IsShutDown || IsBlocked(worker))
            {
                return Task.CompletedTask;
            }

            worker.Pending ??= NewSignal();
            worker.Wake.TrySetResult();
            return worker.Pending.Task;
        }
    }

    private bool IsBlocked(ProviderWorker worker)
    {
        lock (worker.Gate)
        {
            return worker.BlockedUntil is { } until && _time.GetUtcNow() < until;
        }
    }

    private void ScheduleDebouncedFetch(ProviderWorker worker)
    {
        CancellationToken token;
        lock (worker.Gate)
        {
            if (worker.IsShutDown)
            {
                return;
            }

            worker.DebounceCts?.Cancel();
            worker.DebounceCts?.Dispose();
            worker.DebounceCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            token = worker.DebounceCts.Token;
        }

        _ = DebounceThenFetchAsync(worker, token);
    }

    private async Task DebounceThenFetchAsync(ProviderWorker worker, CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceDelay, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer change, or shutting down.
        }

        _ = RequestFetch(worker);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private sealed class ProviderWorker(IUsageProvider provider, ProviderView initialView)
    {
        private ProviderView _view = initialView;

        public object Gate { get; } = new();
        public IUsageProvider Provider { get; } = provider;

        public ProviderView View
        {
            get { lock (Gate) { return _view; } }
            set { lock (Gate) { _view = value; } }
        }

        // The fields below are guarded by Gate.
        public int ConsecutiveFailures;
        public DateTimeOffset? BlockedUntil;
        public TaskCompletionSource? Pending;
        public TaskCompletionSource Wake = NewSignal();
        public CancellationTokenSource? DebounceCts;
        public bool IsShutDown;

        public void Shutdown()
        {
            lock (Gate)
            {
                IsShutDown = true;
                Pending?.TrySetResult();
                Pending = null;
                DebounceCts?.Cancel();
                DebounceCts?.Dispose();
                DebounceCts = null;
            }
        }
    }
}
```

- [ ] **Step 17: Polling 테스트 전체 실행 — 통과 확인 (3회 반복으로 불안정성 점검)**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~AITokenUsage.Core.Tests.Polling"`
Expected: `Passed!  - Failed: 0, Passed: 80` (BackoffPolicy 28 + SnapshotCache 13 + WatchPathMatcher 13 + UsageService 26). 같은 명령을 두 번 더 실행해도 매번 전부 통과해야 한다. 한 번이라도 `TimeoutException: ViewChanged was not raised within 5 seconds`가 나오면 테스트가 타이밍 규칙(한 번의 `Advance`로 예정 시각 하나만 넘기기)을 어겼는지부터 확인한다.

- [ ] **Step 18: 솔루션 전체 빌드·테스트**

Run: `dotnet build AITokenUsage.sln` → Expected: `0 Warning(s)`, `0 Error(s)`
Run: `dotnet test AITokenUsage.sln` → Expected: `Failed: 0`

- [ ] **Step 19: 커밋**

```bash
git add src/AITokenUsage.Core/Polling/BackoffPolicy.cs src/AITokenUsage.Core/Polling/SnapshotCache.cs src/AITokenUsage.Core/Polling/WatchPathMatcher.cs src/AITokenUsage.Core/Polling/UsageService.cs tests/AITokenUsage.Core.Tests/Fakes/FakeUsageProvider.cs tests/AITokenUsage.Core.Tests/Fakes/ViewRecorder.cs tests/AITokenUsage.Core.Tests/Polling/BackoffPolicyTests.cs tests/AITokenUsage.Core.Tests/Polling/SnapshotCacheTests.cs tests/AITokenUsage.Core.Tests/Polling/WatchPathMatcherTests.cs tests/AITokenUsage.Core.Tests/Polling/UsageServiceTests.cs
git commit -m "feat(core): 사용량 폴링 엔진(UsageService·BackoffPolicy·SnapshotCache) 추가" -m "공급자별 백그라운드 루프, ±10% 지터와 MinInterval 하한, 429 차단·지수 백오프, 경로 변경 1.5초 디바운스, 마지막 정상 값 캐시를 FakeTimeProvider 테스트로 검증한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: `RotationController`

**Files:**
- Create: `src/AITokenUsage.Core/Rotation/RotationChangedEventArgs.cs`
- Create: `src/AITokenUsage.Core/Rotation/RotationController.cs`
- Test: `tests/AITokenUsage.Core.Tests/Rotation/RotationControllerTests.cs`

**Interfaces:**
- Consumes:
  - `record ProviderView(string ProviderId, string DisplayName, UsageSnapshot Current, UsageSnapshot? LastGood)` (Task 2)
  - `enum ProviderStatus { Ok, NotInstalled, SignedOut, WaitingForCliRefresh, Unsupported, RateLimited, Error }` (Task 2)
  - `static UsageSnapshot UsageSnapshot.WithStatus(string providerId, ProviderStatus status, DateTimeOffset now, string? message = null, TimeSpan? retryAfter = null)` (Task 2, 테스트에서 사용)
- Produces:
  - `public sealed class RotationChangedEventArgs(string? currentId, int direction) : EventArgs` — `string? CurrentId`, `int Direction`(+1 다음, −1 이전, 0 목록 변경으로 인한 교체)
  - `public sealed class RotationController`
    - `public RotationController(TimeProvider time)`
    - `public void Configure(bool enabled, TimeSpan interval, bool skipUnavailable)`
    - `public void UpdateProviders(IReadOnlyList<ProviderView> enabledInOrder)`
    - `public IReadOnlyList<string> EligibleIds { get; }`
    - `public string? CurrentId { get; }`
    - `public int CurrentIndex { get; }` — `EligibleIds` 안의 위치, 없으면 −1
    - `public bool IsUserPaused { get; }`
    - `public event EventHandler<RotationChangedEventArgs>? CurrentChanged`
    - `public void OnTick()`, `public void Next()`, `public void Previous()`
    - `public void SetHoverPaused(bool paused)`, `public void SetUserPaused(bool paused)`
    - `public static readonly TimeSpan MinInterval`(1초, `Configure`의 하한), `DefaultInterval`(5초, `Configure` 전 기본값)
  - **다음 Task가 지켜야 할 의미 규칙**
    - 스레드 안전하지 않다. 모든 멤버를 UI 스레드에서 호출한다(앱의 250ms `DispatcherTimer`가 `OnTick()`). `AppHost`는 UI 스레드로 마샬링한 `ViewChanged`에서 `UpdateProviders(EnabledViewsInOrder())`를 다시 부른다.
    - `SetHoverPaused`는 설정 `PauseOnHover`가 켜져 있을 때만 앱이 호출한다. 컨트롤러는 그 설정을 모른다.
    - 위치 점은 `EligibleIds.Count >= 2`일 때만 보인다(§2.2). `CurrentId == null`이면 위젯은 "AI 사용량 · 우클릭하여 설정"을 보여 준다.

**동작 규칙**

| # | 규칙 |
|---|---|
| 1 | 적격: `skipUnavailable`이면 `Current.Status ∈ {NotInstalled, SignedOut, Unsupported} && LastGood == null`인 공급자를 뺀다. 결과가 비면 `enabledInOrder`의 첫 항목 하나만 남긴다. `enabledInOrder`가 비면 적격 목록도 비고 `CurrentId = null`이다. |
| 2 | 목록 변경(`UpdateProviders`, `Configure`): 현재 id가 여전히 적격이면 그대로 둔다. 이벤트도 없다. 아니면 **이전 순서**에서 현재 id 다음에 오는 첫 적격 id로 바꾼다(끝에서 처음으로 순환). 그런 id가 없으면 적격 목록의 첫 항목이다. id가 실제로 바뀔 때만 `Direction 0` 이벤트를 올리고 타이머를 리셋한다. |
| 3 | `OnTick()`: 자동 전환이 꺼짐 · 호버 멈춤 · 사용자 일시정지 · 적격 2개 미만 중 하나라도 해당하면 넘기지 않는다. 이때 주기 시작 시각을 "지금"으로 붙잡아 둔다(풀리면 처음부터 센다). 그 밖에는 마지막 리셋 후 `interval`이 지났을 때 `+1`로 넘긴다. |
| 4 | `Next()`/`Previous()`: 수동 전환이다. 자동 전환이 꺼져 있거나 일시정지여도 동작한다. 언제나 타이머를 리셋한다. 적격이 2개 미만이면 아무 것도 바꾸지 않는다. 끝과 처음을 순환한다. |
| 5 | `SetHoverPaused(false)`, `SetUserPaused(false)`, `Configure(enabled, interval, skipUnavailable)`는 타이머를 리셋한다. 사용자 일시정지는 저장하지 않는다(앱 재시작 시 풀림). |
| 6 | `Configure`의 `interval`이 1초보다 작으면 1초로 올린다(설정 범위 2–60초는 `AppSettings.Normalize`가 보장). |

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Rotation/RotationControllerTests.cs`:

```csharp
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Rotation;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Rotation;

public class RotationControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly RotationController _rotation;
    private readonly List<(string? Id, int Direction)> _events = [];

    public RotationControllerTests()
    {
        _rotation = new RotationController(_time);
        _rotation.CurrentChanged += (_, e) => _events.Add((e.CurrentId, e.Direction));
        _rotation.Configure(enabled: true, Interval, skipUnavailable: true);
    }

    private static ProviderView View(string id, ProviderStatus status = ProviderStatus.Ok, bool hasLastGood = false)
    {
        var current = UsageSnapshot.WithStatus(id, status, T0);
        var lastGood = hasLastGood ? UsageSnapshot.WithStatus(id, ProviderStatus.Ok, T0.AddHours(-1)) : null;
        return new ProviderView(id, id, current, lastGood);
    }

    private static ProviderView[] Views(params string[] ids) => ids.Select(id => View(id)).ToArray();

    private void Tick(TimeSpan advance)
    {
        _time.Advance(advance);
        _rotation.OnTick();
    }

    [Fact]
    public void BeforeAnyProviders_CurrentIsNull()
    {
        Assert.Null(_rotation.CurrentId);
        Assert.Equal(-1, _rotation.CurrentIndex);
        Assert.Empty(_rotation.EligibleIds);
        Assert.Empty(_events);
    }

    [Fact]
    public void FirstUpdate_SelectsFirstEligible_WithDirectionZero()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));

        Assert.Equal("claude", _rotation.CurrentId);
        Assert.Equal(0, _rotation.CurrentIndex);
        Assert.Equal(new[] { "claude", "codex", "gemini" }, _rotation.EligibleIds);
        Assert.Equal(("claude", 0), Assert.Single(_events));
    }

    [Fact]
    public void OnTick_AdvancesOnlyWhenIntervalElapsed_AndWrapsAround()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _events.Clear();

        Tick(TimeSpan.FromSeconds(4.75));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(0.25));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(Interval);
        Assert.Equal("gemini", _rotation.CurrentId);
        Tick(Interval);
        Assert.Equal("claude", _rotation.CurrentId);

        Assert.Equal(new (string?, int)[] { ("codex", 1), ("gemini", 1), ("claude", 1) }, _events);
    }

    [Fact]
    public void HoverPause_StopsRotation_AndReleaseRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex"));
        Tick(TimeSpan.FromSeconds(3));

        _rotation.SetHoverPaused(true);
        Tick(TimeSpan.FromSeconds(30));
        Assert.Equal("claude", _rotation.CurrentId);

        _rotation.SetHoverPaused(false);
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("codex", _rotation.CurrentId);
    }

    [Fact]
    public void UserPause_StopsRotation_ManualStillWorks_AndResumeRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.SetUserPaused(true);
        Assert.True(_rotation.IsUserPaused);

        Tick(TimeSpan.FromSeconds(60));
        Assert.Equal("claude", _rotation.CurrentId);

        _rotation.Next();
        Assert.Equal("codex", _rotation.CurrentId);

        Tick(TimeSpan.FromSeconds(2));
        _rotation.SetUserPaused(false);
        Assert.False(_rotation.IsUserPaused);
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("gemini", _rotation.CurrentId);
    }

    [Fact]
    public void NextAndPrevious_WrapAround_WithDirections()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _events.Clear();

        _rotation.Previous();
        Assert.Equal("gemini", _rotation.CurrentId);
        Assert.Equal(2, _rotation.CurrentIndex);
        _rotation.Next();
        Assert.Equal("claude", _rotation.CurrentId);
        _rotation.Next();
        Assert.Equal("codex", _rotation.CurrentId);

        Assert.Equal(new (string?, int)[] { ("gemini", -1), ("claude", 1), ("codex", 1) }, _events);
    }

    [Fact]
    public void ManualNext_RestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        Tick(TimeSpan.FromSeconds(4));

        _rotation.Next();
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("gemini", _rotation.CurrentId);
    }

    [Fact]
    public void SkipUnavailable_ExcludesDisconnectedProvidersWithoutCachedData()
    {
        _rotation.UpdateProviders(
        [
            View("a", ProviderStatus.Ok),
            View("b", ProviderStatus.SignedOut),
            View("c", ProviderStatus.NotInstalled, hasLastGood: true),
            View("d", ProviderStatus.Unsupported),
            View("e", ProviderStatus.Error),
            View("f", ProviderStatus.WaitingForCliRefresh),
            View("g", ProviderStatus.RateLimited),
            View("h", ProviderStatus.NotInstalled),
        ]);

        Assert.Equal(new[] { "a", "c", "e", "f", "g" }, _rotation.EligibleIds);
    }

    [Fact]
    public void SkipUnavailableOff_IncludesEveryEnabledProvider()
    {
        _rotation.Configure(enabled: true, Interval, skipUnavailable: false);

        _rotation.UpdateProviders([View("a"), View("b", ProviderStatus.SignedOut), View("c", ProviderStatus.NotInstalled)]);

        Assert.Equal(new[] { "a", "b", "c" }, _rotation.EligibleIds);
    }

    [Fact]
    public void Configure_ReevaluatesEligibilityWithLastProviderList()
    {
        _rotation.UpdateProviders([View("a"), View("b", ProviderStatus.SignedOut)]);
        Assert.Equal(new[] { "a" }, _rotation.EligibleIds);

        _rotation.Configure(enabled: true, Interval, skipUnavailable: false);

        Assert.Equal(new[] { "a", "b" }, _rotation.EligibleIds);
        Assert.Equal("a", _rotation.CurrentId);
    }

    [Fact]
    public void NothingEligible_FallsBackToFirstEnabledProvider()
    {
        _rotation.UpdateProviders([View("codex", ProviderStatus.NotInstalled), View("copilot", ProviderStatus.SignedOut)]);

        Assert.Equal(new[] { "codex" }, _rotation.EligibleIds);
        Assert.Equal("codex", _rotation.CurrentId);
    }

    [Fact]
    public void EmptyList_ClearsCurrent_WithDirectionZeroEvent()
    {
        _rotation.UpdateProviders(Views("claude"));
        _events.Clear();

        _rotation.UpdateProviders([]);

        Assert.Null(_rotation.CurrentId);
        Assert.Equal(-1, _rotation.CurrentIndex);
        Assert.Empty(_rotation.EligibleIds);
        Assert.Equal(((string?)null, 0), Assert.Single(_events));
    }

    [Fact]
    public void ListChange_KeepsCurrentWhenStillEligible_WithoutEvent()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.Next();
        _events.Clear();

        _rotation.UpdateProviders(Views("gemini", "claude", "codex"));

        Assert.Equal("codex", _rotation.CurrentId);
        Assert.Equal(2, _rotation.CurrentIndex);
        Assert.Empty(_events);
    }

    [Fact]
    public void ListChange_CurrentBecomesIneligible_MovesToNextInPreviousOrder()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.Next();
        _events.Clear();

        _rotation.UpdateProviders([View("claude"), View("codex", ProviderStatus.SignedOut), View("gemini")]);

        Assert.Equal("gemini", _rotation.CurrentId);
        Assert.Equal(("gemini", 0), Assert.Single(_events));
    }

    [Fact]
    public void ListChange_CurrentDisabled_WrapsToFirstFollowingEligible()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        _rotation.Previous();
        _events.Clear();

        _rotation.UpdateProviders(Views("claude", "codex"));

        Assert.Equal("claude", _rotation.CurrentId);
        Assert.Equal(("claude", 0), Assert.Single(_events));
    }

    [Fact]
    public void ListChange_ReplacementRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex", "gemini"));
        Tick(TimeSpan.FromSeconds(4));

        _rotation.UpdateProviders(Views("codex", "gemini"));
        Tick(TimeSpan.FromSeconds(4));
        Assert.Equal("codex", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("gemini", _rotation.CurrentId);
    }

    [Fact]
    public void SingleProvider_NeverRotates_AndManualMovesDoNothing()
    {
        _rotation.UpdateProviders(Views("claude"));
        _events.Clear();

        Tick(TimeSpan.FromSeconds(60));
        _rotation.Next();
        _rotation.Previous();

        Assert.Equal("claude", _rotation.CurrentId);
        Assert.Empty(_events);
    }

    [Fact]
    public void GrowingFromOneToTwoProviders_DoesNotSwitchImmediately()
    {
        _rotation.UpdateProviders(Views("claude"));
        Tick(TimeSpan.FromSeconds(60));

        _rotation.UpdateProviders(Views("claude", "codex"));
        Tick(TimeSpan.FromSeconds(0.25));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(4.75));
        Assert.Equal("codex", _rotation.CurrentId);
    }

    [Fact]
    public void DisabledRotation_NeverAutoAdvances_ButManualNextWorks()
    {
        _rotation.Configure(enabled: false, Interval, skipUnavailable: true);
        _rotation.UpdateProviders(Views("claude", "codex"));
        _events.Clear();

        Tick(TimeSpan.FromSeconds(120));
        Assert.Equal("claude", _rotation.CurrentId);

        _rotation.Next();
        Assert.Equal("codex", _rotation.CurrentId);
        Assert.Equal(("codex", 1), Assert.Single(_events));
    }

    [Fact]
    public void Configure_ChangesIntervalAndRestartsTimer()
    {
        _rotation.UpdateProviders(Views("claude", "codex"));
        Tick(TimeSpan.FromSeconds(4));

        _rotation.Configure(enabled: true, TimeSpan.FromSeconds(10), skipUnavailable: true);
        Tick(TimeSpan.FromSeconds(9));
        Assert.Equal("claude", _rotation.CurrentId);
        Tick(TimeSpan.FromSeconds(1));
        Assert.Equal("codex", _rotation.CurrentId);
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~RotationControllerTests"`
Expected: 빌드 실패. `error CS0234: The type or namespace name 'Rotation' does not exist in the namespace 'AITokenUsage.Core'`

- [ ] **Step 3: `RotationChangedEventArgs` 구현**

`src/AITokenUsage.Core/Rotation/RotationChangedEventArgs.cs`:

```csharp
namespace AITokenUsage.Core.Rotation;

public sealed class RotationChangedEventArgs(string? currentId, int direction) : EventArgs
{
    public string? CurrentId { get; } = currentId;

    /// <summary>+1 next, -1 previous, 0 replaced because the provider list changed.</summary>
    public int Direction { get; } = direction;
}
```

- [ ] **Step 4: `RotationController` 구현**

`src/AITokenUsage.Core/Rotation/RotationController.cs`:

```csharp
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Rotation;

/// <summary>
/// Decides which provider the widget shows. Not thread-safe: call every member from the UI thread
/// (the app's 250 ms DispatcherTimer calls <see cref="OnTick"/>).
/// </summary>
public sealed class RotationController
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private IReadOnlyList<ProviderView> _views = [];
    private IReadOnlyList<string> _order = [];
    private string[] _eligible = [];
    private bool _enabled = true;
    private TimeSpan _interval = DefaultInterval;
    private bool _skipUnavailable = true;
    private bool _hoverPaused;
    private DateTimeOffset _cycleStart;

    public RotationController(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _cycleStart = time.GetUtcNow();
    }

    public event EventHandler<RotationChangedEventArgs>? CurrentChanged;

    public IReadOnlyList<string> EligibleIds => _eligible;

    public string? CurrentId { get; private set; }

    /// <summary>Index of <see cref="CurrentId"/> in <see cref="EligibleIds"/>, or -1 when there is none.</summary>
    public int CurrentIndex => CurrentId is null ? -1 : Array.IndexOf(_eligible, CurrentId);

    public bool IsUserPaused { get; private set; }

    /// <summary>Applies rotation settings, re-evaluates eligibility with the last provider list, and restarts the timer.</summary>
    public void Configure(bool enabled, TimeSpan interval, bool skipUnavailable)
    {
        _enabled = enabled;
        _interval = interval < MinInterval ? MinInterval : interval;
        _skipUnavailable = skipUnavailable;
        Recompute(_views);
        ResetTimer();
    }

    /// <summary>
    /// Replaces the provider list (enabled providers in settings order). Keeps the current provider when it is still
    /// eligible; otherwise moves to the next eligible provider after it in the previous order and raises
    /// CurrentChanged with Direction 0.
    /// </summary>
    public void UpdateProviders(IReadOnlyList<ProviderView> enabledInOrder)
    {
        ArgumentNullException.ThrowIfNull(enabledInOrder);
        Recompute(enabledInOrder.ToArray());
    }

    /// <summary>Advances to the next provider once the interval has elapsed and nothing pauses rotation.</summary>
    public void OnTick()
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (!_enabled || _hoverPaused || IsUserPaused || _eligible.Length < 2)
        {
            // While rotation cannot run, keep the cycle anchored to "now" so it restarts from zero when it can.
            _cycleStart = now;
            return;
        }

        if (now - _cycleStart >= _interval)
        {
            Move(+1);
        }
    }

    /// <summary>Manual "next" (click, wheel down). Works even when automatic rotation is disabled or paused.</summary>
    public void Next() => Move(+1);

    /// <summary>Manual "previous" (wheel up).</summary>
    public void Previous() => Move(-1);

    /// <summary>The app calls this only when the PauseOnHover setting is on. Releasing restarts the timer.</summary>
    public void SetHoverPaused(bool paused)
    {
        _hoverPaused = paused;
        if (!paused)
        {
            ResetTimer();
        }
    }

    /// <summary>"자동 전환 일시정지" menu item. Not persisted. Releasing restarts the timer.</summary>
    public void SetUserPaused(bool paused)
    {
        IsUserPaused = paused;
        if (!paused)
        {
            ResetTimer();
        }
    }

    private void Move(int direction)
    {
        ResetTimer();
        if (_eligible.Length < 2)
        {
            return;
        }

        int index = CurrentIndex;
        int next = index < 0
            ? 0
            : ((index + direction) % _eligible.Length + _eligible.Length) % _eligible.Length;
        SetCurrent(_eligible[next], direction);
    }

    private void Recompute(IReadOnlyList<ProviderView> enabledInOrder)
    {
        IReadOnlyList<string> previousOrder = _order;
        string? previousId = CurrentId;

        _views = enabledInOrder;
        _order = enabledInOrder.Select(v => v.ProviderId).ToArray();
        _eligible = ComputeEligible(enabledInOrder);

        string? nextId = previousId is not null && _eligible.Contains(previousId)
            ? previousId
            : PickReplacement(previousId, previousOrder);

        if (!string.Equals(nextId, previousId, StringComparison.Ordinal))
        {
            ResetTimer();
            SetCurrent(nextId, 0);
        }
    }

    private string[] ComputeEligible(IReadOnlyList<ProviderView> enabledInOrder)
    {
        if (enabledInOrder.Count == 0)
        {
            return [];
        }

        string[] eligible = enabledInOrder
            .Where(v => !_skipUnavailable || !IsUnavailable(v))
            .Select(v => v.ProviderId)
            .ToArray();

        // Nothing eligible: show the first enabled provider so its status message is visible.
        return eligible.Length > 0 ? eligible : [enabledInOrder[0].ProviderId];
    }

    private static bool IsUnavailable(ProviderView view) =>
        view.LastGood is null
        && view.Current.Status is ProviderStatus.NotInstalled or ProviderStatus.SignedOut or ProviderStatus.Unsupported;

    /// <summary>The first eligible id after <paramref name="previousId"/> in the previous order (wrapping), else the first eligible.</summary>
    private string? PickReplacement(string? previousId, IReadOnlyList<string> previousOrder)
    {
        if (_eligible.Length == 0)
        {
            return null;
        }

        int start = previousId is null ? -1 : IndexOf(previousOrder, previousId);
        if (start >= 0)
        {
            for (int step = 1; step < previousOrder.Count; step++)
            {
                string candidate = previousOrder[(start + step) % previousOrder.Count];
                if (_eligible.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        return _eligible[0];
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void SetCurrent(string? id, int direction)
    {
        if (string.Equals(id, CurrentId, StringComparison.Ordinal))
        {
            return;
        }

        CurrentId = id;
        CurrentChanged?.Invoke(this, new RotationChangedEventArgs(id, direction));
    }

    private void ResetTimer() => _cycleStart = _time.GetUtcNow();
}
```

- [ ] **Step 5: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~RotationControllerTests"`
Expected: `Passed!  - Failed: 0, Passed: 20`

- [ ] **Step 6: 솔루션 전체 빌드·테스트**

Run: `dotnet build AITokenUsage.sln` → Expected: `0 Warning(s)`, `0 Error(s)`
Run: `dotnet test AITokenUsage.sln` → Expected: `Failed: 0`

- [ ] **Step 7: 커밋**

```bash
git add src/AITokenUsage.Core/Rotation/RotationChangedEventArgs.cs src/AITokenUsage.Core/Rotation/RotationController.cs tests/AITokenUsage.Core.Tests/Rotation/RotationControllerTests.cs
git commit -m "feat(core): AI 자동 전환 RotationController 추가" -m "간격 경과 전환, 호버·사용자 일시정지와 타이머 리셋, 수동 다음/이전 순환, 연결 안 된 AI 건너뛰기, 목록 변경 시 현재 AI 유지 규칙을 테스트로 고정한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: `PixelRect`, `TaskbarGeometry`, `TaskbarLayoutCalculator`

**Files:**
- Create: `src/AITokenUsage.Core/Taskbar/PixelRect.cs`
- Create: `src/AITokenUsage.Core/Taskbar/TaskbarGeometry.cs`
- Create: `src/AITokenUsage.Core/Taskbar/TaskbarLayoutCalculator.cs`
- Test: `tests/AITokenUsage.Core.Tests/Taskbar/PixelRectTests.cs`
- Test: `tests/AITokenUsage.Core.Tests/Taskbar/TaskbarLayoutCalculatorTests.cs`

**Interfaces:**
- Consumes: `enum WidgetPlacement { LeftEdge, BeforeStart, BeforeTray }` (Task 4, `AITokenUsage.Core.Settings`)
- Produces:
  - `public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)` — `Width`, `Height`, `IsEmpty`, `PixelRect Offset(int dx, int dy)`, `bool IsInside(PixelRect outer)`
  - `public sealed record TaskbarGeometry(PixelRect Taskbar, uint Dpi, bool IsHorizontal, bool IsCentered, PixelRect? Start, PixelRect? Widgets, PixelRect? AppButtons, PixelRect? Tray)` — 전부 화면 좌표(물리 px). 못 찾았거나 유효하지 않은 요소는 `null`.
  - `public static class TaskbarLayoutCalculator`
    - `public const double DesiredWidthDip = 176, DesiredHeightDip = 40, PaddingDip = 8`
    - `public const double TrayFallbackWidthDip = 88` — 트레이 사각형이 없을 때 `tb.Right − 88 × scale`
    - `public static PixelRect? Compute(TaskbarGeometry g, WidgetPlacement placement)` — **작업 표시줄 기준(부모 상대)** 물리 px. Task 16 `TaskbarHost`가 `SetWindowPos`에 그대로 쓴다. overlay 모드에서는 `.Offset(g.Taskbar.Left, g.Taskbar.Top)`로 화면 좌표로 바꾼다.
  - **Task 16(`TaskbarGeometryReader`)이 지켜야 할 입력 규칙**: `Start`, `Widgets`, `AppButtons`, `Tray`에는 0이 아니고 작업 표시줄 안에 있는 사각형만 넣는다. `AppButtons`는 UIA `Taskbar.TaskListButtonAutomationPeer` 전체의 합집합이다. `Tray`는 `TrayNotifyWnd`나 UIA `SystemTrayIcon`의 가장 왼쪽(보조 작업 표시줄은 시계)이다. `IsCentered`는 `TaskbarAl`이 없거나 1이면 참이다.

**계산 규칙** (spec §4.3, research §3)

`scale = Dpi / 96`(Dpi가 0이면 1). `w = round(176 × scale)`, `h = min(tb.Height, round(40 × scale))`, `pad = round(8 × scale)`. 반올림은 `MidpointRounding.AwayFromZero`.

| 위치 설정 | 가운데 정렬 (`IsCentered`이고 `Start`를 앎) | 왼쪽 정렬 또는 `Start` 모름 |
|---|---|---|
| `LeftEdge` | 구간 `[(Widgets.Right ?? tb.Left) + pad, Start.Left − pad]`의 **왼쪽 끝**: `x = 왼쪽 경계` | `BeforeTray`로 계산 |
| `BeforeStart` | 같은 구간의 **오른쪽 끝**: `x = 오른쪽 경계 − w` | `BeforeTray`로 계산 |
| `BeforeTray` | 구간 `[(AppButtons.Right ?? Start.Right) + pad, trayLeft − pad]`의 오른쪽 끝. `trayLeft = Tray?.Left ?? tb.Right − round(88 × scale)`. 구간 안에 Widgets가 걸치면 오른쪽 경계를 `min(현재값, Widgets.Left − pad)`로 자른다 | 같음 |

- `LeftEdge`/`BeforeStart`의 `Widgets.Right`는 Widgets가 Start 왼쪽에 있을 때(`Widgets.Right <= Start.Left`)만 쓴다. 아니면 `tb.Left`를 쓴다.
- `BeforeTray`에서 `AppButtons`와 `Start`가 모두 없으면 `null`이다(왼쪽 경계를 모르면 앱 버튼과 겹칠 수 있다).
- 구간 폭이 `w`보다 작으면 `null`이다. 정확히 같으면 들어간다.
- 세로: `y = (tb.Height − h) / 2`(정수 나눗셈). 결과는 `(x − tb.Left, y, x − tb.Left + w, y + h)`이다.
- `IsHorizontal == false`이거나 `tb`가 비어 있으면 `null`이다. 결과가 `(0, 0, tb.Width, tb.Height)` 밖으로 나가면(예: 오래된 트레이 사각형) `null`이다.

**검증용 수치** (research §0 실측: 1920×1080, 100%, 가운데 정렬, Widgets 숨김)
- `Taskbar = (0,1032,1920,1080)`, `Start = (718,1032,763,1080)`, `AppButtons = (763,1032,1203,1080)`, `Tray = (1630,1032,1920,1080)`
- `LeftEdge → (8,4,184,44)`, `BeforeStart → (534,4,710,44)`, `BeforeTray → (1446,4,1622,44)`
- 보조 작업 표시줄 `(-1920,1032,0,1080)`, 트레이 없음 → `trayLeft = −88` → `BeforeTray → (1648,4,1824,44)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/AITokenUsage.Core.Tests/Taskbar/PixelRectTests.cs`:

```csharp
using AITokenUsage.Core.Taskbar;
using Xunit;

namespace AITokenUsage.Core.Tests.Taskbar;

public class PixelRectTests
{
    [Fact]
    public void SizeEmptyOffsetInside()
    {
        var r = new PixelRect(-1920, 1032, 0, 1080);

        Assert.Equal(1920, r.Width);
        Assert.Equal(48, r.Height);
        Assert.False(r.IsEmpty);
        Assert.True(new PixelRect(5, 5, 5, 10).IsEmpty);
        Assert.Equal(new PixelRect(0, 0, 1920, 48), r.Offset(1920, -1032));
        Assert.True(new PixelRect(8, 4, 184, 44).IsInside(new PixelRect(0, 0, 1920, 48)));
        Assert.False(new PixelRect(-1, 4, 184, 44).IsInside(new PixelRect(0, 0, 1920, 48)));
    }
}
```

`tests/AITokenUsage.Core.Tests/Taskbar/TaskbarLayoutCalculatorTests.cs`:

```csharp
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Taskbar;
using Xunit;

namespace AITokenUsage.Core.Tests.Taskbar;

public class TaskbarLayoutCalculatorTests
{
    // Ground truth from docs/research/win11-taskbar-embedding.md §0 (25H2 26200, 1920x1080, 100%, centered, Widgets hidden).
    private static readonly TaskbarGeometry GroundTruth = new(
        Taskbar: new PixelRect(0, 1032, 1920, 1080),
        Dpi: 96,
        IsHorizontal: true,
        IsCentered: true,
        Start: new PixelRect(718, 1032, 763, 1080),
        Widgets: null,
        AppButtons: new PixelRect(763, 1032, 1203, 1080),
        Tray: new PixelRect(1630, 1032, 1920, 1080));

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 8, 4, 184, 44)]
    [InlineData(WidgetPlacement.BeforeStart, 534, 4, 710, 44)]
    [InlineData(WidgetPlacement.BeforeTray, 1446, 4, 1622, 44)]
    public void GroundTruth_Centered_AllPlacements(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(GroundTruth, placement));
    }

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 158, 4, 334, 44)]
    [InlineData(WidgetPlacement.BeforeStart, 534, 4, 710, 44)]
    [InlineData(WidgetPlacement.BeforeTray, 1446, 4, 1622, 44)]
    public void Centered_WithWidgetsAtFarLeft_LeftEdgeStartsAfterWidgets(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        var g = GroundTruth with { Widgets = new PixelRect(0, 1032, 150, 1080) };

        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(g, placement));
    }

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge)]
    [InlineData(WidgetPlacement.BeforeStart)]
    public void Centered_WidgetsLeaveTooLittleRoom_ReturnsNull(WidgetPlacement placement)
    {
        var g = GroundTruth with { Widgets = new PixelRect(0, 1032, 560, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, placement));
    }

    [Fact]
    public void Centered_GapExactlyWideEnough_Fits_OnePixelLessDoesNot()
    {
        var fits = GroundTruth with { Start = new PixelRect(192, 1032, 237, 1080) };
        var tooSmall = GroundTruth with { Start = new PixelRect(191, 1032, 236, 1080) };

        Assert.Equal(new PixelRect(8, 4, 184, 44), TaskbarLayoutCalculator.Compute(fits, WidgetPlacement.LeftEdge));
        Assert.Null(TaskbarLayoutCalculator.Compute(tooSmall, WidgetPlacement.LeftEdge));
        Assert.Null(TaskbarLayoutCalculator.Compute(tooSmall, WidgetPlacement.BeforeStart));
    }

    [Fact]
    public void Centered_ManyAppsCrowdTheTray_BeforeTrayReturnsNull()
    {
        var g = GroundTruth with { AppButtons = new PixelRect(763, 1032, 1450, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void Centered_StartUnknown_FallsBackToBeforeTray()
    {
        var g = GroundTruth with { Start = null };

        Assert.Equal(new PixelRect(1446, 4, 1622, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void BeforeTray_NoAppButtons_UsesStartRight()
    {
        var g = GroundTruth with { AppButtons = null, Tray = new PixelRect(900, 1032, 1920, 1080) };

        // [763 + 8, 900 - 8] = [771, 892]: 121 px < 176 px.
        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
        Assert.Equal(new PixelRect(1446, 4, 1622, 44),
            TaskbarLayoutCalculator.Compute(GroundTruth with { AppButtons = null }, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void BeforeTray_NoAnchorAtAll_ReturnsNull()
    {
        var g = GroundTruth with { Start = null, AppButtons = null };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    private static readonly TaskbarGeometry LeftAligned = new(
        Taskbar: new PixelRect(0, 1032, 1920, 1080),
        Dpi: 96,
        IsHorizontal: true,
        IsCentered: false,
        Start: new PixelRect(12, 1032, 57, 1080),
        Widgets: new PixelRect(1460, 1032, 1620, 1080),
        AppButtons: new PixelRect(57, 1032, 497, 1080),
        Tray: new PixelRect(1630, 1032, 1920, 1080));

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge)]
    [InlineData(WidgetPlacement.BeforeStart)]
    [InlineData(WidgetPlacement.BeforeTray)]
    public void LeftAligned_AllPlacementsUseBeforeTray_ClippedAtWidgets(WidgetPlacement placement)
    {
        // Right bound min(1630 - 8, 1460 - 8) = 1452.
        Assert.Equal(new PixelRect(1276, 4, 1452, 44), TaskbarLayoutCalculator.Compute(LeftAligned, placement));
    }

    [Fact]
    public void LeftAligned_WithoutWidgets_HugsTray()
    {
        var g = LeftAligned with { Widgets = null };

        Assert.Equal(new PixelRect(1446, 4, 1622, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void LeftAligned_WidgetsLeaveTooLittleRoom_ReturnsNull()
    {
        var g = LeftAligned with { AppButtons = new PixelRect(57, 1032, 1200, 1080) };

        // [1208, 1452]: 244 px fits; push the apps further and it no longer does.
        Assert.NotNull(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
        Assert.Null(TaskbarLayoutCalculator.Compute(g with { AppButtons = new PixelRect(57, 1032, 1300, 1080) }, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void VerticalTaskbar_ReturnsNull()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 0, 48, 1080), IsHorizontal = false };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void EmptyTaskbarRect_ReturnsNull()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 1080, 1920, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    // Secondary taskbar on a monitor left of the primary (ground truth: Shell_SecondaryTrayWnd at x = -1920, no TrayNotifyWnd).
    private static readonly TaskbarGeometry Secondary = new(
        Taskbar: new PixelRect(-1920, 1032, 0, 1080),
        Dpi: 96,
        IsHorizontal: true,
        IsCentered: true,
        Start: new PixelRect(-1202, 1032, -1157, 1080),
        Widgets: null,
        AppButtons: new PixelRect(-1157, 1032, -761, 1080),
        Tray: null);

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 8, 4, 184, 44)]
    [InlineData(WidgetPlacement.BeforeStart, 534, 4, 710, 44)]
    [InlineData(WidgetPlacement.BeforeTray, 1648, 4, 1824, 44)]
    public void Secondary_NegativeCoordinates_ResultIsParentRelative(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        // BeforeTray: no tray → trayLeft = 0 - 88 = -88, right bound -96, x = -272 → -272 - (-1920) = 1648.
        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(Secondary, placement));
    }

    [Fact]
    public void Secondary_WithClockRect_UsesClockLeft()
    {
        var g = Secondary with { Tray = new PixelRect(-78, 1032, -8, 1080) };

        Assert.Equal(new PixelRect(1658, 4, 1834, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    // 125% (DPI 120): width 220, height min(60, 50) = 50, padding 10, y = 5.
    private static readonly TaskbarGeometry Scaled125 = new(
        Taskbar: new PixelRect(0, 1020, 1920, 1080),
        Dpi: 120,
        IsHorizontal: true,
        IsCentered: true,
        Start: new PixelRect(870, 1020, 926, 1080),
        Widgets: new PixelRect(0, 1020, 200, 1080),
        AppButtons: new PixelRect(926, 1020, 1200, 1080),
        Tray: new PixelRect(1550, 1020, 1920, 1080));

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 210, 5, 430, 55)]
    [InlineData(WidgetPlacement.BeforeStart, 640, 5, 860, 55)]
    [InlineData(WidgetPlacement.BeforeTray, 1320, 5, 1540, 55)]
    public void Dpi125_ScalesWidthHeightAndPadding(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(Scaled125, placement));
    }

    [Fact]
    public void Dpi125_Secondary_TrayFallbackIsScaled()
    {
        var g = Scaled125 with
        {
            Taskbar = new PixelRect(-1920, 1020, 0, 1080),
            Start = new PixelRect(-1050, 1020, -994, 1080),
            Widgets = null,
            AppButtons = new PixelRect(-994, 1020, -720, 1080),
            Tray = null,
        };

        // trayLeft = 0 - 110, right bound -120, x = -340 → 1580 relative.
        Assert.Equal(new PixelRect(1580, 5, 1800, 55), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    [Theory]
    [InlineData(96u, 48, 8, 4, 184, 44)]
    [InlineData(120u, 60, 10, 5, 230, 55)]
    [InlineData(144u, 72, 12, 6, 276, 66)]
    [InlineData(168u, 84, 14, 7, 322, 77)]
    public void LeftEdge_ScalesWithDpi(uint dpi, int taskbarHeight, int left, int top, int right, int bottom)
    {
        var g = new TaskbarGeometry(new PixelRect(0, 2160 - taskbarHeight, 3840, 2160), dpi, true, true,
            Start: new PixelRect(1800, 2160 - taskbarHeight, 1860, 2160), Widgets: null, AppButtons: null, Tray: null);

        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void ShortTaskbar_HeightIsClampedToTaskbar()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 1048, 1920, 1080) };

        Assert.Equal(new PixelRect(8, 0, 184, 32), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void OddHeightDifference_RoundsTopDown()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 1031, 1920, 1080) };

        Assert.Equal(new PixelRect(8, 4, 184, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void ZeroDpi_IsTreatedAs96()
    {
        Assert.Equal(new PixelRect(8, 4, 184, 44), TaskbarLayoutCalculator.Compute(GroundTruth with { Dpi = 0 }, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void TrayOutsideTaskbar_ResultOutsideParent_ReturnsNull()
    {
        var g = GroundTruth with { Tray = new PixelRect(2500, 1032, 2600, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~AITokenUsage.Core.Tests.Taskbar"`
Expected: 빌드 실패. `error CS0234: The type or namespace name 'Taskbar' does not exist in the namespace 'AITokenUsage.Core'`

- [ ] **Step 3: `PixelRect`, `TaskbarGeometry` 구현**

`src/AITokenUsage.Core/Taskbar/PixelRect.cs`:

```csharp
namespace AITokenUsage.Core.Taskbar;

/// <summary>A rectangle in physical pixels (right/bottom exclusive, like Win32 RECT).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
    public bool IsInside(PixelRect outer) => Left >= outer.Left && Top >= outer.Top && Right <= outer.Right && Bottom <= outer.Bottom;
}
```

`src/AITokenUsage.Core/Taskbar/TaskbarGeometry.cs`:

```csharp
namespace AITokenUsage.Core.Taskbar;

/// <summary>
/// Snapshot of one taskbar's layout. Every rectangle is in screen coordinates (physical px, PerMonitorV2).
/// A null element means "not found / not valid" (for example, secondary taskbars have no TrayNotifyWnd).
/// </summary>
public sealed record TaskbarGeometry(
    PixelRect Taskbar, uint Dpi, bool IsHorizontal, bool IsCentered,
    PixelRect? Start, PixelRect? Widgets, PixelRect? AppButtons, PixelRect? Tray);
```

- [ ] **Step 4: `TaskbarLayoutCalculator` 구현**

`src/AITokenUsage.Core/Taskbar/TaskbarLayoutCalculator.cs`:

```csharp
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Taskbar;

/// <summary>
/// Pure placement math for the widget inside the taskbar (spec §4.3). No Win32, no UIA.
/// </summary>
public static class TaskbarLayoutCalculator
{
    public const double DesiredWidthDip = 176, DesiredHeightDip = 40, PaddingDip = 8;

    /// <summary>Width assumed for the clock area when a (secondary) taskbar reports no tray rectangle.</summary>
    public const double TrayFallbackWidthDip = 88;

    /// <summary>작업 표시줄 기준(부모 상대) 물리 px 사각형. 공간 부족·세로 작업 표시줄이면 null.</summary>
    public static PixelRect? Compute(TaskbarGeometry g, WidgetPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(g);
        PixelRect tb = g.Taskbar;
        if (!g.IsHorizontal || tb.IsEmpty)
        {
            return null;
        }

        double scale = g.Dpi == 0 ? 1.0 : g.Dpi / 96.0;
        int width = Px(DesiredWidthDip, scale);
        int height = Math.Min(tb.Height, Px(DesiredHeightDip, scale));
        int pad = Px(PaddingDip, scale);

        // LeftEdge/BeforeStart only exist on a centered taskbar with a known Start button; otherwise use BeforeTray.
        bool useStartGap = g.IsCentered
            && g.Start is not null
            && (placement == WidgetPlacement.LeftEdge || placement == WidgetPlacement.BeforeStart);

        int? screenX = useStartGap
            ? LeftOfStart(g, placement, width, pad)
            : BeforeTray(g, width, pad, scale);
        if (screenX is null)
        {
            return null;
        }

        int left = screenX.Value - tb.Left;
        int top = (tb.Height - height) / 2;
        var rect = new PixelRect(left, top, left + width, top + height);
        return rect.IsInside(new PixelRect(0, 0, tb.Width, tb.Height)) ? rect : null;
    }

    /// <summary>Gap [(Widgets.Right ?? tb.Left) + pad, Start.Left - pad]: LeftEdge hugs its left end, BeforeStart its right end.</summary>
    private static int? LeftOfStart(TaskbarGeometry g, WidgetPlacement placement, int width, int pad)
    {
        PixelRect start = g.Start!.Value;
        int leftAnchor = g.Widgets is { } widgets && widgets.Right <= start.Left ? widgets.Right : g.Taskbar.Left;
        int leftBound = leftAnchor + pad;
        int rightBound = start.Left - pad;
        if (rightBound - leftBound < width)
        {
            return null;
        }

        return placement == WidgetPlacement.LeftEdge ? leftBound : rightBound - width;
    }

    /// <summary>
    /// Gap [(AppButtons.Right ?? Start.Right) + pad, trayLeft - pad], clipped at Widgets.Left - pad when the Widgets
    /// button lies inside it; the widget hugs the right end. trayLeft falls back to tb.Right - 88 DIP.
    /// </summary>
    private static int? BeforeTray(TaskbarGeometry g, int width, int pad, double scale)
    {
        int? anchor = g.AppButtons?.Right ?? g.Start?.Right;
        if (anchor is null)
        {
            return null;
        }

        int leftBound = anchor.Value + pad;
        int trayLeft = g.Tray?.Left ?? g.Taskbar.Right - Px(TrayFallbackWidthDip, scale);
        int rightBound = trayLeft - pad;
        if (g.Widgets is { } widgets && widgets.Right > leftBound && widgets.Left < rightBound)
        {
            rightBound = Math.Min(rightBound, widgets.Left - pad);
        }

        if (rightBound - leftBound < width)
        {
            return null;
        }

        return rightBound - width;
    }

    private static int Px(double dip, double scale) => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
}
```

- [ ] **Step 5: 테스트 실행 — 통과 확인**

Run: `dotnet test tests/AITokenUsage.Core.Tests --filter "FullyQualifiedName~AITokenUsage.Core.Tests.Taskbar"`
Expected: `Passed!  - Failed: 0, Passed: 37` (PixelRect 1 + TaskbarLayoutCalculator 36)

- [ ] **Step 6: 솔루션 전체 빌드·테스트**

Run: `dotnet build AITokenUsage.sln` → Expected: `0 Warning(s)`, `0 Error(s)`
Run: `dotnet test AITokenUsage.sln` → Expected: `Failed: 0`

- [ ] **Step 7: 커밋**

```bash
git add src/AITokenUsage.Core/Taskbar/PixelRect.cs src/AITokenUsage.Core/Taskbar/TaskbarGeometry.cs src/AITokenUsage.Core/Taskbar/TaskbarLayoutCalculator.cs tests/AITokenUsage.Core.Tests/Taskbar/PixelRectTests.cs tests/AITokenUsage.Core.Tests/Taskbar/TaskbarLayoutCalculatorTests.cs
git commit -m "feat(core): 작업 표시줄 위젯 배치 계산기 추가" -m "가운데/왼쪽 정렬과 위치 3종, Widgets 유무, 공간 부족, 세로 작업 표시줄, 음수 좌표 보조 모니터, 125% 배율을 실측 사각형 기반 테스트로 검증한다." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
