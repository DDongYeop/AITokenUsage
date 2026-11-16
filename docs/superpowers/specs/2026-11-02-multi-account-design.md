# Claude·Codex 여러 계정 표시: 설계

- 날짜: 2026-11-02
- 브랜치: `feat/multi-account` (main `f4ecfe0`에서 분기). 커밋은 이 브랜치에만 하고, 병합·푸시는 사용자가 요청할 때 한다.
- 사용자 결정
  - 회사 계정과 개인 계정을 따로 보여 준다. 개인 계정은 claude.ai 웹·앱과 다른 컴퓨터에서 주로 쓰고, 이 PC에서는 거의 쓰지 않는다.
  - 그래서 추가 계정은 **위젯 전용 로그인**으로 한다. Claude는 앱이 토큰을 직접 보관하고 갱신한다(아래 위험 수용).
  - 같은 AI 계정이 두 개 이상 보이면, 팀 요금제인지 개인 요금제인지 표시한다.
  - Codex도 가능하면 같은 방식으로 지원한다.

## 1. 개념
- **기본 계정**: 지금의 `claude`, `codex` 패널이다. 이 PC의 CLI 로그인(`~/.claude`, `~/.codex`)을 읽기만 하고, 동작은 바뀌지 않는다.
- **추가 계정**: 사용자가 설정에서 추가한 계정이다. 패널 ID는 `<종류>#<키>`이고, 키는 소문자·숫자 6자다(예: `claude#k3x9q2`, `codex#a1b2c3`).
  - 종류(kind)는 `claude`, `codex`만 허용한다. Gemini·Copilot은 대상이 아니다.
  - `ProviderIds.KindOf(id)`는 `#` 앞부분을 돌려준다. 브랜드 색, 아이콘, 기본 줄, CLI 이름처럼 종류별로 고르는 곳은 모두 `KindOf`를 쓴다.
- 추가 계정은 `AppSettings.Providers`에 `ProviderSettings` 항목으로만 기록한다(별도 목록 없음).
  - `Normalize`는 형식에 맞는 계정 ID를 유지한다. 형식이 틀리거나 중복이면 버린다. 기본 4개 AI는 지금처럼 빠지면 채운다.
  - 계정 ID의 기본 줄은 종류의 기본 줄과 같다.
  - `AppSettings.CurrentVersion`은 1을 유지한다.
- 계정 추가·삭제는 **앱을 다시 시작해야 반영**된다. 공급자 목록, 폴링, 파일 감시가 시작할 때 한 번 만들어지기 때문이다. 다시 로그인하는 경우에는 재시작이 필요 없다.

## 2. Claude 추가 계정 (위젯 전용 로그인)

### 2.1 저장
- 로그인 정보는 앱의 DPAPI 저장소(`ISecretStore`, `%LOCALAPPDATA%\AITokenUsage\<이름>.bin`)에 둔다.
  - 이름은 `claude-account-<키>`다.
  - 내용은 JSON이다: `accessToken`, `refreshToken`, `expiresAt`(ms), `refreshTokenExpiresAt`(ms 또는 null), `scopes`, `subscriptionType`, `rateLimitTier`, `email`, `organizationName`
- 로그, 캐시, 화면 어디에도 토큰을 남기지 않는다. 레코드의 `ToString`에서도 토큰을 뺀다.

