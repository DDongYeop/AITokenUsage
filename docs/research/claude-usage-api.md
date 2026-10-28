# claude

# Claude Code subscription usage: implementation facts (verified 2026-10-28, independently re-verified)

## Source legend

Every claim below is tagged with one of these sources. Anything I could not confirm is marked **UNCONFIRMED**.

- **(A1) Claude Code 2.1.311.** This is the build actually running on this PC, as the VS Code extension at `%USERPROFILE%\.vscode\extensions\anthropic.claude-code-2.1.311-win32-x64\resources\native-binary\claude.exe` (build 2026-10-26). I read its embedded JavaScript.
- **(A2) Claude Code source mirror, March 2026.** `tangled.org/oppi.li/claude-code`, roughly the v2.1.8x era.
- **(A3) Official docs.** Pages on code.claude.com.
- **(B) Two independent read-only live probes,** both run 2026-10-28.
  - The first was the researcher's, with User-Agent `claude-code/2.1.200`.
  - The second was mine, with User-Agent `claude-code/2.1.311`: `C:\Users\kyung\AppData\Local\Temp\claude\d--AITokenUsage-WorkSpace-AITokenUsage\b0f14b69-2945-43cd-89fe-a14ff4f6c1e2\scratchpad\verify_probe.js`. It prints the response shape only and never the token.
  - Both probes returned the identical key set.
- **(C) Community tool source.** CodexBar (Swift) and clanker-usage/ccusage 0.4.4 (Python).
- **(D) GitHub issues** on anthropics/claude-code.

**Claude Code installs on this PC.** The account is a Team plan with a premium seat.

| Install | Location | Version |
|---|---|---|
| npm package (on PATH) | `%APPDATA%\npm` | 2.1.200 |
| Native install | `%USERPROFILE%\.local\bin\claude.exe` | 2.1.199 |
| VS Code extension | `%USERPROFILE%\.vscode\extensions\anthropic.claude-code-2.1.311-win32-x64\...` | 2.1.311 |

- Only the VS Code extension is running (7 processes), and it is what maintains `.credentials.json`.
- The 2.1.200 binary does not even contain the strings `refreshTokenExpiresAt` or `subscriptionTypeSource`.
- So `claude --version` on PATH does not identify the token owner.

---

## 0. Summary

| Question | Answer |
|---|---|
| Endpoint | `GET https://api.anthropic.com/api/oauth/usage` (A1, A2, B). |
| Required headers | `Authorization: Bearer <claudeAiOauth.accessToken>` and `anthropic-beta: oauth-2025-04-20`. Also send `User-Agent: claude-code/<version>` (see §1). |
| Utilization scale | **0–100 (percent)**. A2's type comment says "a percentage from 0 to 100". A1's schema calls `limits[].percent` "Share of the window used, 0-100". Live values: 26/51 (first probe) and 39/54 (second probe). |
| Nullability | Every top-level window key may be missing or `null`. Inside a window, `utilization` and `resets_at` are each nullable. |
| Session / weekly | `five_hour` and `seven_day` are still populated (B). `seven_day_sonnet` and `seven_day_opus` are `null`. Model-scoped weeklies (such as "Fable") appear only in `limits[]` as `kind: "weekly_scoped"`. |
| Single headline number | The `limits[]` entry with `is_active: true`. A1's schema defines it as "The server's headline pick: the row a single-value indicator shows." |
| Credentials on Windows | `%USERPROFILE%\.claude\.credentials.json` (A3). Relocated by `CLAUDE_SECURESTORAGE_CONFIG_DIR` when that is defined, otherwise by `CLAUDE_CONFIG_DIR` (A1). |
| Credentials on macOS | Keychain service `Claude Code-credentials`, plus `-<first 8 hex chars of sha256(dir)>` when a non-default directory is selected. |
| Should the widget refresh tokens itself? | **No. Treat the credentials as read-only.** Anthropic rotates refresh tokens, and a double renewal revokes the saved login for every session (A3). Current Claude Code uses a refresh lock that differs from the March source (§4). Re-read the file and let Claude Code refresh. |
| Polling | No published limit, and 429s are aggressive and sticky. Poll **no more often than every 300 s**. On 429, block for at least `max(Retry-After, 5 min)` and back off to 60 min. This mirrors Claude Code's own per-token memory: 5 min default, 1 h cap. |

---

