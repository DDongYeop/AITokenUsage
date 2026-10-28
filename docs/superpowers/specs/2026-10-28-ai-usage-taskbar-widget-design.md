# AI 사용량 작업 표시줄 위젯: 설계

- 작성일: 2026-10-28
- 상태: 승인됨 (설계 승인 2026-10-28)
- 참고 앱: `D:\Other\ClaudeUsage.exe`. WPF/.NET 8이고, 디컴파일해서 분석했다.
- 조사 근거: `docs/research/*.md`. 연구 에이전트가 조사하고 검증 에이전트가 원본 소스와 대조한 보고서다. **구현 시 API 필드명, 헤더, 경로의 정본은 이 보고서들이다.**

| 문서 | 내용 |
|---|---|
| [claude-usage-api.md](../../research/claude-usage-api.md) | Claude OAuth usage API, 자격 증명, 레이트 리밋 |
| [codex-usage-api.md](../../research/codex-usage-api.md) | Codex `wham/usage`, app-server JSON-RPC, rollout 로그 |
| [gemini-usage-api.md](../../research/gemini-usage-api.md) | Gemini CLI Code Assist quota API, 개인 계정 종료 |
| [copilot-usage-api.md](../../research/copilot-usage-api.md) | Copilot `copilot_internal/user`, 토큰 출처, 기기 코드 로그인 |
| [win11-taskbar-embedding.md](../../research/win11-taskbar-embedding.md) | Win11 작업 표시줄 자식 창 임베딩, DPI, 테마 |

---

## 1. 목표와 범위

### 1.1 목표
- Windows 11 작업 표시줄 안에 **작고 자연스러운** 위젯을 띄운다. 여러 AI 코딩 도구의 구독 사용량(5시간·주간·일간·월간 한도 %)을 보여준다.
- 한 번에 AI 하나씩 보여주고, **N초마다 자동으로 다음 AI로 넘긴다**. N은 설정에서 바꿀 수 있다.
- **설정 창**에서 표시할 AI, 순서, 줄 구성, 전환 간격, 위치, 모니터 등을 바꾼다.
- 지원하는 AI는 **Claude, Codex, Gemini CLI, GitHub Copilot**이다.

### 1.2 이번 버전에서 하지 않는 것
- Antigravity 공급자. AI Pro/Ultra용이며 별도 조사가 필요하다.
- Copilot 언어 서버의 `auth.db`(SQLite) 토큰 읽기, 그리고 Gemini 암호화 파일 저장소(`gemini-credentials.json`).
- 여러 모니터에 위젯을 동시에 표시하는 것. 한 개만 표시하고 모니터는 고를 수 있다.
- 세로(좌/우) 작업 표시줄. 이때는 위젯을 숨기고 트레이 아이콘만 남긴다.
- 위젯이 CLI 토큰을 **직접 갱신(refresh)하는 것**. 예외는 Gemini이며 메모리에서만 갱신한다(§5.3).

### 1.3 환경 전제
- Windows 11 24H2/25H2, x64. 개발 PC는 25H2 26200, 1920×1080 두 대, 100% 배율이다.
- .NET 10 SDK와 Desktop Runtime이 설치돼 있다.
- 개발 PC에는 Claude Code만 로그인돼 있다. Codex, Gemini, Copilot은 **샘플 데이터 기반 단위 테스트로만 검증한다.**

---

## 2. 사용자 경험

### 2.1 위젯 레이아웃 (2줄 미니바)

```
 ✳ Claude   5h ▰▰▱▱▱▱ 14%        약 176 × 40 DIP, 배경 투명
   ● ○ ○    주 ▰▰▰▱▱▱ 50%
```

| 영역 | 내용 | 크기 (DIP) |
|---|---|---|
| 왼쪽 열 | AI 아이콘 14px, 이름(12px SemiBold). 그 아래 위치 점(지름 5px, 간격 4px) | 폭 약 66 |
| 오른쪽 각 줄 | 라벨(10.5px, 흐린 색, 폭 18) + 바(44×4, 둥근 모서리) + 값(11.5px SemiBold, 최소 폭 32, 오른쪽 정렬) | 폭 약 100 |
| 전체 | 높이 40. 작업 표시줄 높이가 40보다 작으면 그 높이를 쓴다 | 폭 약 176 |

**세부 규칙**
- **위치 점**은 표시 대상 AI가 2개 이상일 때만 보인다.
- **값**은 기본으로 "사용한 %"를 보여준다. 설정에서 "남은 %"(= 100 − 사용)로 바꿀 수 있고, 그때는 라벨 앞에 `잔` 표기 없이 값만 바뀌며 바도 남은 양을 그린다.
- **바 색상**
  - 기본(normal)은 AI 브랜드 색이다: Claude `#D97757`, Codex `#10A37F`, Gemini `#4285F4`, Copilot `#8957E5`.
  - 경고(warning)는 `#F5A524`, 위험(critical)은 `#F04438`이다.
  - 등급은 공급자가 주는 `severity`를 우선 쓴다. 없으면 사용 75% 이상을 경고, 90% 이상을 위험으로 본다.
- **글자색**: 작업 표시줄이 다크면 흰색, 라이트면 검정이다. 보조 텍스트는 불투명도 0.65다.
- **바 트랙**: 글자색에 불투명도 0.18을 준다.
- **폰트**: `Segoe UI Variable Text`이며, 없으면 `Segoe UI`를 쓴다.

**AI별 기본 두 줄** (설정에서 줄마다 바꿀 수 있다)

| AI | 1줄 | 2줄 | 고를 수 있는 다른 항목 |
|---|---|---|---|
| Claude | `5h` 세션 | `주` 주간 전체 | 모델별 주간 (`limits[].kind == weekly_scoped`, 예: Fable) |
| Codex | `5h` | `주` 주간 | 추가 한도(`additional_rate_limits`)의 창 |
| Gemini | `Pro` 일간 | `Fl` Flash 일간 | `Lt` Flash-Lite 일간 |
| Copilot | `월` 크레딧 또는 프리미엄 요청 | `↻ 11/1`, 리셋 날짜 텍스트 줄 | Free 플랜의 `완성` 코드 완성 |

**상태별 위젯 표시**

| 상태 | 표시 |
|---|---|
| 정상 | 두 줄 바 |
| 이전 값 (마지막 조회 실패) | 값과 바를 불투명도 0.55로 표시, 이름 옆에 작은 `·` |
| 로그인 필요 / 설치 안 됨 | 2줄 자리에 "로그인 필요" 또는 "설치 안 됨" (흐린 색) |
| CLI 갱신 대기 | 이전 값을 흐리게 표시하고 2줄 대신 "갱신 대기" |
| 지원 안 됨 (API 키, Gemini 개인 계정 종료) | "지원 안 됨" 또는 "지원 종료" |
| 요청 제한(429) | 이전 값을 흐리게 표시 |

