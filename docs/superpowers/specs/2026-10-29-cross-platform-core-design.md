# Core 크로스플랫폼화 (Mac 대응 1단계): 설계

- 날짜: 2026-10-29
- 상태: 구현 전에 쓴 계획이다. 실제 macOS 지원은 1.4.0에서 `feature/macos-support` 브랜치로 들어갔고, 세부는 이 문서와 다를 수 있다.
- 브랜치: `feat/cross-platform-core` (`main`에서 분기)
- 기준 문서: v1.0 설계 `2026-10-28-ai-usage-taskbar-widget-design.md`, v1.1 설계 `2026-10-29-widget-v1.1-design.md`
- 사용자 결정
  - Mac을 지원한다. 사용자는 Mac을 갖고 있고, Apple 개발자 계정은 아직 없다(나중에 만들 수 있음).
  - Mac 앱의 UI 방식(Avalonia 또는 Swift)은 시험해 본 뒤 정한다.
  - Mac 쪽 빌드·실행·화면 확인은 Mac에서 실행하는 Claude Code가 맡는다. 이 문서의 작업(1단계)은 Windows에서 한다.
  - 메뉴 막대 표시는 "아이콘 + 짧은 글자" 형태로 한다(2·3단계).

## 1. 전체 단계와 이 문서의 범위

| 단계 | 어디서 | 내용 |
|---|---|---|
| **1. Core 크로스플랫폼화 (이 문서)** | Windows | Core와 테스트를 `net10.0`으로 바꾸고, OS별로 다른 동작을 플랫폼 분기 뒤로 정리한다. CI에서 Windows와 macOS 양쪽으로 테스트한다 |
| 2. Mac 시험 | Mac | Avalonia 메뉴 막대(아이콘 + 글자, 팝오버) 시험, Keychain 읽기, Mac에서의 CLI 위치 조사. 결과로 UI 방식을 정한다 |
| 3. Mac 앱 | Mac | 메뉴 막대 앱, 설정, `.dmg` 배포(서명 없이 시작, 계정이 생기면 공증 추가) |

**이 문서에서 하지 않는 것**
- Mac UI
- macOS Keychain 구현. 인터페이스와 호출 자리만 만들고, 구현은 Mac에서 확인하며 2단계에서 한다
- Mac 패키징
- Windows 앱 동작 변경. Windows 사용자에게 보이는 차이는 없어야 한다

## 2. 프로젝트 구조

- `src/AITokenUsage.Core`: `net10.0-windows`에서 `net10.0`으로 바꾼다.
  - Windows 전용 구현(`DpapiSecretStore`, `WindowsCredentialStore`, `ClaudeVersionProbe`의 실행 중 `claude.exe` 조회 등)은 Core에 두고 `[SupportedOSPlatform("windows")]`를 붙인다.
  - 플랫폼 호환성 분석기(CA1416)가 OS 확인 없는 호출을 경고로 잡는다. 빌드 경고 0 규칙이 그대로 적용된다.
- `tests/AITokenUsage.Core.Tests`: `net10.0`으로 바꾼다.
- `src/AITokenUsage`(WPF 앱): `net10.0-windows` 그대로 둔다. Core 참조도 그대로다.

## 3. 플랫폼 정보

- `IEnvironment`에 `PlatformKind Platform { get; }`를 추가한다(`Windows`, `MacOS`, `Other`).
  - `RealEnvironment`는 `OperatingSystem.IsWindows()`, `OperatingSystem.IsMacOS()`로 정한다.
  - 테스트용 `FakeEnvironment`는 값을 지정할 수 있다. 그래서 Mac 동작 테스트를 Windows에서도 돌릴 수 있다.
- 공급자 코드는 `OperatingSystem.Is*()`를 직접 부르지 않고 `ctx.Env.Platform`으로 분기한다. 단, Windows 전용 API를 부르는 지점은 분석기를 만족시키려고 `OperatingSystem.IsWindows()` 확인을 함께 둔다.

## 4. OS마다 달라지는 동작

### 4.1 실행 파일 찾기와 자식 프로세스의 PATH
- **문제**: macOS에서 Finder나 로그인 항목으로 실행한 GUI 앱의 PATH는 `/usr/bin:/bin:/usr/sbin:/sbin`뿐이다. Homebrew나 npm으로 설치한 `codex`, `gemini`, `gh`, `node`를 찾지 못한다. npm 전역 명령은 `#!/usr/bin/env node`로 시작하는 스크립트라서, 찾더라도 자식 프로세스의 PATH에 `node`가 없으면 실행되지 않는다.
- **설계**: `ToolSearchPath`(Core, 순수 함수) 추가
  - `IReadOnlyList<string> Directories(PlatformKind platform, string? pathVariable, string userProfile)`
    - Windows: PATH를 `;`로 나눈 목록(지금과 같음)
    - macOS: PATH를 `:`로 나눈 목록 뒤에 다음을 덧붙인다(중복 제거).
      - `/opt/homebrew/bin`, `/usr/local/bin`
      - `~/.local/bin`, `~/.npm-global/bin`, `~/.volta/bin`, `~/.bun/bin`
      - nvm: `~/.nvm/versions/node/*/bin` 중 버전이 가장 높은 것
  - `string ChildPathVariable(...)`: 자식 프로세스에 넘길 PATH(위 목록을 `:`로 이은 것)
