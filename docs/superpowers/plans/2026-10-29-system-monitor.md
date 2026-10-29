# PC 사용량(CPU·메모리) 표시 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**목표**: PC 전체 CPU·메모리 사용률을 설정에서 고른 방식(끄기 / 전환 패널 / 위젯 옆 / 카드·툴팁)으로 보여 준다.

**구조**: 계산과 표시 모델은 Core의 순수 코드로 만든다(`SystemUsageSample`, `CpuUsageCalculator`, `SystemPanel`, 폭 계산). Windows 측정과 화면 연결은 App이 맡는다. 전환 패널은 가상 `ProviderView`(ID `system`)를 켜진 AI 목록 뒤에 붙여서 기존 자동 전환을 그대로 쓴다.

**기술 스택**: C#/.NET 10, WPF, xUnit 2.9.3

**설계 문서**: `docs/superpowers/specs/2026-10-29-system-monitor-design.md`(이 문서가 기준이다). v1.1 설계와 v1.0 설계도 함께 따른다.

## 전체 제약
- 빌드 경고 0. `dotnet build AITokenUsage.sln`과 `dotnet test AITokenUsage.sln`이 통과해야 한다.
- 새 사용자 문구는 리소스에 둔다.
  - Core 문구: `Strings.resx`(영어)와 `Strings.ko.resx`(한국어)
  - App 문구: `UiStrings.resx`와 `UiStrings.ko.resx`
- 테스트 기본 문화권은 ko-KR이고, 영어 테스트는 `[UseCulture("en-US")]`를 쓴다.
- 설정 키는 추가만 한다. `AppSettings.CurrentVersion`은 1을 유지한다. 기본값은 `SystemMonitorMode.Off`다.
- 실제 입력(마우스·키보드 주입)을 쓰지 않는다. 화면 확인은 PrintWindow로만 한다.
- 사용자 앱(`C:\Users\kyung\AppData\Local\Programs\AITokenUsage\AITokenUsage.exe`)을 멈췄으면 끝나고 다시 실행한다. `settings.json`, `cache.json`, HKCU Run 값 `AITokenUsage`는 백업했다가 바이트 단위로 그대로 복원한다.
- UI 스레드에서 파일·네트워크 I/O를 하지 않는다. 측정 API 호출은 허용한다(설계 §2).
- 기존 코드 스타일을 따른다: file-scoped namespace, `sealed`, XML doc 주석 밀도, `ArgumentNullException.ThrowIfNull`
- 구현 담당은 커밋하지 않는다. 커밋은 Task가 끝날 때마다 컨트롤러가 한다.

---

### Task 1: Core: 측정 모델, CPU 계산, PC 패널 모델, 설정, 폭

**Files**
- 새로 만들기
  - `src/AITokenUsage.Core/System/SystemUsageSample.cs`, `CpuUsageCalculator.cs`, `SystemPanel.cs`, `SystemMonitorMode.cs`
  - 테스트: `tests/AITokenUsage.Core.Tests/System/*Tests.cs`
  - 네임스페이스 `AITokenUsage.Core.System`이 `System` 네임스페이스와 헷갈린다면 `AITokenUsage.Core.SystemUsage`로 바꾸고, 바꿨다고 보고서에 적는다.
- 수정
  - `src/AITokenUsage.Core/Settings/AppSettings.cs`, `SettingsEnums.cs`(열거형을 그쪽에 두는 것이 관례면 그쪽에)
  - `src/AITokenUsage.Core/Taskbar/WidgetMetrics.cs`
  - `src/AITokenUsage.Core/Presentation/DetailCardModel.cs`: `string? SystemLine = null`을 마지막 매개변수로 추가
  - `src/AITokenUsage.Core/Util/TrayTextFormatter.cs`: PC 툴팁 줄
  - `Strings.resx`, `Strings.ko.resx`
  - 관련 기존 테스트