### 2.2 로그인과 가져오기
1. 설정 → AI 연결 → [Claude 계정 추가]를 누른다.
2. 앱이 임시 폴더 `%LOCALAPPDATA%\AITokenUsage\login\claude-<키>`를 만든다. 그리고 `cmd.exe`(시스템 폴더의 전체 경로)를 셸 실행 없이(`UseShellExecute = false`, `CreateNoWindow = false`) 직접 띄운다. 작업 폴더는 사용자 프로필이다.
   ```
   cmd.exe /c claude auth login || pause
   ```
   - 폴더 경로는 명령줄에 넣지 않고 자식 프로세스의 환경 변수로 넘긴다: `CLAUDE_SECURESTORAGE_CONFIG_DIR`은 지우고(사용자 환경에 있더라도. Claude Code는 그 변수를 먼저 본다), `CLAUDE_CONFIG_DIR=<임시 폴더>`를 넣는다. 따라서 따옴표 처리가 경로를 바꿀 수 없다(`wt.exe`를 거치면 명령줄을 다시 나누면서 따옴표가 사라진다).
   - 앱에는 콘솔이 없으므로 cmd가 자기 콘솔 창을 연다(Windows 11 기본 터미널 설정이면 Windows Terminal에서 열린다).
   - `/c … || pause`: 로그인에 성공하면 창이 닫히고, 실패하면 오류를 읽을 수 있게 창이 남는다.
   - 경로가 비었거나, 제어 문자가 있거나, 전체 경로가 아니면 터미널을 띄우지 않고 오류를 보여 준다.
   - 로그에는 "terminal opened for <종류> account login"만 남긴다. 경로와 환경 변수 값은 남기지 않는다.
3. 앱은 1초마다 `<임시 폴더>\.credentials.json`을 확인한다(최대 10분, [취소] 가능).
   - `claudeAiOauth`에 `accessToken`과 `refreshToken`이 있고, `expiresAt`가 0보다 크고, `scopes`에 `user:profile`이 있으면 완료로 본다.
   - 완료를 보면 2초 더 기다린 뒤 가져온다. Claude Code가 `.claude.json`을 마저 쓸 시간이다.
4. 가져오기
   - `.credentials.json`에서 토큰과 플랜 정보를 읽는다.
   - `<임시 폴더>\.claude.json`이 있으면 `oauthAccount.emailAddress`와 `organizationName`을 읽는다. 없으면 비워 둔다.
   - 결과를 저장소에 저장한 뒤 임시 폴더를 통째로 지운다. 지우지 못하면 로그만 남긴다(다음 시작 때 정리, §6).
5. **새 계정**이면 `Providers`에서 같은 종류의 마지막 패널 바로 뒤에 `{Id, Enabled = true, 기본 줄}`을 넣고 설정을 저장한다. 그다음 "다시 시작하면 위젯에 나타나요 [지금 다시 시작]"을 보여 준다.
6. **다시 로그인**이면 실행 중인 공급자에게 새 로그인 정보를 넘긴다. 공급자는 잠금 안에서 저장하고 메모리를 바꾼 뒤 곧바로 새로 고친다.
- 취소하거나 10분이 지나면 임시 폴더를 지우고 처음 상태로 돌아간다.
- `claude`가 PATH에 없으면 [Claude 계정 추가]를 끄고 "Claude Code가 설치되어 있어야 추가할 수 있어요"를 보여 준다. 설치 안내는 기존 연결 안내를 따른다.

### 2.3 토큰 갱신 (이 계정에만 허용)
> 1.3.1에서 대체됨: `2026-11-16-claude-account-cli-renewal-design.md`(갱신을 Claude Code에 맡기고, 로그인은 계정 폴더에 둔다).

- 기존 규칙 "Claude 토큰은 갱신하지 않는다"는 **기본 계정에는 그대로** 적용한다. 앱이 만든 로그인 체인(추가 계정)만 앱이 갱신한다. 그 체인은 다른 프로그램이 쓰지 않으므로 회전 충돌이 없다.
- 요청: `POST https://platform.claude.com/v1/oauth/token`
  - `Content-Type: application/json`, `User-Agent: claude-code/<버전>`(사용량 요청과 같은 값)
  - 본문: `{"grant_type":"refresh_token","refresh_token":…,"client_id":"9d1c250a-e61b-44d9-88ed-5944d1962f5e","scope":<저장된 scopes를 공백으로 이은 값>}`
  - 저장된 scopes가 없으면 `user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload`를 보낸다.
  - 타임아웃은 30초다(공용 HttpClient의 15초 제한이 먼저 걸릴 수 있다).
- 응답: `access_token`(필수), `refresh_token`(있으면 교체), `expires_in`(초, 필수), `refresh_token_expires_in`(초, 있으면 교체), `scope`(있으면 공백으로 나눠 교체)
  - `expiresAt`은 받은 시각 + `expires_in`이다.
  - **새 토큰은 사용량 요청을 보내기 전에 저장한다.** 저장에 실패하면 로그를 남기고 메모리의 새 토큰으로 계속한다.