### 2.2 자동 전환
- **간격**: 기본 5초, 범위 2–60초, 1초 단위.
- **전환 애니메이션**: 현재 패널은 위로 8 DIP 이동하며 사라지고, 새 패널은 아래에서 8 DIP 올라오며 나타난다. 길이는 220ms, `CubicEase EaseOut`이다. 투명 창은 매 프레임 GPU→CPU 복사가 일어나므로 애니메이션은 이 전환에만 쓴다.
- **멈춤**: 마우스를 올리면 멈추고(설정으로 끌 수 있다), 떠나면 타이머를 처음부터 다시 센다.
- **수동 전환**
  - 왼쪽 클릭: 다음 AI.
  - 휠 아래/위: 다음/이전 AI.
  - 수동 전환을 하면 타이머를 처음부터 다시 센다.
- **표시 대상**: 설정에서 켠 AI를 설정 순서대로 보여준다.
  - "연결 안 된 AI 건너뛰기"(기본 켜짐)가 켜져 있으면, 상태가 `NotInstalled`, `SignedOut`, `Unsupported`이면서 캐시된 값도 없는 AI는 뺀다.
  - 대상이 0개면 켜진 AI 중 첫 번째의 상태 메시지를 보여준다. 켜진 AI도 없으면 "AI 사용량 · 우클릭하여 설정"을 보여준다.
  - 대상이 1개면 전환하지 않고 점도 숨긴다.
- **일시정지**: 우클릭 메뉴의 "자동 전환 일시정지"는 앱을 다시 시작하면 풀린다.

### 2.3 상세 카드 (호버)
- **동작**: 위젯 위에 마우스를 400ms 올리면, 위젯 바로 위(작업 표시줄 위쪽)에 카드가 뜬다. 떠나면 200ms 뒤 닫힌다.
- **창 속성**: 별도 최상위 창이다. `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, Topmost이며 그림자가 있다. 배경은 시스템 테마를 따른다(다크 `#2B2B2B`, 라이트 `#F9F9F9`, 둥근 모서리 8).
- **내용** (현재 보이는 AI 기준)
  - 제목 줄: 아이콘, AI 이름, 플랜(예: `Team · Max 5x`, `Pro`, `Copilot Pro+`).
  - 한도마다 한 줄: 긴 라벨(예: "현재 세션 (5시간)", "주간 · 모든 모델", "주간 · Fable"), 넓은 바(160 DIP), %, 리셋 시각.
    - 리셋 시각 형식은 `3시간 12분 후 · 오늘 15:50`, `1일 8시간 후 · 10/30(목) 05:00`, `11/1(토)`이다.
  - Claude 추가 사용량(`extra_usage.is_enabled == true`일 때): `추가 사용 $12.40 / $50.00`. 단위 변환 규칙은 `claude-usage-api.md` §2.3을 따른다.
  - 하단 상태 줄: `1분 전 갱신`, `요청 제한 — 14:20 이후 재시도`, `Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다` 등.

### 2.4 우클릭 메뉴와 트레이 아이콘
- **메뉴 항목**: `설정…` · `지금 새로고침` · `자동 전환 일시정지`(체크) · 구분선 · `종료`.
  - 위젯 우클릭과 트레이 아이콘 우클릭에 같은 메뉴를 쓴다.
- **트레이 아이콘**: 왼쪽 클릭하면 설정 창이 열린다. 설정의 "트레이 아이콘 표시"는 기본 켜짐이다.
  - 아이콘은 16/32px 원 안에 현재 보이는 AI의 1줄 값을 숫자로 그린다(참고 앱 방식). 색은 위젯 바 색 규칙을 따른다.
  - 툴팁 예: `Claude 5h 14% · 주 50%`.
- **위젯을 숨기는 경우**: 위젯 자리가 부족하거나 세로 작업 표시줄이면 위젯을 숨긴다. 그동안에도 트레이 아이콘으로 설정 창에 접근할 수 있다.

### 2.5 설정 창
- **창 형태**: 일반 최상위 창, 약 480×620 DIP, 크기 조절 불가, 작업 표시줄에 표시. 테마는 시스템 앱 테마(`AppsUseLightTheme`)를 따른다.
- **적용 방식**: 변경하면 **즉시 적용하고 자동 저장**한다. 확인/취소 버튼은 없다.

```
┌ AI 사용량 설정 ─────────────────────────────── ✕ ┐
│ 표시할 AI                                          │
│ [✓] ✳ Claude   연결됨 · Team (Max 5x)        ▲ ▼  │
│      1줄 [세션(5h) ▾]  2줄 [주간 ▾]                │
│ [✓] ◎ Codex    설치 안 됨 — codex login 필요  ▲ ▼  │
│      1줄 [5h ▾]        2줄 [주간 ▾]                │
│ [✓] ✦ Gemini   개인 계정 지원 종료 (06-18)    ▲ ▼  │
│ [✓] ⬡ Copilot  로그인 필요 [GitHub 로그인]    ▲ ▼  │
│ 자동 전환                                          │
│ [✓] 여러 AI 자동으로 넘기기   간격 ──●──── 5초     │
│ [✓] 마우스 올리면 멈춤  [✓] 연결 안 된 AI 건너뛰기  │
│ 표시                                               │
│ 모니터 [주 모니터 ▾]   위치 [작업 표시줄 왼쪽 끝 ▾] │
│ 수치 (●) 사용한 % ( ) 남은 %   글자색 [자동 ▾]     │
│ 일반                                               │
│ 새로고침 [5분 ▾]  [✓] 시작 시 자동 실행  [✓] 트레이 │
└────────────────────────────────────────────────────┘
```

| 설정 | 값 | 기본 |
|---|---|---|
| AI 켜기/끄기, 순서 | 4개, ▲▼로 이동 | 모두 켜짐. 순서는 Claude, Codex, Gemini, Copilot |
| AI별 1줄, 2줄 | 그 AI가 제공하는 미터 키 목록(드롭다운) | §2.1 표 |
| 자동 전환 | 켜기/끄기 | 켜짐 |
| 전환 간격 | 2–60초 슬라이더와 숫자 표시 | 5초 |
| 마우스 올리면 멈춤 | 켜기/끄기 | 켜짐 |
| 연결 안 된 AI 건너뛰기 | 켜기/끄기 | 켜짐 |
| 모니터 | "주 모니터", "모니터 2 (1920×1080, 왼쪽)" 등. 연결된 모니터 목록 | 주 모니터 |
| 위치 | 작업 표시줄 왼쪽 끝 / 시작 버튼 왼쪽 / 알림 영역 왼쪽 | 왼쪽 끝 |
| 수치 | 사용한 % / 남은 % | 사용한 % |
| 글자색 | 자동(작업 표시줄 테마) / 밝게 / 어둡게 | 자동 |
| 새로고침 간격 | 2 / 5 / 10 / 15분 | 5분 |
| Windows 시작 시 자동 실행 | 켜기/끄기 (`HKCU\...\Run`) | 꺼짐 |
| 트레이 아이콘 표시 | 켜기/끄기 | 켜짐 |