## 1. Request

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer sk-ant-oat01-…            (claudeAiOauth.accessToken)
anthropic-beta: oauth-2025-04-20
Content-Type: application/json                   (Claude Code sends it; harmless)
User-Agent: claude-code/<version>
```

### What Claude Code does

**A2 (March 2026; `services/api/usage.ts`, `utils/http.ts`, `utils/userAgent.ts`):**
- The URL is `${BASE_API_URL}/api/oauth/usage` with `BASE_API_URL = 'https://api.anthropic.com'`.
- The auth headers are `Authorization: Bearer …` and `'anthropic-beta': OAUTH_BETA_HEADER`, where `OAUTH_BETA_HEADER = 'oauth-2025-04-20'`.
- The User-Agent is `claude-code/${MACRO.VERSION}`, and the timeout is 5000 ms.
- It skips the call when the token is expired. The comment reads "Skip API call if OAuth token is expired to avoid 401 errors". Expired means `now + 5 min >= expiresAt`.
- It returns `{}` when the user is not a subscriber or the token lacks `user:profile`.

**A1 (2.1.311, current):**
- The timeout is still 5000 ms.
- The request goes through a client with `refreshOAuth: true`. On a 401, Claude Code refreshes its own token under its lock and retries; the log line reads `"(401→refresh→retry succeeded)"`.
- It caches failures per bearer token:
  - A 429, or a 403 without an Anthropic-format JSON error body, is treated as `rate_limited`. It is remembered for `min(Retry-After > 0 ? Retry-After : 300000 ms, 3600000 ms)`.
  - A 401, or a 403 with an Anthropic-format error (`{"error":{"type":…}}`), is treated as `auth_rejected` and remembered for 3600000 ms.
- It also uses these query variants:
  - `?cedar_ember=1&skip_spend=1`
  - `?at_wall=1&skip_spend=1`
  - `?starry_waterfall=<surface>&skip_spend=1`
- The widget should call the **plain URL with no query** (verified 200).

### User-Agent

- clanker-usage hard-codes `USER_AGENT = "claude-code/2.1.71"`, with the comment "Anthropic's edge rejects other User-Agents with a 403". That claim is **UNCONFIRMED**.
- Community evidence in #30930 (fazxes, confirmed by misium) says something different: a non-Claude-Code UA such as `curl/x` lands in a **stricter rate-limit bucket** and gets 429.
- CodexBar sends `claude-cli/<ver> (external, cli)` with `?cedar_ember=1`. On a 400, or on a 403 whose body lacks `user:profile`, it retries with `claude-code/<ver>` and no query. Its fallback version is `2.1.0`.
- Both live probes returned 200, with `claude-code/2.1.200` and `claude-code/2.1.311`.
- **Recommendation:** always send `claude-code/<ver>`. Pick the highest version you can find, in this order:
  1. The running `claude.exe` processes' file version or path.
  2. The `%USERPROFILE%\.vscode\extensions\anthropic.claude-code-*` folder names.
  3. `claude --version` (the first token of `"2.1.200 (Claude Code)"`).
  4. A fixed string such as `claude-code/2.1.311`.

### Required scope

- The token needs `user:profile`. CodexBar's docs say: "CLI tokens with only `user:inference` cannot call usage".
- `claude setup-token` and `CLAUDE_CODE_OAUTH_TOKEN` tokens are inference-only. A3 says the token "can only make model requests".
- Check that `scopes` contains `"user:profile"` before polling.

---

## 2. Response schema (live, 2026-10-28; my probe, verbatim shape)

```json
{
  "five_hour":  { "utilization": 39, "resets_at": "2026-10-28T06:50:00.478089+00:00",
                  "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null },
  "seven_day":  { "utilization": 54, "resets_at": "2026-10-29T20:00:00.478111+00:00",
                  "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null },
  "seven_day_oauth_apps": null, "seven_day_opus": null, "seven_day_sonnet": null,
  "seven_day_cowork": null, "seven_day_omelette": null,
  "tangelo": null, "iguana_necktie": null, "omelette_promotional": null, "nimbus_quill": null,
  "cinder_cove": null, "copper_kite": null, "brass_thimble": null, "harbor_lantern": null,
  "wattle_ember": null, "amber_ladder": null, "granite_teacup": null, "amber_cistern": null,
  "amber_cistern_offer": null, "juniper_tide": null, "cedar_ember": null, "starry_waterfall": null,
  "amber_gauge": null, "pace": null,
  "extra_usage": { "is_enabled": false, "monthly_limit": null, "used_credits": null, "utilization": null,
                   "currency": null, "decimal_places": null, "disabled_reason": null, "user_disabled": false,
                   "spend_limit_reached": false, "credits_ever_enabled": false, "daily": null, "weekly": null },
  "limits": [
    { "kind": "session", "group": "session", "percent": 39, "severity": "normal",
      "resets_at": "2026-10-28T06:50:00.478089+00:00", "scope": null, "is_active": false },
    { "kind": "weekly_all", "group": "weekly", "percent": 54, "severity": "normal",
      "resets_at": "2026-10-29T20:00:00.478111+00:00", "scope": null, "is_active": true },
    { "kind": "weekly_scoped", "group": "weekly", "percent": 35, "severity": "normal",
      "resets_at": "2026-10-29T20:00:00.478257+00:00",
      "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null }, "is_active": false }
  ],
  "spend": { "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 },
             "limit": null, "percent": 0, "severity": "normal", "enabled": false, "disabled_reason": null,
             "cap": null, "balance": null, "auto_reload": null, "disclaimer": "<string>",
             "can_purchase_credits": false, "can_toggle": false },
  "member_dashboard_available": false,
  "seven_day_breakdown": null,
  "weekly_scoped_shares": [ { "limit_index": 2, "used_percent_of_weekly": 18, "allowance_percent_of_weekly": 50 } ]
}
```

**Response headers on 200:**
- Present: `content-type: application/json`, `request-id`, `anthropic-organization-id`, `anthropic-workspace-id`, `cf-ray`.
- Absent: `retry-after` and any rate-limit headers.

**Keys Claude Code itself recognizes** as a usage body (A1 list): `five_hour`, `seven_day`, `seven_day_oauth_apps`, `seven_day_opus`, `seven_day_sonnet`, `cinder_cove`, `wattle_ember`, `extra_usage`, `limits`.

### 2.1 Window object (`five_hour`, `seven_day`, `seven_day_*`)

| Field | Type | Notes |
|---|---|---|
| `utilization` | number or null | Percent from 0 to 100. Parse as `double?`; ints (`39`) and floats (`74.0`, from #30930) both occur. Whether it can exceed 100 is **UNCONFIRMED**, so clamp for display. clanker rejects `percent` values outside 0–100. |
| `resets_at` | string or null | ISO 8601 with offset and up to 6 fractional digits. Null when the window has not started. It **jitters by about 1 s between calls** (06:49:59.595952 in one probe, 06:50:00.478089 in the next), so round to the minute. |
| `limit_dollars`, `used_dollars`, `remaining_dollars`, `locked_reason` | null so far | Their meaning on 5-hour and weekly windows is **UNCONFIRMED**. CodexBar docs say these fields are already **USD (not cents)** inside `iguana_necktie` (cloud credits), where `resets_at` is an expiry. |

**CodexBar mappings:**
- **Daily Routines:** taken from the first of `seven_day_routines`, `seven_day_claude_routines`, `claude_routines`, `routines`, `routine`, `seven_day_cowork`, `cowork`.
- **Claude Design / Omelette keys:** ignored, because "Claude Design shares the main Claude usage limit".
- **Fallback when `five_hour` is null:** CodexBar uses `seven_day`, then `seven_day_oauth_apps`, then `seven_day_sonnet`, then `seven_day_opus`.

### 2.2 `limits[]`: the primary, self-describing form

The descriptions below are quoted from Claude Code 2.1.311's own schema, which is marked "Experimental — the shape may change".

| Field | Type | Meaning |
|---|---|---|
| `kind` | string | "The server's meter kind, e.g. 'session', 'weekly_all' or 'weekly_scoped'. Classify a row on this, never on a label." |
| `group` | string | "e.g. 'session' or 'weekly'; rows render grouped under it, in the server's order." |
| `percent` | number | "Share of the window used, 0-100." Read it only when the JSON value is a Number; CodexBar ignores booleans and strings. |
| `resets_at` | string or null | ISO 8601. |
| `scope` | object or null | `{ model: {id?, display_name} \| null, surface: {display_name} \| null }`. A1's schema types `surface` as `{display_name}`, so surface-scoped rows may appear (a scenario that is **UNCONFIRMED**). In my probe `model.id` was null and `display_name` was "Fable". |
| `severity` | string | "The server's reading of the row for a meter's colour, e.g. 'normal', 'warning' or 'critical'. Every row here is the server's, so a client never grades a row itself." |
| `is_active` | bool | "The server's headline pick: the row a single-value indicator shows." CodexBar does not filter on it, because "observed enforceable scoped limits report false". |

- **`limits` itself:** "Empty when the server reported no meters; null when the body carried no rows at all (a server that predates them)."
- **How Claude Code renders scoped rows:**
  - The title is `Current week (<display_name>)`, built from `{utilization: percent, resets_at}`.
  - It only shows rows whose name is in a GrowthBook allowlist, `tengu_usage_overage_included_models`, which defaults to `["Fable","Fable 5","Fable 5.1"]`.
  - CodexBar shows every `group=="weekly" && kind=="weekly_scoped"` row that has a non-empty name. It skips "All models" scopes and dedupes by slug.
  - **Recommendation:** show every weekly_scoped row.
- **`weekly_scoped_shares[]`** has the fields `limit_index` (an index into `limits`), `used_percent_of_weekly` and `allowance_percent_of_weekly`.
  - The first probe had 17/50, which matched `percent` 34.
  - My probe had 18/50 = 36, but `percent` was 35, so the values are rounded and the relation is not exact.
  - Do not derive `percent` from these fields.

**Recommended parse order:**
- **Session:** `limits[kind=="session"].percent`, else `five_hour.utilization`.
- **Weekly (all models):** `limits[kind=="weekly_all"].percent`, else `seven_day.utilization`.
  - In both probes the two forms were equal: session = five_hour and weekly_all = seven_day.
  - ccusage 0.1.10 and clanker 0.4.4 read only `limits`. CodexBar reads the flat keys first. Supporting both is safest.
- **Model weekly rows:** each `limits[kind=="weekly_scoped"]` entry whose `scope.model.display_name` is not empty, plus the legacy `seven_day_sonnet` and `seven_day_opus` when they are not null.
- **Headline for a single-number taskbar:** the row with `is_active==true`. If none is active, use the highest `percent`.
- **Do not hard-code the session length.** The response does not state it. clanker labels it "5-hour", and ccusage 0.1.10 used "Session".

### 2.3 `extra_usage` (overage) and `spend`

- **`extra_usage` (A1 schema):** `{ is_enabled: bool, monthly_limit: number|null, used_credits: number|null, utilization: number|null, currency?: string|null }`.
  - The A1 description reads: "**Amounts are in minor units of `currency` (cents for USD)**; is_enabled is false while extra usage cannot cover sends."
  - CodexBar uses the same rule ("OAuth extra usage is always in cents, including Enterprise spend-only responses"): it divides by 100 and treats an empty currency as `USD`.
  - ccusage 0.1.10 shows `monthly_limit: 100000` as `$1000.00`.
- **Live extra fields:** `decimal_places`, `disabled_reason`, `user_disabled`, `spend_limit_reached`, `credits_ever_enabled`, `daily`, `weekly`.
  - **Divisor:** use `10^decimal_places` when present, else 100. This is an inference; that `decimal_places` is the right divisor is **UNCONFIRMED**.
  - **Display:** show only when `is_enabled == true`. `spend_limit_reached == true` makes a good red flag.
- **`spend`** stores money explicitly as `{amount_minor, currency, exponent}`. Its other fields are `percent`, `severity`, `enabled`, `limit`, `cap`, `balance`, `auto_reload`, `can_purchase_credits` and `can_toggle`.
  - Claude Code passes `skip_spend=1` on its own reads, which suggests the server can omit `spend`.
  - The full semantics are **UNCONFIRMED**.

### 2.4 Optional blocks (CodexBar)

- **`cedar_ember`:** returned only with the query parameter. It is `{ eligible, grants:[{ resets_left, resets_total, starts_at, ends_at, paused }] }`, the "Limit Reset Credits". The widget does not need it.
- **`iguana_necktie`:** cloud credits, `{ limit_dollars, used_dollars, remaining_dollars, resets_at (expiry), locked_reason }`, in USD and not divided by 100.

### 2.5 Status codes

| Status | Meaning | Widget action |
|---|---|---|
| 200 | OK | Cache it, and clear the backoff. |
| 401 | Access token expired or revoked | Re-read the credentials file. If `accessToken` changed, retry once. Otherwise show "Sign-in expired — open Claude Code" and wait for the file to change. **Do not refresh.** Claude Code itself remembers a 401 per token for 1 h. |
| 403 with an Anthropic JSON error (`{"error":{"type":…}}`) | Auth or permission: missing `user:profile`, no subscription, or the organization disabled OAuth | Show "Usage unavailable for this login". CodexBar detects the scope case when the body contains `user:profile`, and clanker reports "no active subscription, or the organization disabled OAuth". Wait about 1 h, or until the token changes. |
| 403 without an Anthropic JSON error | Edge or throttle | Treat as 429. This is how Claude Code 2.1.311 classifies it. |
| 400 | Only expected with optional query params | Not expected for the plain URL. |
| 429 | Rate limited | Body: `{"error":{"message":"Rate limited. Please try again later.","type":"rate_limit_error"}}`. `retry-after: 0` (#30930) or missing (#31637). See §5. |
| 5xx / network | Server or connection error | Back off and show cached data. |

---

## 3. Credentials

### 3.1 Location

**Official docs** (code.claude.com/docs/en/authentication):
- **Windows:** credentials "are stored in `%USERPROFILE%\.claude\.credentials.json` and inherit the access controls of your user profile directory".
- **Linux:** `~/.claude/.credentials.json`, mode `0600`.
- **macOS:** the Keychain. Claude Code falls back to `~/.claude/.credentials.json` when the Keychain rejects the write.
- **`CLAUDE_CONFIG_DIR`:** "Claude Code keeps the `.credentials.json` file under that directory instead … and keys the macOS Keychain entry to that directory too".

**Exact resolution in Claude Code 2.1.311 (A1):**

```js
function ZN(){ let e=process.env.CLAUDE_SECURESTORAGE_CONFIG_DIR;
  if(e!==void 0) return (e || join(homedir(),'.claude')).normalize('NFC');
  return configHome(); }                       // configHome = CLAUDE_CONFIG_DIR ?? join(homedir(),'.claude')
// credentials file = join(ZN(), '.credentials.json')
```

**Widget resolution order:**
1. If the variable `CLAUDE_SECURESTORAGE_CONFIG_DIR` **exists**, use its value, or `%USERPROFILE%\.claude` when the value is empty. An empty value pins the default store even when `CLAUDE_CONFIG_DIR` is set.
2. Otherwise, if `CLAUDE_CONFIG_DIR` is set, use it.
3. Otherwise, use `%USERPROFILE%\.claude`.

Notes:
- `CLAUDE_SECURESTORAGE_CONFIG_DIR` is undocumented; docs issue #79223 is still open as of 2.1.301. CodexBar honors it.
- Optionally let the user add extra config directories for multiple accounts, as CodexBar and clanker (`configure --config-dir`) do.

**macOS service name:** `Claude Code${OAUTH_FILE_SUFFIX}-credentials` plus `-<sha256(dir).hex[0..8]>` when the chosen directory is non-default. In production the suffix is empty, giving `Claude Code-credentials`. Every platform except darwin uses only the plaintext file.

### 3.2 `.credentials.json` schema

These are the key names and types on this PC, written by Claude Code 2.1.311.

```json
{
  "claudeAiOauth": {
    "accessToken": "sk-ant-oat01-…",          // string (len 108 here)
    "refreshToken": "sk-ant-ort01-…",         // string
    "expiresAt": 1793181056830,               // int64 ms epoch; ~8 h after issue
    "refreshTokenExpiresAt": 1794241627830,   // int64 ms epoch; from token response `refresh_token_expires_in` (~12.6 d here; ~24 h in #91571)
    "scopes": ["user:file_upload","user:inference","user:mcp_servers","user:plugins","user:profile","user:sessions:claude_code"],
    "subscriptionType": "team",               // "max" | "pro" | "team" | "enterprise" | null
    "subscriptionTypeSource": "sign_in",      // "sign_in" | "profile"; only present when subscriptionType != null
    "rateLimitTier": "default_claude_max_5x"  // string | null
  },
  "organizationUuid": "…"                     // top-level sibling
}
```

- **Other top-level blocks.** The store can also hold `mcpOAuth`, `cloudSessionsOauth` and `designOauth`, all referenced in A1. CodexBar has seen macOS Keychain items with only `mcpOAuth`. Ignore unknown keys and never rewrite the file.
- **Signed-out detection.** Treat an empty `accessToken` or `refreshToken`, or `expiresAt: 0`, as "signed out". Issue #91571 (Windows 2.1.220, Linux 2.1.270/2.1.280) reports Claude Code blanking both tokens to `""` with `expiresAt: 0` after a failed refresh.
- **Partial writes.**
  - The March source writes with `writeFileSync(…, {flush:false})`, not temp-plus-rename, and 2.1.311 has an `O_WRONLY|O_CREAT|O_TRUNC` write path.
  - Open the file with `FileShare.ReadWrite | FileShare.Delete`.
  - On a parse error or an empty file, retry after about 200–500 ms. Never treat a parse error as a logout.
- **Change detection.** Claude Code watches the file's mtime (`invalidateOAuthCacheIfDiskChanged` in A2). Use a `FileSystemWatcher` on the directory plus an mtime check on each poll.
- **Access token lifetime is about 8 h.** Evidence: #31095 ("8-hour lifetime"), #80585 ("~8h"), and this PC (about 7.3 h remaining at check). The "roughly hourly" claim in the ccusage 0.1.10 README is wrong for current accounts. Always read `expiresAt`.

### 3.3 `.claude.json` (account and plan metadata, no secrets)

- **Path** (A2 `getGlobalClaudeFile`):
  1. If `<configDir>\.config.json` exists, use it (legacy).
  2. Otherwise use `.claude.json` in `CLAUDE_CONFIG_DIR` if set, else in `%USERPROFILE%`.
- The file is about 79 KB here. Use System.Text.Json.
- **`oauthAccount` keys on this PC (verified):** `accountUuid`, `emailAddress`, `organizationUuid`, `hasExtraUsageEnabled` (bool), `billingType` (`"stripe_subscription"`), `accountCreatedAt`, `subscriptionCreatedAt`, `ccOnboardingFlags`, `claudeCodeTrialEndsAt`, `claudeCodeTrialDurationDays`, `seatTier` (`"team_tier_1"`), `displayName`, `fullName`, `profileFetchedAt` (ms), `organizationRole`, `workspaceRole`, `organizationName`, `organizationType` (`"claude_team"`), `organizationRateLimitTier` (`"default_raven"`), `userRateLimitTier` (`"default_claude_max_5x"`).
- **Stable account key:** `oauthAccount.accountUuid`, as CodexBar and clanker use. clanker aborts a fetch if the UUID changed mid-request.

---

## 4. Token expiry and refresh: the widget must NOT refresh

### Evidence of rotation and reuse revocation

1. **Official docs** (troubleshoot-install):
   - "Parallel sessions on one machine share a saved login and coordinate its renewal so that only one process refreshes the token at a time."
   - "Before v2.1.211, waking the machine from sleep could cause two sessions to renew with the same token, which revoked the saved login and prompted every open session to log in again at once."
2. **Refresh code** (A2, and A1 for the same request):
   - The request is `POST https://platform.claude.com/v1/oauth/token`, `Content-Type: application/json`, with body `{grant_type:'refresh_token', refresh_token, client_id:'9d1c250a-e61b-44d9-88ed-5944d1962f5e', scope:'user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload'}`.
   - A1 may request more scopes, such as `user:plugins` and `user:design:*`.
   - The timeout is 30 s in A1 and 15 s in A2.
   - The code reads `refresh_token: newRefreshToken = refreshToken`, so it saves a rotated refresh token whenever one is returned.
   - A1 also reads `refresh_token_expires_in`, which becomes `refreshTokenExpiresAt`.
3. **CodexBar** (`ClaudeOAuthCredentials.swift`): "Claude Code rotates refresh tokens; evidence of CLI storage or a logged-in Claude Code config keeps the chain CLI-owned … never rotate a chain we cannot prove we own."
4. **clanker-usage** (`providers/claude.py`): "Anthropic rotates refresh tokens, so the new one must reach Claude Code's file, or Claude Code is signed out."
5. **Issues:**
   - #80585: the refresh grant "appears to be single-use"; a losing racer gets `invalid_grant` and the stored credential is cleared.
   - #30930 and #31637: refreshing manually to dodge 429s "logs out my claude code itself" and forced re-login "every few minutes".
   - #91571: rotations were not persisted to `.credentials.json` on Windows 2.1.220. The refresh token in the file may already be superseded, so using it risks `invalid_grant` or family revocation. The revocation risk is inferred.

### How Claude Code refreshes

**March 2026 (A2), `checkAndRefreshOAuthTokenIfNeeded`:**
- A token counts as expired when `now + 5 min >= expiresAt`.
- The sequence is: re-read storage, take the `proper-lockfile` lock `lock(configDir)`, re-read again, refresh, save, release.
  - The lock directory is `<configDir>.lock`, i.e. `%USERPROFILE%\.claude.lock`, with defaults stale 10 s and update 5 s.
  - It retries the lock 5 times with 1–2 s random sleeps.
- On a 401 (`handleOAuth401Error`), it clears its caches and re-reads storage. If the stored token differs from the one that failed, it uses that ("another tab already refreshed"); otherwise it force-refreshes.

**Current (A1, 2.1.311): the lock changed.**
- The new lock is `join(storeDir, '.oauth_refresh.lock')` with `{realpath:false, stale:60000, update:5000}`.
- The **legacy** lock `${realpath(dir)}.lock` is also acquired.
- An owner record `.oauth_refresh.lock.owner` is written: JSON with `pid`, `procStart`, `pidDomain`, `pidSpace`, `lockBirthtimeMs`, `legacyLockBirthtimeMs`.
- Usage reads now refresh on a 401 (`refreshOAuth: true`).

**`claude auth status --json`** (A2 `cli/handlers/auth.ts`):
- It is read-only and outputs `loggedIn`, `authMethod`, `apiProvider`, plus `email`, `orgId`, `orgName` and `subscriptionType` when `authMethod == "claude.ai"`.
- It exits 0 when logged in and 1 otherwise.

### What other monitors do

| Tool | Behavior |
|---|---|
| **Claude Code `/usage`** | In A2 it skipped the call when expired. In A1 (2.1.311) it refreshes on a 401 under its own lock, and caches 429/403 per token. |
| **CodexBar** | Never refreshes CLI-owned credentials (file or Keychain). When they are expired it throws `refreshDelegatedToClaudeCLI` and runs `claude` with `/status` in a PTY (`ClaudeStatusProbe.touchOAuthAuthPath`, 8 s timeout), then re-reads. The cooldown is 300 s (20 s short). In the background this only runs if the user chose "Always allow prompts" or the refresh is user-initiated. It self-refreshes only chains it owns, form-encoded, at `https://platform.claude.com/v1/oauth/token`. |
| **clanker-usage / ccusage 0.4.4** | Refreshes when `now > expiresAt − 60 s` or on a 401. It re-reads the file first; if the tokens changed, it uses them. Otherwise it POSTs JSON (no scope) to `https://api.anthropic.com/v1/oauth/token` with UA `claude-code/2.1.71`, and writes back only if (accessToken, refreshToken) is unchanged (compare-and-swap, temp + fsync + `os.replace`). It takes **no lock**. On a 400/401 from the token endpoint it re-reads in case Claude Code won the race. |

### Recommended widget algorithm (read-only)

1. Before each poll, resolve the path (§3.1) and read the file, retrying on parse errors. Compute `remaining = expiresAt − now`.
2. If `accessToken` is empty or `expiresAt == 0`, show "Signed out — run `claude` and `/login`".
3. If `scopes` lacks `user:profile`, show "This login cannot read usage".
4. If `remaining < 5 min`, **skip the call**. Show cached data plus "waiting for Claude Code to renew sign-in", and poll again as soon as the file's mtime or `accessToken` changes. A running Claude Code session (2.1.311 or later) renews automatically on its next request.
5. On a 401, re-read the file once. If the token changed, retry once; otherwise use the waiting state from step 4. Do not call again with the same token for 1 h, as Claude Code does.
6. Optional user-initiated "Renew now" button: launch `claude` interactively in a new terminal so Claude Code refreshes under its own lock.
   - Do not use `claude -p`, which spends quota.
   - Whether an idle interactive start or `/status` reliably refreshes on Windows is **UNCONFIRMED**; CodexBar relies on `/status` in a PTY on macOS.
7. Show a login-expiry hint from `refreshTokenExpiresAt` (ms). Per A3, Claude Code warns "within three days of expiring". The lifetime varies, so compute it from the value rather than assuming one.

**Self-refresh: do not implement it.** If it is ever forced (opt-in only), it would have to:
- Take **both** of Claude Code's current locks: `<storeDir>\.oauth_refresh.lock` (stale 60 s, mtime touched every 5 s or less) and the legacy `<storeDir>.lock`.
- Re-read the file under the lock, and confirm it is still expired with an unchanged refresh token.
- POST to `https://platform.claude.com/v1/oauth/token`.
- Merge only `accessToken`, `refreshToken`, `expiresAt = now + expires_in*1000` and `refreshTokenExpiresAt` (from `refresh_token_expires_in`), keeping every other key.
- Write atomically (temp file plus `File.Replace`).

Even then it cannot protect against versions that keep rotated tokens only in memory (#91571), and the lock protocol is internal and changes between versions.

---

## 5. Rate limiting of `/api/oauth/usage`

- **No official numbers or documentation.** No Anthropic staff reply appears on these issues:

  | Issue | State |
  |---|---|
  | #30930 | Open |
  | #31637 | Closed not_planned by the inactivity bot |
  | #31021 | Closed by its reporter as a duplicate |
  | #31319 | Closed (state_reason completed), no staff comment |

- **429 body:** `{"error":{"message":"Rate limited. Please try again later.","type":"rate_limit_error"}}`.
  - The header is `retry-after: 0` (#30930) or missing (#31637).
  - The 429 state persisted 30+ minutes with retries at 300 s, and for hours with 10-minute polling plus backoff to 30 min (#31637 comment).
- **Community claims (UNCONFIRMED):**
  - The limit is per access token, about 5 requests per token (onWatch author). Exploiting it by refreshing is unsafe; see §4.
  - The User-Agent selects the bucket: a `claude-code/<ver>` UA is more generous than curl (#30930).
  - Several concurrent Claude Code sessions drain the bucket, because Claude Code polls the endpoint itself (#31319).
  - On this PC, 7 Claude Code processes and a separate `D:\Other\ClaudeUsage.exe` monitor are already running, so expect contention.
- **What Claude Code does (A1):** a per-token memory of 429 (and 403 without an error JSON) for `Retry-After` if > 0, else 300 s, capped at 3600 s.
- **What other tools do:**
  - **CodexBar:** `ClaudeOAuthUsageRateLimitGate.defaultCooldown = 300 s`, or `Retry-After` when that is later. The block is keyed by sha256(accessToken), and user-initiated refreshes bypass it. Fresh installs use "Adaptive" refresh; the legacy default is 5 min, with fixed options of 1, 2, 5, 15 and 30 min.
  - **clanker:** `INTERVAL = 300`. Status and statusline back off `min(60·2^(n−1), 1800)` s. The daemon doubles up to `max(interval, 3600)` and skips a refresh if another process updated the same account's cache within the interval.
- **Recommendation for the widget:**
  - Poll every 300 s while visible, with about ±30 s of jitter. Allow one in-flight request per account.
  - Skip polls while the session is locked, the machine is asleep, or the widget is hidden.
  - On a 429 (or a 403 without an error JSON), block for `max(Retry-After, 300 s)`, then double up to a 60-minute cap. Keep showing the last good values with a "stale since hh:mm" badge. Never retry immediately.
  - Never poll with an expired token. Always send `User-Agent: claude-code/<ver>`.
  - Locally interpolate the countdown to `resets_at`. When `now > resets_at`, show "reset" or 0% until the next fetch. Claude Code's status line likewise drops a window once its `resets_at` passes.
- **Zero-cost supplement (official, docs/statusline):**
  - Claude Code's status-line stdin JSON has `rate_limits.five_hour.used_percentage` and `rate_limits.seven_day.used_percentage` (0–100), and `rate_limits.*.resets_at` (Unix epoch **seconds**). Behind a gateway there is also `rate_limits.spend_limit`.
  - It "appears only for claude.ai Pro and Max subscribers … and only after the first API response in the session". Team availability is **UNCONFIRMED**.
  - Per a comment on #30930, the feature was added in v2.1.80.
  - It has no scoped (Fable) row and no extra usage, and using it requires the user to configure a `statusLine.command` that writes the JSON to a file the widget reads. Treat it as optional.

---

## 6. Display-worthy information

- **Plan** (from `.credentials.json` `claudeAiOauth`):
  - `subscriptionType` is `max`, `pro`, `team` or `enterprise`, mapped from `organization.organization_type` values `claude_max`, `claude_pro`, `claude_team` and `claude_enterprise` (A2).
  - `rateLimitTier` is a string such as `default_claude_max_5x`.
  - CodexBar shows the multiplier from the token after `max` that matches `^\d+x$`, giving "Claude Max 5x" or "Max 20x". Its docs name `default_claude_max_20x`, but that string is not observed live.
  - clanker strips the `default_claude_` prefix.
- **Team seat** (`.claude.json` `oauthAccount.seatTier`):
  - CodexBar maps `team_tier_1` to "Claude Team Premium" and `team_standard` to "Claude Team Standard".
  - On this PC: Team, `team_tier_1`, `userRateLimitTier` `default_claude_max_5x`.
- **Rows to show:**
  - "Session (5h)", from `limits[session]` or `five_hour`.
  - "Weekly", from `limits[weekly_all]` or `seven_day`.
  - "Weekly – <display_name>" for each `limits[weekly_scoped]` (currently "Fable").
  - Legacy `seven_day_sonnet` and `seven_day_opus` when not null.
  - Optional "Daily Routines".
  - For a single-number taskbar view, use the `is_active` row.
- **Colour:** use `limits[].severity` (`normal` / `warning` / `critical`), and fall back to percent thresholds only when it is missing.
  - The ccusage-reported Claude Code thresholds (five_hour 90% when 72% of the window has passed; seven_day 75%@60%, 50%@35%, 25%@15%) come from cli.js v2.1.32 and are **UNCONFIRMED** for current versions.
- **Extra usage:** when `extra_usage.is_enabled`, show `used_credits/monthly_limit` in minor units (÷100, or ÷10^`decimal_places`) in `currency`, defaulting to USD. Show `spend_limit_reached` as a red flag.
- **Login expiry:** from `refreshTokenExpiresAt`.
- **Organization name:** `oauthAccount.organizationName`. Do not show the email by default.

---

## 7. C# (.NET 10) sketch

```csharp
public sealed class ClaudeWindow {
    [JsonPropertyName("utilization")] public double? Utilization { get; set; }
    [JsonPropertyName("resets_at")]   public string? ResetsAtRaw { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    public DateTimeOffset? ResetsAt => DateTimeOffset.TryParse(ResetsAtRaw, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var d) ? d : null;   // strings carry an offset; AssumeUniversal only applies if absent
}
public sealed class ClaudeExtraUsage {
    [JsonPropertyName("is_enabled")]    public bool? IsEnabled { get; set; }
    [JsonPropertyName("monthly_limit")] public double? MonthlyLimitMinor { get; set; }   // minor units (cents for USD)
    [JsonPropertyName("used_credits")]  public double? UsedCreditsMinor { get; set; }    // minor units
    [JsonPropertyName("utilization")]   public double? Utilization { get; set; }
    [JsonPropertyName("currency")]      public string? Currency { get; set; }
    [JsonPropertyName("decimal_places")] public int? DecimalPlaces { get; set; }
    [JsonPropertyName("spend_limit_reached")] public bool? SpendLimitReached { get; set; }
}
public sealed class ClaudeUsageResponse {
    [JsonPropertyName("five_hour")]  public ClaudeWindow? FiveHour { get; set; }
    [JsonPropertyName("seven_day")]  public ClaudeWindow? SevenDay { get; set; }
    [JsonPropertyName("seven_day_sonnet")] public ClaudeWindow? SevenDaySonnet { get; set; }
    [JsonPropertyName("seven_day_opus")]   public ClaudeWindow? SevenDayOpus { get; set; }
    [JsonPropertyName("seven_day_oauth_apps")] public ClaudeWindow? SevenDayOAuthApps { get; set; }
    [JsonPropertyName("extra_usage")] public ClaudeExtraUsage? ExtraUsage { get; set; }
    [JsonPropertyName("limits")] public JsonElement Limits { get; set; }   // Null / Array; parse entry-by-entry
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
// limits[] entry, parsed defensively from JsonElement (a bad field must not drop the entry or its siblings):
//   kind (string), group (string), percent (only if ValueKind == Number), severity (string),
//   resets_at (string|null), scope?.model?.display_name / id (string|null), scope?.surface?.display_name,
//   is_active (only if True/False)
```

- **HTTP:** use `HttpClient` with `AllowAutoRedirect = false`, as clanker does, and a 15–30 s timeout. Claude Code itself uses 5 s and CodexBar 30 s.
- **Logging:** never log the token or the raw body. CodexBar also disables the URL cache for this response.
- **Caching:** cache the last good response per `accountUuid` on disk, to survive restarts and 429s.
- **Reading credentials:** open `.credentials.json` with `FileShare.ReadWrite | FileShare.Delete`, retry on JSON errors, and **never write it**.

---

## 8. Sources

**Claude Code**
- **Running 2.1.311 binary (embedded JS):** `C:\Users\kyung\.vscode\extensions\anthropic.claude-code-2.1.311-win32-x64\resources\native-binary\claude.exe`.
  - Usage fetch: `fetchUtilization`, query variants, `Lt=300000` / `je=3600000` / `xt=3600000`.
  - `rate_limits` zod schema with field descriptions.
  - Refresh: `formatTokens` (`refreshTokenExpiresAt`, `subscriptionTypeSource`), `TOKEN_URL`, `.oauth_refresh.lock`.
  - Credential directory: the `ZN()` and `Tie()` resolvers.
- **Source mirror (March 2026):** https://tangled.org/oppi.li/claude-code. Files: `services/api/usage.ts`, `services/oauth/client.ts`, `utils/auth.ts`, `utils/http.ts`, `utils/userAgent.ts`, `constants/oauth.ts`, `utils/envUtils.ts`, `utils/env.ts`, `utils/secureStorage/{index,plainTextStorage,macOsKeychainHelpers}.ts`, `utils/lockfile.ts`, `cli/handlers/auth.ts`.

**Official docs**
- https://code.claude.com/docs/en/authentication
- https://code.claude.com/docs/en/troubleshoot-install
- https://code.claude.com/docs/en/statusline

**Live probes**
- Researcher's probe: `…\scratchpad\probe_usage.js`.
- Verifier's probe: `C:\Users\kyung\AppData\Local\Temp\claude\d--AITokenUsage-WorkSpace-AITokenUsage\b0f14b69-2945-43cd-89fe-a14ff4f6c1e2\scratchpad\verify_probe.js`.

**CodexBar** (Sources/CodexBarCore/Providers/Claude/)
- `ClaudeOAuth/ClaudeOAuthUsageFetcher.swift`
- `ClaudeOAuth/ClaudeOAuthUsageRateLimitGate.swift`
- `ClaudeOAuth/ClaudeOAuthCredentials.swift`
- `ClaudeOAuth/ClaudeOAuthCredentialModels.swift`
- `ClaudeOAuth/ClaudeOAuthDelegatedRefreshCoordinator.swift`
- `ClaudeUsageFetcher.swift`
- `ClaudeScopedWeeklyLimitMapper.swift`
- `ClaudeWeb/ClaudeWebExtraRateWindowParser.swift`
- `ClaudeConfigPaths.swift`
- `ClaudePlan.swift`
- `ClaudeStatusProbe.swift`
- Docs and README: https://github.com/steipete/CodexBar/blob/main/docs/claude.md and https://github.com/steipete/CodexBar/blob/main/README.md

**clanker-usage 0.4.4**
- https://github.com/wakamex/clanker-usage/blob/HEAD/src/clanker_usage/providers/claude.py
- Also `http.py`, `oauth.py`, `cache.py`, `cli.py`, and `shapes/claude/claude-2026-10-19.json`.
- ccusage 0.1.10 README: https://pypi.org/project/ccusage/0.1.10/

**Issues**
- https://github.com/anthropics/claude-code/issues/30930
- https://github.com/anthropics/claude-code/issues/31637
- https://github.com/anthropics/claude-code/issues/31021
- https://github.com/anthropics/claude-code/issues/31319
- https://github.com/anthropics/claude-code/issues/31095
- https://github.com/anthropics/claude-code/issues/80585
- https://github.com/anthropics/claude-code/issues/91571
- https://github.com/anthropics/claude-code/issues/79223

**Other**
- proper-lockfile (defaults: stale 10000, update stale/2, retries 0): https://github.com/moxystudio/node-proper-lockfile


## Verifier: missing_but_important
- limits[].is_active is the server's 'headline pick: the row a single-value indicator shows' (Claude Code 2.1.311 schema). For a compact taskbar widget that shows one number, show the is_active row; this does not depend on reading is_active as 'enforced'.
- severity is graded by the server ('normal' | 'warning' | 'critical', per the Claude Code schema), and Claude Code says 'a client never grades a row itself'. Use severity for colour, and fall back to your own thresholds only when severity is missing.
- limits semantics: null means the server predates limits; [] means the server reported no meters. scope can carry `surface: {display_name}` as well as `model: {id, display_name}`, so surface-scoped rows may appear.
- Claude Code 2.1.311 caches failures per bearer: 429, or a 403 without an Anthropic-format JSON error, is cached as rate_limited for min(Retry-After>0 ? Retry-After : 300 s, 3600 s); 401, or a 403 WITH an Anthropic error JSON, is cached as auth_rejected for 3600 s. The widget should mirror this, treating a 403 with no JSON error body as throttling rather than as 'no subscription'.
- Claude Code's refresh lock changed after the March-2026 mirror. Current builds lock `<storeDir>\.oauth_refresh.lock` (stale 60 s, update 5 s) plus the legacy `<configDir>.lock`, and write `.oauth_refresh.lock.owner`. A self-refresh built from the March source would not coordinate with current Claude Code, which is another strong reason never to refresh.
- Current Claude Code (2.1.311) refreshes its own token on a 401 during usage reads ('401->refresh->retry'). When the widget sees an expired or 401 token, the fix is to let any running Claude Code session refresh, then re-read the file when its mtime changes.
- The refresh-token lifetime varies and is set by the server (`refresh_token_expires_in` -> refreshTokenExpiresAt). It is about 12.6 days on this PC, but issue #91571 saw about login + 24 h. The access token is about 8 h, not 'hourly'.
- subscriptionTypeSource values are 'sign_in' | 'profile', and the key is only written when subscriptionType is non-null (2.1.311 formatTokens).
- The credential store can hold other top-level blocks besides claudeAiOauth and organizationUuid: mcpOAuth, cloudSessionsOauth and designOauth all appear in 2.1.311. Never rewrite the file, and ignore unknown keys.
- This PC has three Claude Code installs (npm 2.1.200 on PATH, ~/.local/bin 2.1.199, and the running VS Code extension 2.1.311). `claude --version` reports a stale version. For the User-Agent, any recent claude-code/<ver> string works; my probe with claude-code/2.1.311 returned 200.
- Community evidence in #30930 says the User-Agent selects the rate-limit bucket: curl's default UA hits a stricter bucket than `claude-code/<ver>`. Always send a Claude Code UA.
- resets_at jitters between calls: the same window read 06:49:59.595952 in one probe and 06:50:00.478089 in another. Round to the minute for display, and do not treat a changed resets_at as a reset unless it moves forward by roughly a window length.
- Another usage monitor (D:\Other\ClaudeUsage.exe) is running on this PC alongside 7 Claude Code processes. All of them may draw from the same usage-endpoint bucket, so expect 429s during testing that the widget did not cause.
- The 200 response carries an `anthropic-organization-id` header, and no rate-limit or Retry-After headers on success.
- Claude Code itself shows scoped rows titled 'Current week (<display_name>)', limited to a GrowthBook allowlist `tengu_usage_overage_included_models` (default ['Fable','Fable 5','Fable 5.1']). The widget should show every weekly_scoped row the server sends, as CodexBar does.
- CodexBar documents that the `*_dollars` fields (limit_dollars, used_dollars, remaining_dollars, seen in iguana_necktie cloud credits) are already USD and are never divided by 100, and that iguana_necktie.resets_at is an expiry. This differs from extra_usage, which is in cents.

## Verifier: checked claims (non-confirmed only)
- [corrected] Claude Code refreshes via POST https://platform.claude.com/v1/oauth/token with JSON {grant_type, refresh_token, client_id '9d1c250a-e61b-44d9-88ed-5944d1962f5e', scope}, treats a token as expired 5 min before expiresAt, and refreshes under a proper-lockfile lock on <configDir>.lock, re-reading storage before and after the lock.
  -> The URL, JSON body, client_id and 5-minute buffer are confirmed in the mirror, and the URL, body and client_id also appear in 2.1.311 (refresh timeout 30000 ms in 2.1.311, 15000 ms in the mirror). The lock is OUTDATED. The March mirror calls `lockfile.lock(claudeDir)` with proper-lockfile defaults (lock directory `<configDir>.lock`, stale 10 s, update 5 s). Current 2.1.311 takes a NEW lock at `join(storeDir, '.oauth_refresh.lock')` with `{realpath:false, stale:60000, update:5000}`, ALSO takes the legacy `${realpath(dir)}.lock`, and writes an owner record `.oauth_refresh.lock.owner` (JSON with pid, procStart, pidDomain, pidSpace, lockBirthtimeMs, legacyLockBirthtimeMs). A third-party refresher that copies the March lock would not coordinate with current Claude Code.
- [corrected] Claude Code's own usage fetch skips the call (returns null) when the OAuth token is expired, rather than refreshing for it.
  -> True only for the March-2026 mirror (comment 'Skip API call if OAuth token is expired to avoid 401 errors'). In 2.1.311, fetchUtilization issues the GET with `refreshOAuth: !0` and logs 'fetchUtilization: 200 after N attempt(s) (401->refresh->retry succeeded)', so Claude Code now refreshes its own token for usage reads, under its own lock. It also caches failures per bearer token: 429 or 403 (without an Anthropic error body) -> 'rate_limited' for min(Retry-After > 0 ? Retry-After : 300000 ms, 3600000 ms); 401, or 403 with an Anthropic-format error body -> 'auth_rejected' for 3600000 ms. The conclusion for the widget is unchanged: the widget must not refresh.
- [could-not-verify] Anthropic's edge rejects non-Claude-Code User-Agents with a 403 on the usage endpoint.
  -> Still only a code comment in clanker-usage. Related community evidence in #30930 (fazxes, confirmed by misium) points the other way: a non-claude-code UA such as curl lands in a stricter rate-limit bucket and gets 429, not 403. Claude Code 2.1.311 classifies a 403 that lacks an Anthropic-format JSON error as 'rate_limited', which suggests edge 403s can be throttling. Practical rule: always send `User-Agent: claude-code/<version>`.
- [could-not-verify] The usage-endpoint rate limit is per access token, roughly 5 requests before a 429.
  -> This is only a community claim, posted by the onWatch author (prakersh) on #30930, #31021 and #31637. CodexBar and Claude Code 2.1.311 both key their 429 memory per bearer token, which is consistent with but does not prove a per-token bucket. Exploiting it by refreshing logs Claude Code out (GiGurra on #30930 and #31637).
- [corrected] Installed version on this PC is Claude Code 2.1.200 (`claude --version` -> '2.1.200 (Claude Code)').
  -> There are three installs. `claude` on PATH is the npm package at %APPDATA%\npm (2.1.200), and %USERPROFILE%\.local\bin\claude.exe is 2.1.199. The processes actually running and maintaining the credentials are the VS Code extension at %USERPROFILE%\.vscode\extensions\anthropic.claude-code-2.1.311-win32-x64\resources\native-binary\claude.exe (2.1.311, build 2026-10-26). Running `claude --version` can therefore report a stale version. That is harmless for the User-Agent, but you must not assume it is the binary that owns the token.
- [corrected] limits[].is_active meaning UNCONFIRMED; severity values other than 'normal' UNCONFIRMED.
  -> Both are now confirmed from Claude Code 2.1.311's own schema descriptions. is_active: 'The server's headline pick: the row a single-value indicator shows.' severity: 'The server's reading of the row for a meter's colour, e.g. 'normal', 'warning' or 'critical'. Every row here is the server's, so a client never grades a row itself.' kind: 'Classify a row on this, never on a label.' group: 'rows render grouped under it, in the server's order.' limits: 'Empty when the server reported no meters; null when the body carried no rows at all'. The schema is marked 'Experimental - the shape may change.'
- [corrected] Access token lifetime: ccusage says 'roughly hourly'; about 7.4 h remaining observed.
  -> 'Roughly hourly' (ccusage 0.1.10 README) is refuted for current accounts. Issue #31095 ('the access token's expiresAt field confirms an 8-hour lifetime'), #80585 ('access-token lifetime (~8h)') and the local file (about 7.3 h remaining) all indicate about 8 h. Always read expiresAt; never hard-code the lifetime.