- 갱신 시점
  - 만료 5분 전부터(`RefreshMargin`)
  - 사용량 요청이 401을 받았을 때 한 번. 그 요청 직전에 이미 갱신했다면 다시 하지 않는다.
- 결과 처리
  - 400·401 응답은 다시 로그인이 필요하다는 뜻이다(`invalid_grant` 등). 로그인 정보는 남겨 두고 상태를 **다시 로그인 필요**로 둔다. 이 상태에서는 사용자가 다시 로그인할 때까지 갱신을 다시 시도하지 않는다.
  - `refreshTokenExpiresAt`이 이미 지났으면 요청을 보내지 않고 바로 **다시 로그인 필요**로 둔다.
  - 429, 5xx, 네트워크 오류, 본문 오류는 일시 오류다. 토큰을 그대로 두고 다음 폴링 때 다시 시도한다.
    - 일반 조회에서 지금 액세스 토큰이 아직 만료되지 않았으면(`expiresAt` > 지금) 그 토큰으로 조회를 계속한다. 이미 만료됐으면 일시 오류로 둔다. 401 뒤의 강제 갱신에는 적용하지 않는다(거부된 토큰이므로).
- 한 계정의 갱신·조회는 공급자 잠금(`SemaphoreSlim`) 하나로 차례대로 실행한다.

### 2.4 사용량 조회
- 기본 계정과 같은 엔드포인트, 헤더, 파서, 상태 처리를 쓴다. `ClaudeUsageProvider`가 로그인 정보의 출처만 바꿔 쓴다.
  - **CLI 출처**(기본 계정): 지금과 같다. 만료가 가까우면 "Claude Code가 갱신하기를 기다리는 중"으로 두고, 401을 받으면 파일을 다시 읽는다.
  - **앱 소유 출처**(추가 계정): 만료가 가까우면 직접 갱신한다. 401을 받으면 강제로 한 번 갱신하고 다시 요청한다. 감시할 파일이 없다.
- 상태 문구
  - 다시 로그인 필요: 짧은 표시는 기존 "로그인 필요"다. 설명은 "다시 로그인하세요 · 설정 → AI 연결"이고 영어는 "Sign in again in Settings"다.
  - 일시 오류: 기존 네트워크 오류 문구를 쓴다.

## 3. Codex 추가 계정 (Codex에게 갱신을 맡김)
- 계정마다 전용 `CODEX_HOME`을 둔다: `%LOCALAPPDATA%\AITokenUsage\accounts\codex-<키>`
  - 이 폴더는 앱이 만들고 앱만 쓴다. 계정을 삭제하면 지운다(§6).
- 로그인: §2.2와 같은 방식으로 `cmd.exe /c codex login || pause`를 띄우고, 환경 변수 `CODEX_HOME=<폴더>`를 넘긴다. 경로 검사도 §2.2와 같다.
  - 새 계정의 폴더를 만들 때 `config.toml`이 없으면 `cli_auth_credentials_store = "file"` 한 줄로 만든다. 사용자의 Codex가 OS 키링에 저장하도록 설정돼 있어도 이 계정의 로그인은 `<폴더>\auth.json`에 남아야 앱이 읽을 수 있다. 이 파일은 앱 폴더에만 쓴다(`~/.codex`는 건드리지 않는다).
  - 완료 조건: `<폴더>\auth.json`이 ChatGPT 로그인으로 읽히고 `access_token`이 있다. 다시 로그인이면 시작할 때와 `access_token`이 달라야 한다.
- 조회: `CodexUsageProvider`를 그 폴더의 `CodexPaths`로 만든다.
  - 앱은 여전히 Codex 토큰을 **갱신하지 않는다.** 토큰이 오래되면 지금처럼 `codex app-server`를 띄운다. 이때 환경 변수 `CODEX_HOME=<폴더>`를 넘기므로 Codex가 그 폴더의 토큰을 직접 갱신하고 저장한다.
  - 감시 경로는 그 폴더의 `auth.json`이다.
  - 로그인 파일이 없으면 상태는 "다시 로그인 필요"이고 문구는 §2.4와 같다.