**AI 행의 상태 텍스트 예시**
- `연결됨 · Team (Max 5x) · 1분 전`
- `설치 안 됨 — codex 설치 후 codex login`
- `로그인 필요 — 터미널에서 claude 실행 후 /login`
- `Claude Code의 로그인 갱신 대기 중`
- `API 키 로그인 — 사용량 조회 불가`
- `개인 계정 지원 종료 (2026-06-18) — Antigravity 사용`
- `요청 제한 — 14:20 이후 재시도`

**Copilot 행** (Copilot 토큰을 하나도 찾지 못했거나, 사용자가 원하는 경우)
- **[GitHub 로그인]** 버튼으로 기기 코드 흐름을 시작한다(§5.4).
  - 진행 중에는 코드 `ABCD-1234`와 [복사] 버튼을 보여주고, 브라우저로 `https://github.com/login/device`를 연다.
- 로그인한 뒤에는 `GitHub: <login>`과 [로그아웃]을 보여준다.
- 작은 글씨로 "비공식 API를 사용합니다"라고 안내한다.

---

## 3. 아키텍처

### 3.1 솔루션 구성

```
AITokenUsage.sln
├─ src/AITokenUsage.Core/            net10.0-windows (WPF 없음). UI 의존 없음. 전부 단위 테스트 대상
│   ├─ Models/        UsageSnapshot, UsageMeter, ProviderStatus, Severity, ProviderInfo
│   ├─ Providers/     IUsageProvider, ProviderContext (HttpClient, 경로, 시계, 환경 변수, 프로세스 실행기)
│   │   ├─ Claude/    ClaudeCredentialReader, ClaudeUsageClient, ClaudeUsageParser, ClaudeVersionProbe
│   │   ├─ Codex/     CodexAuthReader, CodexUsageClient, CodexUsageParser, CodexAppServerClient,
│   │   │             CodexRolloutReader, CodexWindowLabeler
│   │   ├─ Gemini/    GeminiCredentialReader, GeminiTokenRefresher, GeminiQuotaClient, GeminiQuotaParser
│   │   └─ Copilot/   CopilotTokenSources, CopilotUsageClient, CopilotUsageParser, GitHubDeviceFlow
│   ├─ Polling/       UsageService, BackoffPolicy, SnapshotCache
│   ├─ Rotation/      RotationController
│   ├─ Settings/      AppSettings, SettingsStore
│   ├─ Taskbar/       TaskbarLayoutCalculator (사각형만 받는 순수 계산)
│   └─ Util/          SharedFileReader (FileShare.ReadWrite|Delete + 재시도), Jwt, ResetTimeFormatter (한국어),
│                     CredentialManager (CredRead/CredEnumerate P/Invoke), SecretProtector (DPAPI)
├─ src/AITokenUsage/                 net10.0-windows. WPF. WinExe
│   ├─ App.xaml(.cs)  단일 인스턴스, 구성(조립) 루트, ShutdownMode=OnExplicitShutdown
│   ├─ app.manifest   PerMonitorV2 + supportedOS Win10
│   ├─ Taskbar/       NativeMethods, TaskbarLocator, TaskbarGeometryReader (UIA), TaskbarHost,
│   │                 ShellEventWindow, ThemeWatcher, FullscreenWatcher
│   └─ UI/            WidgetWindow, ProviderPanel, DetailFlyout, SettingsWindow, TrayIconController,
│                     ProviderGlyphs, Brushes/Theme 리소스
└─ tests/AITokenUsage.Core.Tests/    xUnit. Fixtures/ 아래에 JSON/JSONL 샘플
```

`CredentialManager`와 `SecretProtector`는 Windows 전용 API를 쓴다. Core의 TFM은 `net10.0-windows`로 둔다(WPF 의존 없이). 테스트는 Windows에서 돈다.

### 3.2 핵심 타입

```csharp
public enum ProviderStatus { Ok, NotInstalled, SignedOut, WaitingForCliRefresh, Unsupported, RateLimited, Error }
public enum Severity { Normal, Warning, Critical }

public sealed record UsageMeter(
    string Key,               // "session", "weekly", "weekly:fable", "pro", "monthly" …
    string ShortLabel,        // 위젯용 2~3글자: "5h", "주", "Pro", "월"
    string LongLabel,         // 카드용: "현재 세션 (5시간)"
    double? UsedPercent,      // 0–100. 표시할 때 클램프
    DateTimeOffset? ResetsAt,
    Severity? ServerSeverity, // 공급자가 준 값. 없으면 null
    string? TextOverride = null); // 예: Copilot "↻ 11/1" 같은 텍스트 줄, "N 크레딧 사용"

public sealed record UsageSnapshot(
    string ProviderId,
    ProviderStatus Status,
    string? PlanLabel,
    IReadOnlyList<UsageMeter> Meters,
    DateTimeOffset FetchedAt,
    string? StatusMessage,    // 한국어 사용자 메시지
    TimeSpan? RetryAfter,     // 레이트 리밋이나 대기 힌트
    string? ExtraLine = null);// 예: Claude 추가 사용량

public interface IUsageProvider
{
    string Id { get; }            // "claude" | "codex" | "gemini" | "copilot"
    string DisplayName { get; }
    TimeSpan MinInterval { get; } // 이 공급자가 허용하는 최소 조회 간격
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);
    IEnumerable<string> WatchPaths { get; } // 바뀌면 즉시 재조회할 자격 증명 파일·디렉터리
}
```

`ProviderContext`가 주입하는 것:
- `HttpMessageHandler`
- `TimeProvider`
- `IEnvironment`(환경 변수, 사용자 홈)
- `IFileSystem`(파일 읽기·열거)
- `IProcessRunner`(`gh`, `codex` 실행)
- `ICredentialStore`(CredMan)

테스트에서는 이 모두를 가짜로 바꾼다.

### 3.3 데이터 흐름