**인터페이스 (Task 2·3이 쓴다)**
```csharp
public enum SystemMonitorMode { Off, RotationPanel, AlwaysBeside, CardOnly }
public sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);
public sealed record SystemUsageSample(double? CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, DateTimeOffset At)
{ public double? MemoryPercent { get; } }
public static class CpuUsageCalculator { public static double? Percent(CpuTimes previous, CpuTimes current); }
public static class SystemPanel
{
    public const string Id = "system";
    public static string DisplayName { get; }                      // "PC"
    public static ProviderView ToView(SystemUsageSample? sample);   // Status Ok, meters "cpu"/"ram" (keys), labels CPU/RAM
    public static WidgetPanelModel BuildWidgetModel(SystemUsageSample? sample, int dotCount, int dotIndex);
    public static DetailCardModel BuildCard(SystemUsageSample? sample);
    public static string MemoryText(SystemUsageSample sample);      // "19.5 / 32 GB"
    public static string CardLine(SystemUsageSample? sample);       // ko "CPU 23% · 메모리 19.5 / 32 GB"
    public static string TooltipLine(SystemUsageSample? sample);    // "PC CPU 23% · RAM 61%"
    public static Severity SeverityFor(double? percent);            // ≥90 Critical, ≥75 Warning
}
// AppSettings: public SystemMonitorMode SystemMonitor { get; set; } = SystemMonitorMode.Off;
// WidgetMetrics: SystemGapDip = 8, SystemColumnDip = 56, WidthDip(bool showResetTime, bool systemBeside)
// TrayTextFormatter: Tooltip(...) gains an optional `string? systemLine` appended on a new line when it fits in 127 chars
```
- `ProviderView`·`UsageSnapshot`·`UsageMeter`의 실제 생성자는 Core의 Models 폴더를 읽고 맞춘다. 표본이 null이면 CPU·RAM 값은 `–`(`MeterSelector.MissingValue`), 막대는 0이다.

- [ ] **Step 1**: 설계 §5의 Core 테스트를 먼저 쓴다.
  - CPU: (idle 100→150, kernel 300→400, user 100→200) → busy = (100+100)−50 = 150, total 200 → 75.0
  - Δtotal 0 → null, 값이 역행하면 null, 100 초과는 100으로 자른다.
  - 메모리 문구: 19.5 GiB / 32 GiB → ko `19.5 / 32 GB`, en `19.5 / 32 GB`. 카드 줄은 ko `CPU 23% · 메모리 19.5 / 32 GB`, en `CPU 23% · Memory 19.5 / 32 GB`
  - 툴팁: 기존 툴팁 + `\nPC CPU 23% · RAM 61%`. 127자를 넘으면 PC 줄을 붙이지 않는다.
  - 폭: (false,false)=176, (true,false)=218, (false,true)=240, (true,true)=282
  - 설정 기본값 Off, 정의되지 않은 값 → Off, Clone, 저장 왕복, JSON에 없으면 Off
  - SystemPanel: 위젯 모델의 행 라벨, 값, 등급(74→Normal, 75→Warning, 90→Critical), 표본 null 처리
- [ ] **Step 2**: 실패를 확인한다.
- [ ] **Step 3**: 구현한다.
- [ ] **Step 4**: 빌드 경고 0과 전체 테스트 통과를 확인한다.

### Task 2: App: 측정기, 설정 UI, 전환 패널, 카드·툴팁 줄

**Files**
- 새로 만들기: `src/AITokenUsage/SystemUsageSampler.cs`(P/Invoke는 `src/AITokenUsage/Taskbar/NativeMethods.cs` 또는 앱의 기존 네이티브 파일 관례를 따른다)
- 수정
  - `AppHost.cs`: 측정기 소유, `SystemUsage`, `SystemUsageChanged`, `EnabledViewsInOrder()`에 PC 뷰 추가
  - `UI/WidgetViewModel.cs`: 현재 ID가 `system`이면 `SystemPanel.BuildWidgetModel`. 표본이 바뀌면 PC가 현재일 때만 다시 그림
  - `UI/ProviderGlyphs.cs`: `system` 아이콘과 중립색 `#8B949E`
  - `UI/DetailFlyoutController.cs`: 현재 ID가 `system`이면 `SystemPanel.BuildCard`. AlwaysBeside·CardOnly이면 AI 카드에 `SystemLine` 설정. 열린 카드는 표본이 바뀔 때 갱신
  - `UI/DetailFlyout.xaml(.cs)`: `SystemLine` 렌더링(구분선 + 한 줄)
  - `UI/TrayIconController.cs`: PC가 현재면 아이콘 숫자 = CPU %. CardOnly·AlwaysBeside면 툴팁에 PC 줄
  - `UI/Settings/WidgetPage.xaml(.cs)`: "PC 사용량(CPU·메모리)" 카드 + ComboBox(설계 §1 표)
  - `UiStrings.resx`, `UiStrings.ko.resx`