- `codex`가 PATH에 없으면 [Codex 계정 추가]를 끄고 "Codex CLI가 설치되어 있어야 추가할 수 있어요"를 보여 준다.
- 이메일은 `id_token`의 `email` 클레임에서 읽는다(설정 카드 표시용, 저장하지 않음).

## 4. 팀·개인 표시
- **요금제 구분**: `PlanCategory`는 `Team`, `Personal`, `Unknown` 중 하나다. 표시용 플랜 문자열(`ProviderView.DisplayPlan`)로 정한다.
  - 소문자로 바꾼 값에 `team`, `enterprise`, `business`, `edu` 중 하나가 들어 있거나 값이 `ent`로 시작하면 Team이다.
  - 그 밖의 값이 있으면 Personal(`max`, `pro`, `plus`, `free`, `go` 등)이다.
  - 값이 없으면 Unknown이다.
- **태그**: 같은 종류의 **켜진** 패널이 2개 이상일 때만 붙는다. 하나뿐이면 지금과 똑같이 보인다.
  - Team이면 `팀` / `Team`, Personal이면 `개인` / `Solo`이다.
  - Unknown이면 `#n`이다. n은 그 종류 패널 가운데 설정 순서상 1부터 센 번호다.
  - 같은 종류 안에서 태그가 겹치면, 두 번째부터 뒤에 ` n`(위와 같은 번호)을 붙인다. 예: `개인`, `개인 2`
- **표시 위치**
  - 위젯 패널: 이름 자리에 태그만 보인다. 아이콘이 이미 AI 종류를 알려 준다. 이름 최대 폭은 42에서 46 DIP로 넓힌다.
  - 상세 카드 제목, 트레이 툴팁, 설정 카드 이름: `Claude · 팀`
  - 설정 카드(추가 계정): 이름 아래에 이메일을 흐린 글자로 보여 준다(Claude는 저장된 이메일, Codex는 `id_token`). 기본 계정에는 "이 PC의 Claude Code" / "이 PC의 Codex"를 보여 준다. 단, 같은 종류의 패널이 2개 이상일 때만이다.
- 구현
  - `ProviderView`에 `string? AccountTag`(init, 기본 null)를 더한다. 표시 이름 규칙은 `ProviderView.Title`(`AccountTag is null ? DisplayName : $"{DisplayName} · {AccountTag}"`)와 위젯용 `ShortName`(`AccountTag ?? DisplayName`)으로 정한다.
  - 태그는 Core `AccountTags.Apply(IReadOnlyList<ProviderView> enabledInOrder)`가 계산한다. App은 화면에 넘기는 모든 뷰(`EnabledViewsInOrder`와 카드·트레이·설정이 쓰는 뷰)에 이 결과를 쓴다.

## 5. 설정 화면 (AI 연결)
- 카드 목록 아래에 "계정 추가" 구역을 둔다: [Claude 계정 추가] [Codex 계정 추가]
  - 설명: "회사·개인처럼 다른 계정을 함께 보여 줘요. 추가한 계정은 이 앱 전용으로 로그인해요."
- 로그인을 기다리는 동안 그 구역에 다음을 보여 준다.
  - "터미널에서 로그인을 마치면 자동으로 추가돼요" 진행 표시와 [취소]
  - 실패 문구: 터미널 실행 실패, 경로 오류, 시간 초과, 로그인 정보 읽기 실패
- 추가 계정 카드는 기존 `ProviderCard`를 쓴다. 연결 안내 대신 계정 동작을 둔다.
  - [다시 로그인]은 항상 보인다. "다시 로그인 필요" 상태이면 강조 버튼이 된다.
  - [계정 삭제]는 펼친 영역에 둔다. 확인 대화 상자에서 "Claude · 개인 계정을 삭제할까요? 앱이 다시 시작돼요."라고 묻는다. 확인하면 설정에서 빼고, 저장한 뒤 앱을 다시 시작한다.
- 다시 시작은 언어 변경과 같은 `LanguageBootstrap.Restart`를 쓴다.