```
UsageService (백그라운드 루프, 공급자당 1개)
   ├─ 주기: max(설정 간격, provider.MinInterval) ± 10% 지터
   ├─ FileSystemWatcher(WatchPaths) 변경 → 디바운스 1.5초 후 즉시 조회 (단, 레이트 리밋 차단 중이면 무시)
   ├─ "지금 새로고침" → 모든 공급자 즉시 조회 (레이트 리밋 차단은 존중)
   ├─ 결과 처리:
   │    Ok                  → 캐시 갱신, 백오프 초기화
   │    RateLimited/Error   → 마지막 정상 값을 유지하고 상태만 갱신, BackoffPolicy로 다음 시각 계산
   │    그 외 상태           → 마지막 정상 값 유지 (WaitingForCliRefresh 등)
   └─ SnapshotChanged 이벤트 → (Dispatcher) → WidgetWindow / DetailFlyout / SettingsWindow / TrayIcon

RotationController (UI 스레드 DispatcherTimer)
   └─ 표시 대상 목록 = 설정 순서 ∩ 켜진 AI ∩ (건너뛰기 규칙) → CurrentProviderId 변경 이벤트 → 전환 애니메이션
```

- **스냅샷 표시 규칙**: UI에 넘기는 스냅샷은 `현재 상태 + 마지막 정상 Meters`의 합성이다. 상태가 `Ok`가 아니고 이전 값이 있으면 "이전 값" 스타일로 그린다.
- **캐시 파일**: `%LOCALAPPDATA%\AITokenUsage\cache.json`에 공급자별 마지막 정상 스냅샷을 둔다. **토큰과 응답 원문은 저장하지 않는다.** 시작 시 읽어서 즉시 표시한다.
- **백오프 (`BackoffPolicy`)**
  - 429, 또는 Anthropic 오류 JSON이 없는 403: `max(Retry-After, 5분)`부터 시작해 2배씩 늘리고, 최대 60분.
  - 네트워크·5xx 오류: `max(간격, 2분)`부터 시작해 2배씩 늘리고, 최대 30분.
  - 401: 공급자별 규칙(§5)에 따른다.

### 3.4 스레딩
- **UI 스레드**: 렌더링과 사용자 입력만 처리한다. 위젯이 Explorer 창의 자식이면 두 창의 입력 큐가 연결되기 때문에, UI 스레드가 막히면 작업 표시줄도 같이 굳는다.
- HTTP, 파일 I/O, 프로세스 실행, UIA 조회는 **모두 백그라운드**에서 한다.
- UIA는 전용 백그라운드 스레드 하나에서만 호출한다.

### 3.5 설정·저장 위치

| 파일 | 내용 |
|---|---|
| `%APPDATA%\AITokenUsage\settings.json` | `AppSettings`. `version` 필드를 둬서 마이그레이션할 수 있게 한다 |
| `%LOCALAPPDATA%\AITokenUsage\cache.json` | 마지막 정상 스냅샷 |
| `%LOCALAPPDATA%\AITokenUsage\copilot-token.bin` | 기기 코드 로그인 토큰, DPAPI `CurrentUser` |
| `%LOCALAPPDATA%\AITokenUsage\logs\app.log` | 1MB 넘으면 교체, 최대 3개. **토큰, Authorization 헤더, 응답 원문은 기록 금지** |

- **단일 인스턴스**: Mutex `Local\AITokenUsage.SingleInstance`를 쓴다. 두 번째로 실행하면 이름 있는 이벤트 `Local\AITokenUsage.ShowSettings`로 첫 인스턴스에 "설정 창 열기"를 알리고 끝난다.

---

## 4. 작업 표시줄 임베딩 (`TaskbarHost`)

근거: `docs/research/win11-taskbar-embedding.md`.

### 4.1 대상 작업 표시줄 찾기 (`TaskbarLocator`)
1. `FindWindowEx(0, prev, "Shell_TrayWnd")`로 **같은 클래스의 창을 모두 나열한다.** 그중 소유 프로세스가 `explorer.exe`인 창을 고른다. 다른 작업 표시줄 대체 프로그램도 같은 클래스명을 쓰기 때문이다.
2. 보조 모니터에는 `Shell_SecondaryTrayWnd`를 같은 방법으로 찾는다. 설정의 모니터와 `MonitorFromWindow`로 짝을 맞춘다.
3. Win11 XAML 작업 표시줄인지 확인한다. 자식으로 `Windows.UI.Composition.DesktopWindowContentBridge`가 없으면 overlay 모드로 간다.
4. 방향은 `ABM_GETTASKBARPOS`의 `uEdge`와 사각형 가로세로 비로 판정한다. 세로(좌/우)면 위젯을 숨긴다.

### 4.2 자식 창으로 부착
- **창 속성**: `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `ShowInTaskbar=False`, `ShowActivated=False`, `ResizeMode=NoResize`, `Topmost=False`, `UseLayoutRounding=True`.
- **루트 배경**: `#01000000`로 둔다. 알파가 0인 픽셀은 클릭이 아래로 통과하기 때문이다.
- **부착 순서**
  1. `EnsureHandle()`.
  2. 스타일에서 `WS_POPUP|WS_CAPTION|WS_THICKFRAME|WS_SYSMENU|WS_MINIMIZEBOX|WS_MAXIMIZEBOX`를 빼고 `WS_CHILD|WS_CLIPSIBLINGS`를 더한다.
  3. 확장 스타일에 `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`를 더하고 `WS_EX_APPWINDOW`를 뺀다. **`WS_EX_LAYERED`는 건드리지 않는다.**
  4. `SetWindowRgn(빈 영역)`으로 첫 배치 전까지 접어 둔다.
  5. `SetParent(hwnd, taskbar)`.
  6. `GetAncestor(hwnd, GA_PARENT) == taskbar`로 부착을 확인한다. 실패하면 4.5의 overlay 모드로 간다.
  7. `SWP_FRAMECHANGED`를 적용하고 `Show()`.
- **부착 후 금지 사항**: `Window.Left/Top/Width/Height/SizeToContent/Owner/ShowInTaskbar`를 절대 바꾸지 않는다. 위치는 `SetWindowPos`(부모 기준 물리 픽셀)로만 옮긴다. 내용 크기는 루트 요소의 DIP 값으로 정한다.
- **부착 시점**: 부팅 직후에는 작업 표시줄이 아직 준비되지 않았을 수 있다. UIA로 `StartButton`이 보일 때까지 1초마다 확인하고, 최대 15초 기다린다.

### 4.3 배치 계산
- **입력 (`TaskbarGeometryReader`)**
  - 백그라운드에서 UIA로 읽는다: 작업 표시줄 사각형, DPI, `StartButton`, `WidgetsButton`, 앱 버튼들(`Taskbar.TaskListButtonAutomationPeer`), 알림 영역(`SystemTrayIcon`/`NotifyItemIcon`).
  - 빠른 확인용으로 숨겨진 `Start` HWND 사각형과 `TrayNotifyWnd` 사각형도 쓴다. 0이 아니고 작업 표시줄 안에 있는 값만 유효로 본다.
  - 레지스트리 값 `TaskbarAl`(없으면 가운데 정렬)과 `TaskbarDa`도 읽는다.
