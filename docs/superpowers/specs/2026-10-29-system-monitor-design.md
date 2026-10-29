# PC 사용량(CPU·메모리) 표시: 설계

- 날짜: 2026-10-29
- 브랜치: `feat/system-monitor` (`feat/hover-card-toggle`에서 분기)
- 사용자 결정
  - 대상은 PC 전체 CPU 사용률과 메모리 사용률이다.
  - 표시 위치는 설정에서 고른다: 끄기 / 전환 패널 / 위젯 옆에 항상 / 상세 카드·툴팁에만. 기본은 끄기다.

## 1. 설정
- `AppSettings.SystemMonitor` (`SystemMonitorMode`): `Off`(기본), `RotationPanel`, `AlwaysBeside`, `CardOnly`
  - JSON에서는 다른 열거형과 같은 방식으로 저장한다(기존 `Placement` 등과 같음).
  - 정의되지 않은 값은 `Normalize`가 `Off`로 고친다. `Clone`이 복사한다. `CurrentVersion`은 1로 둔다.
- 설정 창 → 위젯 표시에 `SettingsCard` "PC 사용량(CPU·메모리)"과 ComboBox 4개 항목을 둔다.

  | 값 | 한국어 | 영어 |
  |---|---|---|
  | Off | 끄기 | Off |
  | RotationPanel | 전환 패널로 표시 | As a rotating panel |
  | AlwaysBeside | 위젯 옆에 항상 표시 | Always beside the widget |
  | CardOnly | 상세 카드·툴팁에만 | Detail card and tooltip only |

## 2. 측정
- **Core 순수 로직**
  - `SystemUsageSample(double? CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, DateTimeOffset At)`
    - `MemoryPercent` = used / total × 100(total이 0이면 null)
  - `CpuTimes(ulong Idle, ulong Kernel, ulong User)`
  - `CpuUsageCalculator.Percent(CpuTimes previous, CpuTimes current) → double?`
    - busy = (Δkernel + Δuser) − Δidle, total = Δkernel + Δuser이다(Windows의 kernel 시간에는 idle이 포함된다).
    - total이 0 이하이거나 값이 거꾸로 가면 null을 돌려준다. 결과는 0–100으로 자른다.
- **App(Windows)**: `SystemUsageSampler`
  - `GetSystemTimes`와 `GlobalMemoryStatusEx`(P/Invoke, `NativeMethods` 스타일)로 2초마다 잰다.
  - 첫 측정에는 직전 값이 없으므로 CPU가 null이다(화면에는 `–`).
  - 모드가 `Off`이면 타이머를 멈추고 측정하지 않는다. 모드가 바뀌면(SettingsChanged) 시작하거나 멈춘다.
  - 결과는 `AppHost.SystemUsage`(최근 표본)에 두고 `SystemUsageChanged` 이벤트를 UI 스레드에서 낸다.
  - 두 API는 마이크로초 단위의 시스템 호출이라 UI 스레드 `DispatcherTimer`에서 불러도 된다. 파일·네트워크 I/O는 없다.

## 3. 표시

### 3.1 공통 표시 규칙
- 패널 이름은 `PC`(두 언어 동일)이고, 줄 라벨은 `CPU`, `RAM`(두 언어 동일, 24 DIP 칸에 맞춤)이다.
- 값은 항상 "사용한 %"로 보인다. 설정의 사용한 %/남은 %는 AI 한도에만 적용한다.
- **등급**: 75% 이상 경고, 90% 이상 위험이다(`SeverityRules`와 같은 기준). 기본 막대 색은 중립 회색 `#8B949E`다. 경고·위험 색은 AI와 같다.
- **아이콘**: 단순한 모니터 모양 Path(14px)다. 브랜드 로고가 아니다.
- **메모리 표시**: `19.5 / 32 GB`(GiB 기준, 소수 첫째 자리; 전체는 반올림 정수)
  - ko: `메모리 19.5 / 32 GB`, en: `Memory 19.5 / 32 GB`

### 3.2 RotationPanel (전환 패널)
- 가상 패널 ID는 `SystemPanel.Id = "system"`이다. `ProviderIds.All`에는 넣지 않는다(설정 정규화에 영향 없음).
- 순서와 대상
  - `AppHost.EnabledViewsInOrder()`가 켜진 AI 목록 **맨 뒤**에 PC 패널용 `ProviderView`를 붙인다.
  - 이 뷰는 상태 `Ok`이고 미터는 CPU·RAM이며, `SystemPanel.ToView(sample)`로 만든다. 그래서 자동 전환, 점, 클릭·휠 전환이 그대로 동작한다.
  - "연결 안 된 AI 건너뛰기"에 걸리지 않는다(상태가 `Ok`이므로).
