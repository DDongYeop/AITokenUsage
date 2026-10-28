# AI 사용량 작업 표시줄 위젯 (AITokenUsage)

Windows 11 작업 표시줄 안에 **Claude · Codex · Gemini CLI · GitHub Copilot**의 구독 사용량(5시간·주간·일간·월간 한도 %)을 2줄 미니바로 보여 주는 작은 WPF 위젯입니다. 한 번에 AI 하나를 보여 주고 몇 초마다 다음 AI로 넘깁니다.

```
 ✳ Claude   5h ▰▰▱▱▱▱ 14%
   ● ○ ○    주 ▰▰▰▱▱▱ 50%
```

## 기능

- **작업 표시줄 위젯**: 약 176×40 DIP, 배경 투명. 작업 표시줄의 자식 창으로 붙고, 실패하면 작업 표시줄이 소유하는 창(overlay)으로 대신 표시합니다.
- **2줄 미니바**: AI마다 표시할 두 한도를 고를 수 있습니다(예: Claude 5시간 세션 / 주간, Codex 5시간 / 주간, Gemini Pro / Flash, Copilot 월간 / 리셋 날짜). 75% 이상은 주황, 90% 이상은 빨강(공급자가 등급을 주면 그 값)입니다.
- **자동 전환**: 2–60초 간격(기본 5초), 220ms 슬라이드. 왼쪽 클릭·휠로 수동 전환, 마우스를 올리면 멈춤. 연결 안 된 AI는 건너뛸 수 있습니다.
- **상세 카드**: 위젯에 마우스를 0.4초 올리면 한도별 넓은 바, 리셋 시각(`3시간 12분 후 · 오늘 15:50`), 플랜, Claude 추가 사용량, 마지막 갱신·재시도 시각을 보여 줍니다.
- **설정 창**: 표시할 AI·순서·줄 구성, 전환 간격, 모니터, 위치(작업 표시줄 왼쪽 끝 / 시작 버튼 왼쪽 / 알림 영역 왼쪽), 사용한 %·남은 %, 글자색, 새로고침 간격(2/5/10/15분), Windows 시작 시 실행, 트레이 아이콘. 바꾸면 바로 적용·저장됩니다.
- **트레이 아이콘**: 현재 AI의 1줄 값을 숫자로 그립니다. 왼쪽 클릭은 설정 창, 오른쪽 클릭은 `설정…` · `지금 새로고침` · `자동 전환 일시정지` · `종료`.
- **견고함**: 마지막 정상 값을 캐시해 시작 즉시 보여 주고, 조회가 실패하면 흐리게 표시합니다. 요청 제한(429)이면 지수 백오프로 기다립니다.

## 요구 사항

- Windows 11 24H2 또는 25H2, x64, **가로(아래쪽) 작업 표시줄**
- 실행: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- 빌드: .NET 10 SDK
- 보려는 AI 도구에 미리 로그인해 두어야 합니다.
  - Claude: Claude Code (`claude` 실행 후 `/login`, Pro/Max/Team/Enterprise 구독 로그인)
  - Codex: Codex CLI (`codex login`, ChatGPT 계정 로그인)
  - Gemini: Gemini CLI (Google 계정 로그인, Code Assist 라이선스)
  - Copilot: `gh auth login`, Copilot CLI, VS Code 계열 Copilot 중 하나 — 또는 설정 창의 **GitHub 로그인**

관리자 권한은 필요 없습니다.

## 빌드와 실행

저장소 루트에서:

```powershell
dotnet build AITokenUsage.sln
dotnet test AITokenUsage.sln
dotnet run --project src/AITokenUsage
```

창 없이 시작해 작업 표시줄에 위젯이, 알림 영역에 트레이 아이콘이 나타납니다. 이미 실행 중일 때 다시 실행하면 새로 뜨지 않고 설정 창을 엽니다.

## 배포

```powershell
dotnet publish src/AITokenUsage -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

결과물은 `publish\AITokenUsage.exe` 한 파일입니다(.pdb는 디버깅용). 실행하는 PC에 .NET 10 Desktop Runtime이 있어야 합니다. 원하는 폴더에 복사해 실행하고, 설정 창에서 "Windows 시작 시 자동 실행"을 켜면 로그인할 때 자동으로 시작합니다.

앱 아이콘을 다시 만들려면:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/New-AppIcon.ps1
```

## 데이터 출처

| AI | 읽는 자격 증명 (읽기 전용) | 호출하는 곳 |
|---|---|---|
| Claude | 폴더 안의 `.credentials.json`. 폴더는 `CLAUDE_SECURESTORAGE_CONFIG_DIR`가 정의돼 있으면 그 값(빈 값이면 `%USERPROFILE%\.claude`), 없으면 `CLAUDE_CONFIG_DIR`, 둘 다 없으면 `%USERPROFILE%\.claude` | `GET https://api.anthropic.com/api/oauth/usage` |
| Codex | `%USERPROFILE%\.codex\auth.json` (`CODEX_HOME`) | `GET https://chatgpt.com/backend-api/wham/usage`, 토큰이 오래되면 `codex app-server`(15분에 1회), 마지막 대체로 `sessions\**\rollout-*.jsonl` 로그 |
| Gemini | `%USERPROFILE%\.gemini\oauth_creds.json` 또는 Windows 자격 증명 관리자 `gemini-cli-oauth/main-account` | `POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`, `…:retrieveUserQuota` |
| Copilot | 앱의 GitHub 로그인 토큰, `COPILOT_GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_TOKEN`, `gh auth token`, 자격 증명 관리자 `copilot-cli*`, `%LOCALAPPDATA%\github-copilot\apps.json`·`hosts.json` | `GET https://api.github.com/copilot_internal/user` |