- **순수 계산 (`TaskbarLayoutCalculator`, Core)**: 위 사각형들과 위치 설정, 위젯의 원하는 크기를 받아 위젯 사각형을 돌려준다(부모 기준 물리 px). 공간이 모자라면 `null`을 돌려준다.

  | 위치 설정 | 가운데 정렬 | 왼쪽 정렬 |
  |---|---|---|
  | **왼쪽 끝** | `[(Widgets.Right ?? tb.Left) + 8, Start.Left − 8]` 구간의 왼쪽에 붙임 | "알림 영역 왼쪽"으로 대체 |
  | **시작 버튼 왼쪽** | 같은 구간의 오른쪽에 붙임 | "알림 영역 왼쪽"으로 대체 |
  | **알림 영역 왼쪽** | `[앱 버튼 그룹 Right(없으면 Start.Right) + 8, 알림 영역 Left − 8]` 구간의 오른쪽에 붙임. 그 구간에 Widgets 버튼이 있으면 `Widgets.Left`에서 자른다 | 같음 |

  - 세로 위치는 `y = (tb.Height − h) / 2`, 높이는 `h = min(tb.Height, 40 × scale)`이다.
  - 보조 작업 표시줄에는 `TrayNotifyWnd`가 없다. UIA `SystemTrayIcon`(시계)을 쓰고, 그것도 없으면 `tb.Right − 88 × scale`을 쓴다.
- **조정 루프**: 500ms `DispatcherTimer`. 무거운 UIA 결과는 백그라운드에서 최대 3초 주기로, 또는 이벤트가 있을 때 갱신해 캐시한다.
  - 부모가 바뀌었으면(시작 메뉴나 검색이 열리면 셸이 부모를 바꾸기도 한다) `SetParent`를 다시 한다.
  - 계산한 사각형과 실제 `GetWindowRect`가 **다를 때만** `SetWindowPos(HWND_TOP, …, SWP_NOACTIVATE|SWP_SHOWWINDOW)`와 `SetWindowRgn`을 호출한다.
  - 계산 결과가 `null`이면 숨긴다.

### 4.4 이벤트와 복구 (`ShellEventWindow`)
- 숨겨진 **최상위** `WS_POPUP` `HwndSource`가 브로드캐스트 메시지를 받는다. 메시지 전용 창(`HWND_MESSAGE`)은 브로드캐스트를 받지 못한다.

| 메시지 | 처리 |
|---|---|
| `RegisterWindowMessage("TaskbarCreated")` (탐색기 재시작, 주 모니터 DPI 변경) | 위젯 창을 버리고 **새로** 만든 뒤, 1초 대기 + StartButton 대기 후 다시 부착 |
| `WM_DISPLAYCHANGE`, `WM_DPICHANGED` | 즉시 다시 배치 |
| `WM_SETTINGCHANGE` + lParam `"ImmersiveColorSet"` | 테마 다시 읽기 |

- 조정 루프에서 `IsWindow(taskbar)`가 거짓이거나 `GetDpiForWindow(taskbar)`가 기준값과 다르면 창을 다시 만든다.
- **부착 실패 재시도**: 10초 간격으로 다시 시도하고, 그동안은 overlay 모드로 표시한다.

### 4.5 대체 모드: overlay (작업 표시줄이 소유하는 창)
- **진입 조건**: 자식 창 부착에 실패했거나, Win11 작업 표시줄이 아닐 때.
- `new WindowInteropHelper(w).Owner = taskbar`를 `Show()` **전에** 설정한다. 그러면 소유자인 작업 표시줄보다 항상 위에 있다. 화면 좌표로 배치한다.
- **전체 화면 감지 (`FullscreenWatcher`)**: 2초마다 `SHQueryUserNotificationState`를 확인한다. 값이 2, 3, 4이면 숨긴다. 추가로, 대상 모니터를 덮는 전경 창이 있는지 TrafficMonitor 방식으로 확인한다.
- **자동 숨김**: `ABM_GETSTATE & ABS_AUTOHIDE`이고 작업 표시줄 사각형이 모니터 밖으로 나가 있으면 숨긴다.

### 4.6 테마 (`ThemeWatcher`)
- **값 출처**: `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`의 `SystemUsesLightTheme`. 1이면 라이트, 0이거나 없으면 다크다.
- **갱신 시점**: `ImmersiveColorSet` 메시지를 받을 때, 그리고 2초 주기의 레지스트리 폴링(대비책).
- **설정 우선**: 글자색 설정이 "밝게"나 "어둡게"면 그 값을 쓴다.
- **화면 픽셀 샘플링은 하지 않는다.**

### 4.7 매니페스트

```xml
<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
  <application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application>
</compatibility>
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
  </windowsSettings>
</application>
```

- 관리자 권한은 필요 없다(`asInvoker`).
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`를 켜서 `LibraryImport`를 쓴다.

---

## 5. 공급자별 동작

모든 공급자에 공통으로 적용하는 규칙:
- 자격 증명 파일은 `FileShare.ReadWrite | FileShare.Delete`로 열어 한 번에 읽고 바로 닫는다. JSON 파싱에 실패하면 50–300ms 간격으로 최대 3회 다시 읽는다. **파싱 실패를 로그아웃으로 취급하지 않는다.**
- **CLI의 자격 증명 파일에는 절대 쓰지 않는다.**
- HTTP 타임아웃은 15초이고 `AllowAutoRedirect=false`다.
- 백분율은 `double`로 파싱하고, 표시할 때 0–100으로 클램프한다.

### 5.1 Claude (`claude-usage-api.md`)
- **자격 증명 경로**
  - 환경 변수 `CLAUDE_SECURESTORAGE_CONFIG_DIR`가 **정의돼 있으면** 그 값을 쓴다. 값이 비어 있으면 `~/.claude`를 쓴다.
  - 없으면 `CLAUDE_CONFIG_DIR`, 그것도 없으면 `%USERPROFILE%\.claude`다.
  - 파일은 `.credentials.json`이고, 읽는 값은 `claudeAiOauth.{accessToken, expiresAt(ms), scopes[], subscriptionType, rateLimitTier}`다.
- **상태 판정**

  | 조건 | 상태 / 메시지 |
  |---|---|
  | 디렉터리나 파일이 없음 | `NotInstalled` |
  | `accessToken`이 비어 있거나 `expiresAt == 0` | `SignedOut`, "터미널에서 claude 실행 후 /login" |
  | `scopes`에 `user:profile`이 없음 | `Unsupported`, "이 로그인은 사용량 조회 불가" |
  | `expiresAt − now < 5분` | **조회하지 않음**. `WaitingForCliRefresh`. 파일이 바뀌면 FileSystemWatcher로 즉시 재조회 |

- **요청**
  - `GET https://api.anthropic.com/api/oauth/usage`에 쿼리 스트링 없이 보낸다.
  - 헤더는 `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20`, `User-Agent: claude-code/<ver>`, `Accept: application/json`이다.
  - `<ver>`를 고르는 순서(`ClaudeVersionProbe`): 실행 중인 `claude.exe` 파일 버전 → `%USERPROFILE%\.vscode\extensions\anthropic.claude-code-*` 폴더명 중 가장 높은 버전 → `2.1.311`.