## 6. 정리
- 앱을 시작할 때 백그라운드에서 다음을 지운다.
  - `%LOCALAPPDATA%\AITokenUsage\login\` 전체(남은 임시 로그인 폴더)
  - 설정에 없는 키의 `claude-account-<키>.bin`
  - 설정에 없는 키의 `accounts\codex-<키>\`
- 이 정리가 끝날 때까지 설정 화면의 [계정 추가]·[다시 로그인] 버튼은 꺼 둔다(`AppHost.AccountSweepCompleted`). 막 시작한 로그인의 폴더를 정리가 지우지 않게 하려는 것이다. 정리를 건너뛰면 바로 끝난 것으로 본다.
- 삭제는 앱 폴더 안의 파일만 대상으로 한다. CLI의 기본 로그인 파일(`~/.claude`, `~/.codex`)은 절대 쓰거나 지우지 않는다.
- 설치 제거기의 "설정과 캐시도 삭제"는 이미 `%LOCALAPPDATA%\AITokenUsage`를 지우므로 추가 계정도 함께 지워진다. 그 안내 문구에 "추가한 계정 로그인"을 넣는다.

## 7. 위험 (사용자 수용)
- Claude 추가 계정은 Claude Code의 공개 OAuth 클라이언트로 토큰을 갱신한다. 비공식 사용 방식이라, Anthropic이 막거나 바꾸면 추가 계정 표시가 멈출 수 있다. 기본 계정에는 영향이 없다.
- 앱을 오래 꺼 두어 갱신 토큰이 만료되면(약 2주 또는 하루, 계정마다 다름) 다시 로그인해야 한다.
- Codex 추가 계정은 Codex 프로그램이 직접 갱신하므로 이 위험이 없다. 다만 `codex`가 설치되어 있어야 오래된 토큰을 새로 받을 수 있다.

## 8. 하지 않는 것
- Gemini·Copilot 여러 계정
- 사용자가 직접 정하는 계정 이름(태그는 자동)
- 재시작 없이 계정 추가·삭제
- 같은 계정을 두 번 추가하는 것 막기
- Mac(Mac 단계에서 Keychain 저장소로 따로 다룬다)

## 9. 테스트
- **Core 단위 테스트**
  - 계정 ID: 만들기, 형식 검사, `KindOf`
  - 설정 정규화: 계정 ID 유지, 잘못된 ID·중복 버림, 기본 줄
  - Claude 앱 소유 로그인 정보: JSON 왕복, `ToString`에 토큰 없음
  - 가져오기: 완료 판정(토큰·scope·expiresAt), `.claude.json`의 이메일
  - 갱신 요청: 본문·헤더, 성공 시 교체·저장 순서, `refresh_token` 없는 응답, 400/401 → 다시 로그인 필요, 429/5xx/네트워크 → 일시 오류, 갱신 토큰 만료 → 요청 없음
  - 공급자(앱 소유): 만료 전 갱신 후 조회, 401 → 한 번 갱신 후 재시도, 다시 로그인 필요 상태 유지, 다시 로그인으로 새 토큰 적용
  - 공급자(CLI): 기존 테스트가 그대로 통과
  - Codex 계정: app-server에 `CODEX_HOME` 전달, 계정 폴더 경로, 로그인 파일 없음 → 다시 로그인 필요, 로그인 완료 판정
  - 로그인 명령: Claude·Codex의 cmd 인수와 환경 변수(지울 변수 포함), 경로가 명령줄에 들어가지 않음, 빈 경로·제어 문자·전체 경로가 아닌 경로 거부
  - 태그: 2개 이상일 때만, Team/Personal/Unknown, 겹침 번호, `Title`/`ShortName`, 두 언어
  - 정리 대상 계산(설정에 없는 키 고르기)
- **App**
  - 빌드와 전체 테스트 통과
  - PrintWindow 캡처: 위젯 태그, 상세 카드 제목, 설정의 계정 추가 구역과 계정 카드
    - 설정에 시험용 항목 `claude#test01`을 넣어 캡처한다. 로그인 정보가 없으므로 "다시 로그인 필요" 상태와 `#2` 태그가 보인다.
    - `settings.json`은 백업했다가 그대로 복원한다. 실제 로그인이나 실제 입력은 보내지 않는다.
