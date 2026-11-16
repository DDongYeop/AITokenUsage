# 추가 Claude 계정: 갱신을 Claude Code에 맡긴다 (설계)

날짜: 2026-11-16
대체하는 부분: `2026-11-02-multi-account-design.md`의 §2.3(위젯이 직접 토큰 갱신)과 Claude 계정 저장 방식(DPAPI 비밀 `claude-account-<key>`).
그 밖의 부분(계정 id, 태그, 설정 UI, Codex 계정)은 그대로다.

## 1. 왜 바꾸나

1.3.0의 추가 Claude 계정은 로그인 8시간 뒤(액세스 토큰 만료)마다 끊겼다. 사용자 PC의 로그에서 확인한 사실:

- 위젯의 갱신 요청(`POST https://platform.claude.com/v1/oauth/token`)은 첫 시도부터 510여 번 모두 실패했다.
  응답은 항상 `HTTP 429`, 본문 `error.type = rate_limit_error`, `server: cloudflare`, `retry-after` 없음.
- 같은 PC의 Claude Code 2.1.200은 같은 주소로 자기 로그인을 정상 갱신했다. 주소·본문 형식·client_id는 위젯과 같다.
- 결론: 토큰 엔드포인트가 Claude Code가 아닌 클라이언트의 갱신을 받아 주지 않는다. 헤더를 Claude Code처럼 꾸며 통과하는 방법은 쓰지 않는다.

실험(2026-11-16, Claude Code 2.1.200, 실제 개인 계정, 전용 `CLAUDE_CONFIG_DIR`):

| 명령 | 만료된 토큰 | 결과 |
|---|---|---|
| `claude auth status` | 만료 | 0.4초, 갱신 안 함 |
| `claude mcp list` | 만료 | 약 5초, 갱신함(액세스·리프레시 토큰 모두 교체, 만료 +8시간). 계정의 claude.ai 커넥터에 모두 접속한다 |
| `claude mcp list` | 유효 | 파일을 건드리지 않음 |
| `claude mcp get no-such-server` | 만료 | 1.4초, 종료 코드 1, 갱신함. 커넥터에 접속하지 않는다 |

## 2. 설계

### 2.1 저장