- `RealProcessRunner.FindOnPath`
  - Windows: 지금 그대로(PATHEXT 사용)
  - macOS·기타: `ToolSearchPath.Directories`를 쓰고, 확장자 없이 찾으며, 실행 권한이 있는 파일만 인정한다.
- `RealProcessRunner`가 자식 프로세스를 시작할 때, macOS에서는 `ProcessSpec`에 PATH가 따로 지정되지 않았으면 `ChildPathVariable`을 넣는다.

### 4.2 Codex app-server 실행
- `CodexAppServerClient.BuildSpec(string codexPath, PlatformKind platform)`
  - Windows: 지금 그대로(`.exe`/`.com`이면 직접 실행, 그 외에는 `cmd.exe /d /c codex …`)
  - macOS·기타: 찾은 경로를 직접 실행한다(`codexPath -s read-only -a never app-server`).
- 인증 파일 `~/.codex/auth.json`과 rollout 로그 경로는 두 OS에서 같다. 바꾸지 않는다.

### 4.3 Claude
- **로그인 정보**
  - Windows: `~/.claude/.credentials.json`(지금과 같음)
  - macOS: Claude Code는 로그인 정보를 Keychain에 저장한다. 서비스 이름은 2단계에서 실제로 확인한다(조사 문서상 후보는 `Claude Code-credentials`).
  - 1단계 설계: `ClaudeCredentialReader`가 macOS일 때 먼저 `ctx.Credentials.ReadGeneric(ClaudePaths.KeychainService)`를 읽는다. 값이 없으면 파일을 읽는다(`CLAUDE_CONFIG_DIR` 등 기존 규칙 그대로).
  - Keychain 값의 JSON 형식은 파일과 같다고 가정하고 같은 파서를 쓴다. 2단계에서 확인한다.
  - 쓰기와 토큰 갱신을 하지 않는다는 원칙은 Keychain에도 그대로 적용된다.
- **버전(User-Agent)**
  - Windows: 실행 중인 `claude.exe`의 파일 버전 → VS Code 확장 폴더 → 기본값(지금과 같음)
  - macOS: 실행 중 프로세스 조회는 하지 않는다. VS Code 확장 폴더(`~/.vscode/extensions`, 두 OS 공통) → 기본값 순서다. 2단계에서 Claude Code 설치 위치의 버전 정보를 조사해 추가한다.

### 4.4 Gemini CLI
- `~/.gemini/` 파일 경로는 두 OS에서 같다.
- **Keychain 모드**: `GeminiCredentialReader`는 지금도 `ICredentialStore.ReadGeneric("gemini-cli-oauth/main-account")`를 쓴다. 인터페이스가 같으므로 macOS 구현이 생기면 그대로 동작한다.
- **OAuth 클라이언트 찾기**(`GeminiOAuthClientLocator`, npm 전역 `node_modules` 후보)
  - Windows: `%APPDATA%\npm\node_modules`(지금과 같음)
  - macOS
    - `gemini` 실행 파일 위치에서 `../lib/node_modules`
    - `/opt/homebrew/lib/node_modules`, `/usr/local/lib/node_modules`
    - `~/.npm-global/lib/node_modules`
    - nvm 최신 버전의 `lib/node_modules`
  - 환경 변수 우선(`GEMINI_OAUTH_CLIENT_ID`/`SECRET`) 규칙은 그대로다. 클라이언트 값 리터럴을 저장소에 넣지 않는다는 규칙도 그대로다.

### 4.5 Copilot
- `gh auth token`: 두 OS 공통이다(4.1의 PATH 확장으로 `gh`를 찾는다).
- Copilot CLI 자격 증명
  - Windows: Credential Manager의 `copilot-cli*` 열거(지금과 같음)
  - macOS: 1단계에서는 건너뛴다. 2단계에서 Keychain 저장 방식을 조사한다.
- 언어 서버 파일(`apps.json`, `hosts.json`)
  - Windows: `%LOCALAPPDATA%\github-copilot`(지금과 같음)
  - macOS: `~/.config/github-copilot`
- 앱이 저장하는 기기 코드 로그인 토큰은 `ISecretStore`를 그대로 쓴다. Windows는 DPAPI이고, macOS 구현(Keychain)은 2단계에서 만든다.

### 4.6 연결 안내와 터미널 열기
- `ConnectionGuideBuilder.Build(providerId, status, statusMessage, PlatformKind platform)`
  - Node.js 설치 단계의 명령: Windows `winget install OpenJS.NodeJS.LTS`, macOS `brew install node`
  - 나머지 명령(`npm install -g …`, `codex login`, `claude`, `gemini`)은 같다.
  - 리소스 문구는 그대로 쓴다. "Node.js 설치 (이미 있으면 건너뛰세요)"는 OS와 무관하다.