- **파싱**
  - 세션은 `limits[kind=="session"].percent`이고, 없으면 `five_hour.utilization`이다.
  - 주간 전체는 `limits[kind=="weekly_all"]`이고, 없으면 `seven_day`다.
  - 모델별 주간: `limits[kind=="weekly_scoped"]` 각각을 키 `weekly:<display_name 소문자>`, 라벨 `주·<이름 앞 2글자>`로 만든다. 레거시 `seven_day_sonnet`/`seven_day_opus`가 null이 아니면 이것도 추가한다.
  - `severity`는 `normal`/`warning`/`critical` 문자열을 그대로 쓴다.
  - `resets_at`은 분 단위로 반올림한다. 호출마다 1초 정도씩 흔들리기 때문이다.
  - `limits` 항목 하나가 잘못돼도 나머지는 살린다(`JsonElement`로 항목마다 따로 파싱).
- **플랜 라벨**: `subscriptionType`(`max`/`pro`/`team`/`enterprise`)을 대문자로 시작하게 바꾸고, `rateLimitTier`에서 `max_(\d+x)`를 뽑아 붙인다. 예: `Team · Max 5x`.
- **추가 사용량**: `extra_usage.is_enabled`면 `used_credits/monthly_limit`을 `10^decimal_places`(없으면 100)로 나눠 통화와 함께 `ExtraLine`에 넣는다.
- **오류 처리**

  | 응답 | 처리 |
  |---|---|
  | 401 | 파일을 다시 읽는다. 토큰이 바뀌었으면 한 번만 재시도하고, 아니면 `WaitingForCliRefresh`. 같은 토큰으로는 1시간 동안 다시 호출하지 않는다 |
  | 403 + `{"error":{"type":…}}` | `Unsupported`. 1시간 동안 또는 토큰이 바뀔 때까지 대기 |
  | 그 밖의 403, 429 | `RateLimited` (`Retry-After`가 0이거나 없으면 5분) |

- **간격**: `MinInterval = 2분`. 기본 설정 간격은 5분이다.

### 5.2 Codex (`codex-usage-api.md`)
- **경로와 읽기**
  - 기준 디렉터리는 `CODEX_HOME`, 없으면 `%USERPROFILE%\.codex`다. `auth.json`은 공유 모드로 읽는다(이게 **필수**인 이유는 보고서 §1.2).
  - `chatgpt_base_url`은 `config.toml`에서 간단한 정규식으로 읽는다. 기본은 `https://chatgpt.com/backend-api/`다.
- **모드 판정**: `auth_mode == "apikey"`이면 `Unsupported`, "API 키 로그인 — 플랜 한도 없음"이다. `OPENAI_API_KEY` 값만으로는 판단하지 않는다.
- **토큰이 신선할 때** (access_token JWT `exp > now + 5분`이거나, `exp`를 알 수 없고 `last_refresh`가 8일 이내)
  - `GET {base}/wham/usage`를 보낸다. base에 `/backend-api`가 없으면 `{base}/api/codex/usage`다.
  - 헤더는 `Authorization: Bearer`, `chatgpt-account-id: <tokens.account_id 또는 JWT 클레임>`, `User-Agent: AITokenUsage/<ver>`, `Accept: application/json`이다. id_token에 `chatgpt_account_is_fedramp`가 있으면 `X-OpenAI-Fedramp: true`도 보낸다.
  - **`x-openai-codex-luna-reserve`는 보내지 않는다.**
- **토큰이 오래됐거나 401에 토큰도 바뀌지 않았을 때**
  - `codex`가 설치돼 있으면 `codex -s read-only -a never app-server`를 실행한다. JSON-RPC로 `initialize`(8초) → `initialized` → `account/rateLimits/read`(3초, `excludeResetCreditDetails: true`) 순서로 호출하고 종료한다.
  - **최대 15분에 1회**만 실행하고, 실패하면 rollout 로그 대체로 간다.
  - `codex` 실행 파일 찾기: PATH에서 `codex.cmd`/`codex.exe`. `.cmd`면 `cmd.exe /d /c codex …`로 실행한다.
- **rollout 로그 대체**
  - `sessions\**\rollout-*.jsonl` 중 수정 시각이 최신인 파일부터, 끝에서부터 `token_count` 이벤트를 찾는다.
  - `limit_id`가 `"codex"`이거나 없는 것만 받는다.
  - 형식 A–D를 모두 지원한다(보고서 §4.3).
  - `resets_at`이 이미 지났으면 0%로 본다.
  - 상태는 `Ok`로 두고, 메시지에 "로그 기준 · hh:mm"을 넣는다.
- **라벨**: 창 길이(분)가 ±5% 안에서 300이면 `5h`, 1440이면 `일`, 10080이면 `주`, 43200이면 `월`이다. **primary/secondary 순서로 정하지 않는다.**
- **플랜 라벨**: `plan_type` 원문을 대문자로 시작하게 바꿔 쓴다(`Pro`, `Plus`, `Team` 등).
- **401 처리**: `auth.json`을 다시 읽어 토큰이 바뀌었으면 한 번만 재시도한다. **`/oauth/token`은 절대 호출하지 않는다.**
- **간격**: `MinInterval = 1분`.

### 5.3 Gemini CLI (`gemini-usage-api.md`)
- **경로와 인증 방식**
  - 홈은 `GEMINI_CLI_HOME`, 없으면 `%USERPROFILE%`이고, 디렉터리는 `<home>\.gemini`다.
  - `settings.json`은 JSONC다(주석 건너뛰기, 끝 쉼표 허용). `security.auth.selectedType`을 읽고, 없으면 최상위 `selectedAuthType`을 읽는다. 값이 `gemini-api-key`, `api-key`, `vertex-ai`, `gateway`이면 `Unsupported`, "API 키/Vertex — 사용량 조회 불가"다.
- **자격 증명 출처** (순서대로)
  1. `oauth_creds.json` (`access_token`, `refresh_token`, `expiry_date`(ms, double), `id_token`).
  2. Credential Manager의 `gemini-cli-oauth/main-account` (`CRED_TYPE_GENERIC`, UTF-8 JSON의 `token.{accessToken, refreshToken, expiresAt}`).
  3. 둘 다 없으면 `NotInstalled`.