- 계정 폴더: `%LOCALAPPDATA%\AITokenUsage\accounts\claude-<key>`. 이 폴더가 그 계정의 `CLAUDE_CONFIG_DIR`이다.
- 폴더 안의 파일(`.credentials.json`, `.claude.json`, `backups\` 등)은 **Claude Code만 쓴다.** 위젯은 읽기만 한다
  (`.credentials.json`은 기존 `ClaudeCredentialReader`로, `FileShare.ReadWrite | Delete`). 위젯은 폴더를 통째로 옮기거나 지울 뿐,
  자격 증명 파일의 내용을 만들거나 고치지 않는다.
- DPAPI 비밀 `claude-account-<key>`는 더 쓰지 않는다. 위젯은 Claude 토큰을 어디에도 따로 저장하지 않는다.
- 저장 형태는 Claude Code의 기본 로그인(`~/.claude/.credentials.json`)과 같다(암호화 없는 파일, 사용자 프로필 안).

### 2.2 읽기와 갱신 (`ClaudeDelegatedCredentialSource`)

`IClaudeCredentialSource` 구현. `ClaudeOwnedCredentialSource`를 대체한다.

- `WatchPaths` = 계정 폴더의 `.credentials.json` (Claude Code가 갱신하거나 다시 로그인하면 바로 다시 조회).
- `ReadAsync`:
  1. 파일을 읽는다. 파일이 없거나 토큰이 없으면 `ReauthRequired`.
  2. 토큰이 유효하고 만료까지 5분 넘게 남았으면 그대로 `Ok`.
  3. 만료됐거나 5분 이내면, 계정별 잠금 안에서 다시 읽어 확인한 뒤 갱신 명령(§2.3)을 실행하고 다시 읽는다.
     - 갱신 뒤 토큰이 유효하면 `Ok`.
     - 아니고 기존 토큰이 아직 만료 전이면 그 토큰으로 `Ok`(다음 조회에서 다시 시도).
     - 아니면 `RenewFailed`. 연속 3번 실패하면 그때부터 `ReauthRequired`(다시 로그인 안내). 한 번 성공하면 횟수를 지운다.
  4. 갱신 명령은 마지막 실행 뒤 2분 안에는 다시 실행하지 않는다(그동안은 직전 결과 상태를 돌려준다).
- `RecoverAsync`(401 뒤): 파일을 다시 읽어 토큰이 바뀌었으면 그것으로 재시도, 아니면 null. 강제 갱신은 없다(Claude Code는 만료 시각이 지나야 갱신한다).
- `RenewsItself` = true. 이메일은 계정 폴더의 `.claude.json`(`oauthAccount.emailAddress`)에서 읽는다.

### 2.3 갱신 명령 (`ClaudeCliRenewer`)

- `claude mcp get aitokenusage-renew` 를 숨김 실행한다(존재하지 않는 서버 이름: 아무 서버도 띄우지 않고, 종료 코드 1이 정상).
- 환경: `CLAUDE_CONFIG_DIR=<계정 폴더>`. `CLAUDE_SECURESTORAGE_CONFIG_DIR`, `CLAUDE_CODE_OAUTH_TOKEN`, `ANTHROPIC_API_KEY`,
  `ANTHROPIC_AUTH_TOKEN`은 제거한다(다른 인증이 끼어들면 갱신하지 않는다). 작업 폴더 = 계정 폴더.
- 제한 시간 30초. 성공 여부는 종료 코드가 아니라 **명령 뒤 자격 증명 파일의 토큰이 유효해졌는지**로 판단한다.
- `mcp get` 뒤에도 만료 상태면 `claude mcp list`(제한 60초)를 한 번 더 시도한다(Claude Code 버전에 따라 경로가 달라질 때의 대비).
- `claude`를 PATH에서 찾지 못하면 실행하지 않고 실패로 본다.
- 로그에는 명령 이름, 종료 코드, 시간 초과 여부, 걸린 시간, 갱신 여부만 남긴다. 표준 출력·오류는 남기지 않는다(커넥터 이름 등이 들어 있다).

### 2.4 로그인

- 로그인 터미널과 완료 감지는 지금처럼 임시 폴더 `login\claude-<key>`에서 한다(1.3.0에서 실제로 검증된 흐름).
- 완료되면 가져오기(DPAPI 저장) 대신 **임시 폴더를 계정 폴더로 옮긴다**(같은 볼륨의 이름 바꾸기).
  - 새 계정: `login\claude-<key>` → `accounts\claude-<key>`.
  - 다시 로그인: 계정 잠금 안에서 기존 폴더를 옆으로 옮기고, 새 폴더를 제자리로 옮긴 뒤, 옛 폴더를 지운다. 중간에 실패하면 옛 폴더를 되돌린다.
  - 옮기지 못하면 지금의 `SaveFailed`와 같은 실패로 알린다.
- 다시 로그인은 지금처럼 앱 재시작 없이 적용된다(파일 감시가 다시 조회한다).

### 2.5 정리와 이전

- 시작 시 정리(`AccountDataSweeper`): 설정에 없는 `accounts\claude-<유효한 key>` 폴더를 Codex 폴더와 같은 규칙으로 지운다.
  `claude-account-<유효한 key>.bin`은 계정이 설정에 있어도 모두 지운다(더 쓰지 않는 옛 로그인).
  기존 안전 장치(설정을 기본값으로 대체했으면 건너뜀, 재분석 지점 건너뜀)는 그대로다.
- 1.3.0에서 추가한 계정은 업데이트 뒤 한 번 다시 로그인해야 한다(옛 토큰은 옮기지 않는다: 어차피 8시간 뒤 끊긴 상태다).

### 2.6 없애는 것

`ClaudeTokenRenewer`, `ClaudeOwnedCredentialSource`, `ClaudeOwnedCredentials`, `ClaudeLoginImporter`의 DPAPI 저장용 부분과 그 테스트.
`AccountStorage.ClaudeSecretName`은 정리용 접두사만 남긴다.

## 3. 알려진 한계

- Claude Code의 내부 동작(`mcp get`이 시작 때 만료 토큰을 갱신)에 기대므로, Claude Code가 바뀌면 갱신이 멈출 수 있다.
  그때는 `mcp list` 대비책이 먼저 받고, 그것도 안 되면 카드에 "다시 로그인"이 나온다. 기본 계정에는 영향이 없다.
- Claude Code가 로그인 저장을 Windows 자격 증명 관리자로 옮기면(`tengu_windows_credman`) 파일이 생기지 않아 로그인 감지가 되지 않는다. 지금과 같은 한계다.
- 갱신 명령은 계정의 claude.ai 커넥터 목록을 조회한다(접속은 하지 않는다). 추론 사용량은 쓰지 않는다.

## 4. 지켜야 할 것 (전역 제약)

- `~/.claude/.credentials.json`, `~/.codex/auth.json`, `~/.gemini/oauth_creds.json`은 절대 쓰지 않는다. 기본 Claude·Codex 로그인은 위젯이 갱신하지 않는다.
- 위젯은 Claude 토큰 엔드포인트를 직접 호출하지 않는다.
- 토큰, 응답 본문, 명령의 표준 출력·오류를 로그·캐시·화면에 남기지 않는다.
- 자격 증명 파일은 `FileShare.ReadWrite | Delete`로 읽는다.
- 빌드 경고 0, 기존 테스트 모두 통과. 문자열을 추가하면 en·ko 둘 다 넣는다(ResourceCompletenessTests).
- 주변 코드의 주석 밀도, 이름, 관용구를 따른다.