### 비공식 API 주의

위 사용량 엔드포인트는 모두 **공개 문서가 없는 비공식 API**입니다. 각 CLI·IDE가 내부적으로 쓰는 것을 같은 방식으로 호출할 뿐이며, 예고 없이 바뀌거나 막힐 수 있습니다. 바뀌면 위젯은 "오류"와 마지막 정상 값으로 버티고, 파서를 고쳐야 다시 정상 표시됩니다. Copilot의 GitHub 로그인은 Copilot GitHub App의 공개 클라이언트 ID로 기기 코드 흐름을 씁니다. 사용 여부는 각 서비스 약관을 확인하고 본인 판단으로 결정하세요.

Claude의 사용량 API는 Claude Code와 같은 요청 한도를 공유하므로, 새로고침 간격을 너무 짧게 하지 않는 것이 좋습니다(최소 2분, 기본 5분).

### Gemini OAuth 클라이언트

Gemini 토큰을 갱신하려면 Gemini CLI의 Google OAuth 클라이언트가 필요합니다. 이 저장소에는 Google OAuth 클라이언트 ID·시크릿을 **포함하지 않습니다.** 앱은 실행할 때 PC에 설치된 gemini-cli(`%APPDATA%\npm\node_modules\@google\gemini-cli` 등의 번들·`oauth2.js`)에서 클라이언트 값을 찾아 메모리에서만 쓰며, 찾지 못하면 토큰 갱신을 하지 않습니다. 환경 변수 `GEMINI_OAUTH_CLIENT_ID`/`GEMINI_OAUTH_CLIENT_SECRET`를 둘 다 지정하면 그 값을 우선 씁니다. 이 값은 파일이나 로그에 남기지 않습니다.

## 보안과 개인정보

- CLI의 자격 증명 파일에는 **절대 쓰지 않습니다.** 다른 프로그램이 쓰는 중에도 공유 모드로 읽기만 합니다.
- Claude·Codex 토큰을 직접 갱신(refresh)하지 않습니다. 만료가 가까우면 CLI가 갱신할 때까지 기다립니다. Gemini만 메모리 안에서 갱신하고 파일에 쓰지 않습니다.
- 토큰, `Authorization` 헤더, API 응답 원문은 로그·캐시·오류 메시지에 남기지 않습니다.
- 앱이 직접 받은 Copilot 토큰은 DPAPI(현재 사용자)로 암호화해 저장하고, 설정 창의 [로그아웃]으로 지웁니다.

## 저장 위치

| 파일 | 내용 |
|---|---|
| `%APPDATA%\AITokenUsage\settings.json` | 설정 |
| `%LOCALAPPDATA%\AITokenUsage\cache.json` | 공급자별 마지막 정상 사용량(토큰 없음) |
| `%LOCALAPPDATA%\AITokenUsage\copilot-token.bin` | GitHub 로그인 토큰(DPAPI) |
| `%LOCALAPPDATA%\AITokenUsage\logs\app.log` | 로그(1MB마다 교체, 3개 보존) |

## 알려진 한계

- **Codex, Gemini CLI, Copilot은 실제 계정으로 검증하지 못했습니다.** 개발 PC에는 Claude Code만 로그인돼 있어, 세 공급자는 조사 보고서의 실제 응답 형태로 만든 샘플 데이터 단위 테스트로만 확인했습니다. 실제 계정에서 "설치 안 됨"·"로그인 필요" 외의 표시가 이상하면 이슈로 알려 주세요.
- **Antigravity는 지원하지 않습니다.** Gemini 개인 계정의 Code Assist 지원 종료(2026-06-18) 이후 개인 계정은 "개인 계정 지원 종료 — Antigravity 사용"으로만 표시됩니다.
- **세로(왼쪽/오른쪽) 작업 표시줄은 지원하지 않습니다.** 이때와 작업 표시줄에 자리가 부족할 때는 위젯을 숨기고 트레이 아이콘만 남깁니다.
- 위젯은 모니터 한 개에만 표시합니다(설정에서 고를 수 있음).
- Copilot 언어 서버의 `auth.db`, Gemini의 암호화 파일 저장소(`gemini-credentials.json`)는 읽지 않습니다.
- 자식 창 임베딩은 Windows 빌드나 보안 프로그램에 따라 실패할 수 있습니다. 그때는 overlay 모드로 표시하고 10초마다 다시 붙기를 시도합니다.

## 문제 해결

- **위젯이 안 보임**: 세로 작업 표시줄, 공간 부족, 전체 화면 앱, 작업 표시줄 자동 숨김일 수 있습니다. 트레이 아이콘 왼쪽 클릭으로 설정 창을 열어 위치·모니터를 바꿔 보세요.
- **값이 흐리게 보임**: 마지막 조회가 실패해 이전 값을 보여 주는 중입니다. 위젯에 마우스를 올려 상세 카드 아래쪽의 이유와 재시도 시각을 확인하세요.
- **로그**: `%LOCALAPPDATA%\AITokenUsage\logs\app.log`