- **토큰 갱신**: `expiry <= now + 5분`이면 공개 설치형 클라이언트로 `POST https://oauth2.googleapis.com/token`을 보낸다(form-encoded).
  - client id와 secret은 gemini-cli 소스(`packages/core/src/code_assist/oauth2.ts`)의 `OAUTH_CLIENT_ID`/`OAUTH_CLIENT_SECRET`이다. **저장소·소스에 넣지 않는다**(GitHub Push Protection이 비밀로 차단). 실행할 때 `GeminiOAuthClientLocator`가 다음 순서로 찾는다.
    1. 환경 변수 `GEMINI_OAUTH_CLIENT_ID`와 `GEMINI_OAUTH_CLIENT_SECRET`.
    2. PATH의 `gemini` 옆 `node_modules\@google\gemini-cli`(및 `gemini-cli-core`).
    3. `%APPDATA%\npm\node_modules\@google\gemini-cli`(및 `gemini-cli-core`).
    - 각 패키지에서 `bundle\*.js`와 `dist\src\code_assist\oauth2.js`를 정규식으로 읽는다.
    - 찾지 못하면 `Error`, "gemini-cli 설치를 찾지 못해 로그인을 갱신할 수 없음"이다. 토큰이 아직 유효하면 클라이언트가 필요 없다.
  - **결과는 메모리에만 둔다.** 구글 refresh token은 갱신해도 바뀌지 않아 안전하다.
  - `invalid_grant`이면 `SignedOut`, "터미널에서 gemini 실행해 다시 로그인"이다.
- **`loadCodeAssist`**: `POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`, 본문 `{"metadata":{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}`. 결과는 1시간 캐시한다.
  - 프로젝트: `cloudaicompanionProject`(문자열이거나 `{id}`/`{projectId}` 객체) → 환경 변수 `GOOGLE_CLOUD_PROJECT`/`GOOGLE_CLOUD_PROJECT_ID` → `~/.gemini/.env` → `~/.env`.
  - **`onboardUser`는 절대 호출하지 않는다.**
  - 개인 계정 종료 판정: `currentTier`가 없고, `ineligibleTiers[].reasonCode`나 `reasonMessage`에 `UNSUPPORTED_CLIENT`가 있고, `paidTier.name`도 없고, id_token에 `hd`도 없을 때. 이때 `Unsupported`, "개인 계정 지원 종료 (2026-06-18) — Antigravity 사용"이다.
  - `currentTier`는 있는데 프로젝트가 없으면 `Error`, "GOOGLE_CLOUD_PROJECT 설정 필요"다.
- **`retrieveUserQuota`**: `POST …:retrieveUserQuota`, 본문 `{"project":"<id>"}`.
  - 403 응답의 `details[].reason == "SUBSCRIPTION_REQUIRED"`는 위 종료 판정 규칙에 맞춰 처리한다.
  - 버킷은 `modelId` 소문자에 대해 `flash-lite` → `flash` → `pro` 순서의 부분 문자열로 묶는다. 묶음마다 최소 `remainingFraction`을 고르고, 사용 % = `(1 − f) × 100`이다.
  - 미터는 `pro`(Pro, 일간), `flash`(Fl), `flash-lite`(Lt)다. 리셋은 해당 버킷의 `resetTime`이다.
- **플랜 라벨**: `paidTier.name`이 있으면 그것을 쓴다. 없으면 `standard-tier`는 `Standard`, `free-tier` + `hd`는 `Workspace`, `free-tier`는 `Free`다.
- **401 처리**: 한 번 갱신하고 재시도한다. 그래도 401이면 `SignedOut`이다.
- **간격**: `MinInterval = 2분`.

### 5.4 GitHub Copilot (`copilot-usage-api.md`)
- **토큰 후보** (순서대로 실제 호출로 검증하고, 성공한 출처를 기억한다)
  1. 앱이 저장한 기기 코드 로그인 토큰 (`copilot-token.bin`, DPAPI).
  2. 환경 변수 `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`. 접두사가 `gho_`, `ghu_`, `github_pat_`인 것만 받고 `ghp_`는 거른다.
  3. `gh auth token --hostname github.com`. 창 없이 실행하고 타임아웃은 5초이며, 자식 프로세스 환경에서 `GH_TOKEN`/`GITHUB_TOKEN`을 지운다.
  4. Credential Manager `CredEnumerate("copilot-cli*")`. blob은 UTF-8 토큰이다.
  5. `%LOCALAPPDATA%\github-copilot\apps.json`, `hosts.json`. 키에 `github.com`이 들어 있는 항목의 `oauth_token`을 쓴다.
  6. 모두 실패하면 `SignedOut`, "설정에서 GitHub 로그인"이다.
- **요청**: `GET https://api.github.com/copilot_internal/user`. 헤더는 `Authorization: Bearer <token>`, `Accept: application/json`, `User-Agent: AITokenUsage/<ver>`(**없으면 GitHub가 거부한다**), `X-GitHub-Api-Version: 2025-04-01`이다.
  - 401/403/404면 다음 후보 토큰으로 넘어간다. 모든 후보가 실패하면 `SignedOut`이다.
  - `Retry-After`가 붙은 비정상 응답, 429, `x-ratelimit-remaining: 0`인 403은 `RateLimited`다.
- **파싱**: `quota_snapshots`는 항목마다 따로 파싱한다.
  - 주 미터는 Free(`access_type_sku == free_limited_copilot`)면 `chat ?? premium_interactions`, 그 외에는 `premium_interactions`다.
  - 라벨은 `token_based_billing == true`면 "크레딧", 아니면 "프리미엄 요청"이다.
  - 사용 % = `100 − clamp(percent_remaining, 0, 100)`.
  - `!unlimited && entitlement == 0`(Business 과금형 등)이면 미터를 건너뛴다. 대신 `credits_used`가 있으면 `TextOverride = "N 크레딧 사용"`인 텍스트 줄로 보여준다.
  - `unlimited`면 "무제한"이다.
  - 2줄은 리셋 날짜 텍스트 줄(`↻ 11/1`)이다. Free 플랜이면 `completions` 미터도 고를 수 있다.
- **리셋 시각**: `snapshot.quota_reset_at`(unix 초) → `quota_reset_date_utc` → `quota_reset_date`(ISO 우선, 실패하면 `yyyy-MM-dd` UTC 자정) → `limited_user_reset_date` → 다음 달 1일 00:00 UTC.
- **플랜 라벨**
  - `access_type_sku`가 `free_limited_copilot`이면 `Free`, `free_educational_quota`이면 `Student`다.
  - 그 외에는 `copilot_plan`으로 정한다: `individual` Pro, `individual_pro` Pro+, `individual_max` Max, `individual_edu` Student, `business` Business, `enterprise` Enterprise.