- `TerminalCommandLine.Build(PlatformKind platform, bool hasWindowsTerminal, string command)`
  - Windows: 지금 그대로
  - macOS: `("osascript", ["-e", "tell application \"Terminal\" to do script \"<command>\"", "-e", "tell application \"Terminal\" to activate"])`
    - `<command>` 안의 `\`와 `"`는 AppleScript 문자열 규칙으로 이스케이프한다. 명령은 고정 상수이지만 이스케이프 함수는 단위 테스트한다.

### 4.7 그 밖의 정리
- `StartupSync`, `AppSettings`의 주석에 있는 Windows 경로 표현은 "Windows에서는 …"으로 고친다. 동작은 바꾸지 않는다.
- 앱 데이터 경로(`AppPaths`)는 App 프로젝트 소관이라 이번에 바꾸지 않는다. Mac 앱은 3단계에서 `~/Library/Application Support/AITokenUsage`, `~/Library/Caches/AITokenUsage`, `~/Library/Logs/AITokenUsage`를 쓴다.

## 5. 테스트

- **Windows 전용 테스트**: `[WindowsFact]`/`[WindowsTheory]` 속성(xUnit `FactAttribute`를 상속해, Windows가 아니면 생성자에서 `Skip`을 설정)으로 표시한다. 대상은 DPAPI, Credential Manager, `claude.exe` 조회, `cmd.exe` 실행 등이다.
- **경로 표기**: 테스트에 하드코딩된 `\` 경로와 `C:\` 리터럴은 `Path.Combine`이나 플랫폼별 루트로 바꾼다. macOS에서도 같은 테스트가 돌아야 한다.
- **Mac 동작 테스트**: `FakeEnvironment.Platform = MacOS`로 다음을 Windows에서 검증한다.
  - `ToolSearchPath`: 추가 디렉터리, nvm 최신 버전 선택, 중복 제거
  - `CodexAppServerClient.BuildSpec`: macOS는 직접 실행
  - `ClaudeCredentialReader`: Keychain 우선, 없으면 파일
  - `GeminiOAuthClientLocator`: macOS 후보 경로
  - Copilot 언어 서버 파일 경로
  - `ConnectionGuideBuilder`: brew 명령
  - `TerminalCommandLine`: osascript 인자와 이스케이프
- **실제 macOS 확인**: CI의 `macos-latest`에서 같은 테스트 프로젝트를 돌린다.
  - 실제 파일 시스템과 프로세스를 쓰는 테스트(`RealFileSystemTests`, `RealProcessRunnerTests` 등)도 macOS에서 통과해야 한다. Windows 전용 동작만 건너뛴다.

## 6. CI (`.github/workflows/ci.yml`)

- **트리거**: `push`와 `pull_request`
- **`windows-latest` 작업**: `dotnet build AITokenUsage.sln`(경고를 오류로: `-warnaserror`), `dotnet test AITokenUsage.sln`
- **`macos-latest` 작업**: `dotnet test tests/AITokenUsage.Core.Tests/AITokenUsage.Core.Tests.csproj`. WPF 앱은 macOS에서 빌드할 수 없으므로 솔루션 전체가 아니라 테스트 프로젝트만 대상으로 한다.
- .NET SDK는 `actions/setup-dotnet`으로 `10.0.x`를 설치한다.
- 워크플로가 실제로 동작하는지는 push해야 확인할 수 있다. push는 사용자 승인을 받고 한다.

## 7. 완료 기준
- Windows: `dotnet build AITokenUsage.sln` 경고 0, `dotnet test` 전부 통과(기존 1094개 + 새 테스트)
- Windows 설치 프로그램이 지금처럼 빌드된다(`tools/Build-Installer.ps1`).
- Windows 앱의 동작이 바뀌지 않는다. 공급자 4개가 지금과 같은 결과를 낸다.
- macOS CI에서 Core 테스트가 통과한다(Windows 전용은 건너뜀).

## 8. 위험 요소

| 위험 | 대응 |
|---|---|
| 테스트에 Windows 경로 가정이 많아 macOS에서 대량 실패 | 경로 리터럴을 정리하는 Task를 따로 두고, CI 결과로 반복 확인한다 |
| Mac의 Claude Keychain 서비스 이름이나 JSON 형식이 가정과 다름 | 1단계는 호출 자리만 만든다. 2단계에서 실제 값으로 확인하고 고친다 |
| CA1416 경고가 App 프로젝트에도 번짐 | App은 `net10.0-windows`라 Windows 전용 API 호출이 허용된다. Core만 영향을 받는다 |
| macOS GUI 앱 PATH 문제를 테스트로 다 잡을 수 없음 | `ToolSearchPath`를 순수 함수로 테스트하고, 실제 동작은 2단계에서 Mac에서 확인한다 |