- **위젯 패널**: `SystemPanel.BuildWidgetModel(sample, dotCount, dotIndex)` → `WidgetPanelModel`(ProviderId `system`, 이름 `PC`, Row1 CPU, Row2 RAM). 남은 시간 열은 비운다.
- **상세 카드**: `SystemPanel.BuildCard(sample, now, zone)` → `DetailCardModel`
  - 제목: `PC`, 플랜 없음
  - 행 1: `CPU` 막대 + %
  - 행 2: `메모리` 막대 + %. 아래 문구는 `19.5 / 32 GB`(CardRow.ResetText 자리)
  - 상태 줄: `2초마다 갱신` / `Updates every 2 s`
- **트레이**: PC가 현재 패널이면 아이콘 숫자는 CPU %, 툴팁은 `PC CPU 23% · RAM 61%`다.
- 표본이 2초마다 바뀌면 PC가 현재 패널일 때만 위젯을 다시 그린다(애니메이션 없음). 카드가 열려 있으면 카드도 갱신한다.

### 3.3 AlwaysBeside (위젯 옆에 항상)
- AI 패널 오른쪽에 구분선과 두 줄짜리 칸을 붙인다.
  - 구분선: 1px, 글자색 불투명도 0.18, 위아래 여백 6
  - 칸: 줄마다 라벨(`CPU`/`RAM`, 10.5px, 흐린 색, 폭 24) + 값(11.5px SemiBold, 폭 32, 오른쪽 정렬). 값 색은 보통이면 글자색, 경고·위험이면 해당 색이다.
- **폭**: `WidgetMetrics`에 `SystemGapDip = 8`(구분선 포함), `SystemColumnDip = 56`을 추가한다.
  - `WidthDip(bool showResetTime, bool systemBeside)` = 176 + (남은 시간 ? 4 + 38 : 0) + (옆 칸 ? 8 + 56 : 0)
  - 기존 `WidthDip(bool)`은 `WidthDip(showResetTime, false)`와 같게 유지하거나 호출부를 모두 바꾼다.
- PC 칸은 자동 전환과 무관하게 항상 보이고, 2초마다 값만 갱신한다. AI 패널의 전환 애니메이션은 AI 쪽 칸에만 적용한다.
- 클릭·휠·호버는 위젯 전체에서 지금과 같다. 상세 카드는 현재 AI 카드이고, 맨 아래에 PC 줄(3.4)을 붙인다.

### 3.4 CardOnly (상세 카드·툴팁에만)
- 위젯은 그대로다.
- **상세 카드**: 현재 AI 카드 맨 아래(상태 줄 위)에 구분선과 한 줄을 붙인다: `PC  CPU 23% · 메모리 19.5 / 32 GB`
  - `DetailCardModel`에 `string? SystemLine`을 추가해서 표현한다.
  - AlwaysBeside일 때도 같은 줄을 붙인다.
- **트레이 툴팁**: 기존 툴팁 뒤에 줄을 바꿔 `PC CPU 23% · RAM 61%`를 붙인다. 127자 제한은 기존 `TrayTextFormatter` 규칙을 따른다(넘치면 PC 줄을 뺀다).

## 4. 하지 않는 것
- GPU, 디스크, 네트워크, 프로세스별 사용량
- 측정 주기 설정(2초 고정)
- Mac 측정기(Mac 앱 단계에서 같은 Core 로직에 측정기만 추가)

## 5. 테스트
- Core 단위 테스트
  - `CpuUsageCalculator`: 정상 / 0 델타 / 역행 / 100% 초과 자르기
  - `SystemUsageSample.MemoryPercent`
  - 메모리 문구: 두 언어, GB 반올림
  - `SystemPanel`: 위젯 모델, 카드 모델, 뷰, 등급 75/90, CPU null이면 `–`
  - 설정 정규화와 Clone, 저장 왕복
  - `WidgetMetrics.WidthDip`: 4가지 조합
  - 툴팁: PC 줄 추가와 127자 제한
- App
  - 실제 실행 캡처(PrintWindow): RotationPanel의 PC 패널, AlwaysBeside 위젯, CardOnly 카드
  - 실제 입력은 보내지 않는다. 카드는 DEBUG `--pin-detail-card`를 쓴다.