- [ ] **Step 1: `SystemUsageSampler`**
  - 2초 `DispatcherTimer`를 쓴다. `GetSystemTimes`(FILETIME 3개 → ulong)와 `GlobalMemoryStatusEx`(`MEMORYSTATUSEX`, `dwLength` 설정)를 부른다.
  - 직전 `CpuTimes`로 `CpuUsageCalculator.Percent`를 계산한다.
  - `Start()`/`Stop()`/`Dispose()`를 둔다. 실패하면(API false) 경고 로그를 한 번만 남기고 그 회차를 건너뛴다.
- [ ] **Step 2: AppHost 연결**
  - 설정이 `Off`가 아니면 시작하고, `Off`이면 멈춘다(시작 시와 `SettingsChanged`).
  - 표본마다 `SystemUsage`를 갱신하고 `SystemUsageChanged`를 낸다.
  - RotationPanel이면 `EnabledViewsInOrder()` 뒤에 `SystemPanel.ToView(SystemUsage)`를 붙인다. 표본이 갱신될 때마다 `Rotation.UpdateProviders`를 부를 필요는 없다(ID와 상태가 같으므로). 모드가 바뀔 때만 다시 계산한다.
- [ ] **Step 3: 위젯·카드·트레이**: 설계 §3.2와 §3.4대로 연결한다. AlwaysBeside의 위젯 칸 자체는 Task 3에서 만든다. 이 Task에서는 AlwaysBeside일 때 카드·툴팁 줄만 붙인다.
- [ ] **Step 4: 설정 UI**: 위젯 표시 페이지에 카드를 추가한다. 기존 `HookToggle`/`Update`/`WhileLoading` 패턴을 따르고 `AutomationProperties.Name`을 단다.
- [ ] **Step 5**: 빌드 경고 0, 전체 테스트 통과를 확인한다.
  - 실제 실행 확인: settings.json에서 `systemMonitor`를 RotationPanel로 잠시 바꾸고 위젯을 PrintWindow로 캡처해 PC 패널을 확인한다. CardOnly에서는 `--pin-detail-card`로 카드 맨 아래 PC 줄을 확인한다.
  - 끝나면 사용자 앱과 파일을 모두 복원한다.

### Task 3: App: AlwaysBeside 위젯 칸

**Files**
- 수정: `UI/WidgetWindow.xaml(.cs)`, 필요하면 `UI/ProviderPanel.xaml(.cs)` 또는 새 `UI/SystemBesidePanel.xaml(.cs)`, `Taskbar/TaskbarHost.cs`(폭 계산 호출), `UI/WidgetViewModel.cs`

- [ ] **Step 1**: 위젯 오른쪽에 구분선 + CPU/RAM 두 줄 칸(설계 §3.3)을 둔다.
  - AI 패널 전환 애니메이션(Stage 안의 PanelA/PanelB)과 분리된 고정 영역이다.
  - 값 색: 보통은 `palette.Text`, 경고·위험은 해당 색이다.
- [ ] **Step 2**: 폭은 `WidgetMetrics.WidthDip(showResetTime, mode == AlwaysBeside)`를 `TaskbarHost`와 `WidgetWindow`가 함께 쓴다. 모드가 바뀌면 다시 배치한다. 기존 ShowResetTime 변경 경로와 같다.
- [ ] **Step 3**: 표본이 바뀌면 이 칸만 다시 그린다.
- [ ] **Step 4**: 빌드 경고 0, 전체 테스트 통과를 확인한다. 실제 실행 캡처로 AlwaysBeside 위젯(282 DIP, 남은 시간 켜짐)과 남은 시간 끈 경우(240 DIP)를 확인하고, 끝나면 모두 복원한다.

## 끝으로
- 브랜치 전체를 리뷰한다.
- README에 기능 한 줄과 설정 설명을 추가한다(위젯 표시 페이지 항목).
- 사용자 수동 확인: 4가지 모드 전환, 클릭·휠로 PC 패널 넘기기, 카드와 툴팁의 PC 줄