- **기기 코드 로그인 (`GitHubDeviceFlow`)**
  - client `Iv1.b507a08c87ecfe98`, scope `read:user`.
  - `POST https://github.com/login/device/code`로 코드를 받고, `POST https://github.com/login/oauth/access_token`를 `interval`초마다 폴링한다. `slow_down`이면 간격을 5초 늘리고, `expired_token`이면 처음부터 다시 시작하고, `access_denied`이면 취소한다.
  - 모두 `Accept: application/json`이다. 오류가 HTTP 200 본문에 실려 오니 `error` 필드를 먼저 확인한다.
  - 성공하면 DPAPI로 저장하고, `GET https://api.github.com/user`로 `login`을 받아 표시한다.
- **간격**: `MinInterval = 5분`. 기본 설정 간격은 5분이다.

---

## 6. 오류 처리 원칙
- **네트워크 실패**: 마지막 정상 값을 흐리게 보여준다. 상세 카드에 이유와 다음 재시도 시각을 표시한다.
- **알 수 없는 JSON 필드**: 무시한다. 필드가 없으면 그 미터만 빠진다.
- **어떤 예외도 위젯을 죽이지 않는다.** 공급자 루프는 예외를 잡아 `Error` 상태로 바꾼다. UI의 미처리 예외는 `DispatcherUnhandledException`에서 로그를 남기고 계속 실행한다.
- **사용자 메시지는 한국어**로 쓴다. 로그는 영어로 쓰되 비밀 정보는 넣지 않는다.

---

## 7. 테스트 전략

### 7.1 단위 테스트 (`tests/AITokenUsage.Core.Tests`, xUnit)

| 대상 | 샘플·사례 |
|---|---|
| ClaudeUsageParser | 2026-10-28 실제 응답 형태(값은 가공): `limits` 있음 / `limits: null`(레거시 키만) / `limits: []` / scoped 항목 / 잘못된 항목 섞임 / `extra_usage` 켜짐 |
| ClaudeCredentialReader | 정상 / 빈 토큰 / `expiresAt` 0 / `user:profile` 없음 / 만료 임박 / 파싱 실패 후 재시도 / 환경 변수 경로 우선순위 |
| Claude 상태 코드 | 401(토큰 바뀜 → 재시도, 안 바뀜 → 대기) / 403 JSON / 403 비JSON / 429 + `Retry-After` |
| CodexUsageParser | `wham/usage`: Plus/Pro(5h+주간) / Free(30일 단독) / `additional_rate_limits` / primary가 주간인 경우 |
| CodexRolloutReader | 형식 A, B, C, D / `limit_id`가 다른 줄 건너뛰기 / 리셋이 지난 경우 / 창 누락 |
| CodexAppServerClient | 알림 줄 섞인 stdout / 오류 응답 / 타임아웃 |
| CodexWindowLabeler | 299/300/10079/10080/43200/기타 |
| Gemini | JSONC settings / API 키 모드 / creds 파일과 CredMan JSON / 갱신 응답 / 버킷 묶기와 최소값 / 개인 계정 종료 판정 / `SUBSCRIPTION_REQUIRED` / 프로젝트 없음 |
| Copilot | 유료(레거시 프리미엄 요청) / Free 레거시 필드 / Business 과금형(`credits_used`) / 무제한 / 리셋 날짜 우선순위 / 토큰 접두사 필터 / 후보 순서대로 넘어가기 |
| GitHubDeviceFlow | pending → slow_down → 성공 / expired / denied |
| BackoffPolicy, UsageService | 가짜 시계로 간격·지터·백오프·`MinInterval` / 파일 변경 트리거 / 캐시 합성 |
| RotationController | 간격, 멈춤/재개, 수동 전환, 건너뛰기, 0·1·N개 |
| TaskbarLayoutCalculator | 가운데/왼쪽 정렬 × 위치 3종 × Widgets 있음/없음 × 공간 부족 × 보조 모니터 음수 좌표 × 125% 배율 |
| ResetTimeFormatter | "3시간 12분 후 · 오늘 15:50", 날짜 넘어감, 지난 시각 |
| SettingsStore | 기본값 / 저장·복원 / 손상된 파일 / 알 수 없는 필드 |

### 7.2 실제 환경 확인
- **Claude**: 실제 API 1회 호출로 파싱 결과가 `limits[]`와 일치하는지 확인한다. 개발할 때 수동으로 한다.
- **앱 실행**: 작업 표시줄 왼쪽에 부착되는지, 2초 간격으로 줄인 상태에서 전환이 되는지, 호버 카드, 우클릭 메뉴, 설정 창의 즉시 반영을 스크린샷으로 확인한다.
- **복구**: 탐색기를 재시작한 뒤 다시 부착되는지 확인한다. 사용자 동의를 받고 수동으로 한다.
- **Codex, Gemini, Copilot**: 이 PC에는 설치돼 있지 않다. "설치 안 됨"이나 "로그인 필요" 상태 표시만 실제로 확인할 수 있다.

---

## 8. 빌드와 배포
- `dotnet build`와 `dotnet test`를 쓴다. Release 배포 명령은 다음과 같다.
  `dotnet publish src/AITokenUsage -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
  결과물은 `publish\AITokenUsage.exe`이고, .NET 10 Desktop Runtime이 필요하다.
- 앱 아이콘은 간단한 벡터 아이콘을 `.ico`로 만들어 넣는다. 브랜드 로고를 복제하지 않는다. AI별 아이콘도 단순한 기하 도형(✳ ◎ ✦ ⬡ 느낌의 Path)으로 그린다.

## 9. 위험 요소

| 위험 | 대응 |
|---|---|
| 비공식 API(OAuth usage, wham, cloudcode-pa, copilot_internal)가 예고 없이 바뀜 | 파서를 관대하게 만들고, 항목 단위로 실패를 격리하고, 레거시 키를 대체로 쓴다. 바뀌면 "Error"와 이전 값으로 버틴다 |
| 사용량 엔드포인트 429 (Claude Code와 참고 앱이 같은 버킷을 씀) | 최소 간격, 백오프, 캐시 |
| 자식 창 임베딩이 특정 빌드나 보안 프로그램에서 실패 | overlay 모드로 자동 전환하고 주기적으로 다시 시도 |
| 위젯이 UI 스레드를 막으면 작업 표시줄이 굳음 | 모든 I/O를 백그라운드에서 하고, UIA는 전용 스레드에서만 |
| CLI 토큰 손상 | 쓰기 금지, 공유 모드로 읽기, 갱신 금지(Gemini는 메모리에서만) |
| 이 PC에서 Codex, Gemini, Copilot을 실제 계정으로 확인할 수 없음 | 조사 보고서의 실제 형태 기반 샘플로 테스트하고, 실제 연결을 확인하지 못했다는 점을 결과에 명시 |
