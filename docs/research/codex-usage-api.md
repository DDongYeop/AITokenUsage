# codex

# Codex CLI (ChatGPT plan) usage limits: verified implementation facts

**Verification basis.** Every claim below was re-checked against source:
- `openai/codex` main at commit `04e365c698b94651b6ea7e3b3af5c1dd697bd30c` (2026-10-28), plus release tags `rust-v0.34.0`, `0.40`–`0.48`, `0.58`, `0.62`, `0.64`, `0.66`, `0.80`, `0.98`, `0.100`, `0.122` and `0.142.4`.
- `steipete/CodexBar` at commit `5f93dc3068b3bee3f8753f3772614cc2949c335d` (2026-10-26).

Nothing was tested against live data, because this machine has no `C:\Users\kyung\.codex`.

Abbreviations:
- **CX** = `https://github.com/openai/codex/blob/04e365c698b94651b6ea7e3b3af5c1dd697bd30c/codex-rs/`
- **CB** = `https://github.com/steipete/CodexBar/blob/5f93dc3068b3bee3f8753f3772614cc2949c335d/`

### Corrections versus the first draft (summary)
1. **`OPENAI_API_KEY` can be non-null on a ChatGPT login.** The login token-exchanges the id_token for an API key and stores it. Decide the mode from `auth_mode` and `tokens`, not from `OPENAI_API_KEY`.
2. **`originator` is NOT sent on `wham/usage`.** This is resolved from source. It is not needed.
3. **The `FileShare.ReadWrite | FileShare.Delete` rule is critical, not optional.** A FileShare.Read reader can break Codex's own token save.
4. **Never send `x-openai-codex-luna-reserve: 1`, and never set `supportsLunaReserve`.** Both are opt-ins for active clients only.
5. **Windows launch of `codex app-server` is now documented** (from `codex-cli/bin/codex.js`).
6. **Minor fixes:**
   - The `_<rolloutId>` filename form is not specific to "reverted" threads.
   - Compression age is based on the file's mtime.
   - The variant-C sample's provenance is unverified.
   - The 5-minute refresh margin is newer than v0.122.

---

## 1. Credentials

### 1.1 Location
- If env `CODEX_HOME` is set and non-empty, Codex uses it. It must be an existing directory, and Codex canonicalizes it. Otherwise the folder is `%USERPROFILE%\.codex` (`dirs::home_dir()` + `.codex`). Source: CX `utils/home-dir/src/lib.rs` L13-62.
- The file is `<CODEX_HOME>\auth.json`. The lock file is `<CODEX_HOME>\auth.lock`.
- `config.toml` (top level, profile or managed requirements) key `cli_auth_credentials_store` takes `"file"` (default, `config/defaults.toml`), `"keyring"`, `"auto"` or `"ephemeral"`.
  - In `keyring` and `auto` mode, Codex deletes `auth.json` after a successful keyring save.
  - On Windows the default keyring backend is `Secrets`: an encrypted local secrets file whose key lives in Windows Credential Manager. Treat it as unreadable.
  - The `Direct` backend uses service `"Codex Auth"` and account `"cli|"` + the first 16 hex characters of sha256(canonical CODEX_HOME).
  - **If `auth.json` is missing, fall back to the app-server (§3.5).**

### 1.2 Reading safely (Windows)
- **Writer behaviour (Codex):**
  - It takes `File::lock()` (LockFileEx, exclusive) on `auth.lock` (storage.rs L190-210).
  - It then rewrites `auth.json` **in place** with `truncate(true).write(true).create(true)` + `write_all` + `flush`. There is no temp file and no rename (storage.rs L256-268).
  - The lock covers only the read-modify-write. The network refresh happens outside it.
- **Rust std opens files with `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`.** A .NET reader using `FileShare.Read` (the default for `File.ReadAllText` / `File.OpenRead`) causes two problems:
  - The reader gets a sharing violation while Codex is writing.
  - **It makes Codex's save fail.** Codex might have just spent its refresh token, so the user could end up logged out.
- **Always read like this:**
  ```csharp
  using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
  using var sr = new StreamReader(fs); var json = sr.ReadToEnd();   // close immediately
  ```
- **Retries:** you may see an empty or partial file. Retry on JSON parse failure or a missing file up to 3 attempts, about 50 ms apart (as CodexBar does, CB `docs/codex-oauth.md`).
- You do not need to touch `auth.lock`. Readers are not blocked by it, because the lock is on a different file.

### 1.3 Exact `auth.json` schema
Sources: `AuthDotJson`, CX `login/src/auth/storage.rs` L49-73; `TokenData`, CX `login/src/token_data.rs` L10-34.

```jsonc
{
  "auth_mode": "chatgpt",        // optional; serde lowercase enum:
                                 // "apikey" | "chatgpt" | "chatgptAuthTokens" | "headers" | "agentIdentity" |
                                 // "personalAccessToken" | "bedrockApiKey" | "bedrockAccessKeys"
                                 // (all current logins write it; old files may lack it)
  "OPENAI_API_KEY": null,        // ALWAYS serialized. null OR a string. NOTE: a ChatGPT login also stores a
                                 // token-exchanged API key here when that exchange succeeds (login/src/server.rs L446-451, L874)
  "tokens": {                    // absent for API-key-only logins
    "id_token": "<JWT>",         // stored as the raw JWT string
    "access_token": "<JWT>",
    "refresh_token": "<opaque>",
    "oauth_provenance": { "issuer": "https://auth.openai.com", "client_id": "app_EMoamEEZ73f0CkXaXp7hrann" }, // optional (newer logins)
    "login_owner_id": "<random>",                 // optional (newer logins); preserved across rotation
    "account_id": "<chatgpt account/workspace id>"  // Option<String>; may be null/missing in old files
  },
  "last_refresh": "2026-10-27T09:12:34.123456789Z", // optional; chrono RFC3339 UTC (0/3/6/9 fraction digits)
  "agent_identity": "...",          // optional (string JWT or record)
  "personal_access_token": "...",   // optional
  "bedrock_api_key": {...},         // optional
  "bedrock_access_keys": {...}      // optional
}
```

**Mode resolution when `auth_mode` is absent** (`resolved_mode`, CX `login/src/auth/manager.rs` L1845-1862):
1. `personal_access_token` → PersonalAccessToken
2. `bedrock_api_key` → BedrockApiKey
3. `bedrock_access_keys` → BedrockAccessKeys
4. `OPENAI_API_KEY` non-null → ApiKey
5. otherwise → Chatgpt

`AuthMode::has_chatgpt_account()` is true only for `chatgpt`, `chatgptAuthTokens` and `personalAccessToken`.

**Widget rule:**
- If `auth_mode == "apikey"`, show "API key – no plan limits".
- Else, if `tokens.access_token` is non-empty, use the wham/usage path.
- Else (PAT, agent identity, or a missing file), use the app-server path.

**JWT claims** (base64url payload; Codex does not verify the signature):
- `https://api.openai.com/auth` holds:
  - `chatgpt_account_id`
  - `chatgpt_plan_type`
  - `chatgpt_user_id` (fallback `user_id`)
  - `chatgpt_account_is_fedramp` (bool)
  - `chatgpt_account_user_id`
- Email: top-level `email`, else `https://api.openai.com/profile`.`email`.
- `exp` (epoch seconds) on the access token drives refresh.

**Account id:**
- At login, `tokens.account_id` is copied from the id_token claim `https://api.openai.com/auth`.`chatgpt_account_id` (CX `login/src/server.rs` ~L860-870).
- Codex itself sends only `tokens.account_id` in the header.
- For old files where it is null, CodexBar falls back to these, trying the id_token first and then the access_token (CB `CodexOAuthCredentials.swift` L483-513):
  1. JWT top-level `chatgpt_account_id`
  2. `https://api.openai.com/auth`.`chatgpt_account_id`
  3. the first non-empty `organizations[].id`

---

## 2. Usage endpoint (what `/status` uses)

### 2.1 Call path
1. TUI `/status` and the background poll call `ClientRequest::GetAccountRateLimits` (`account/rateLimits/read`) (CX `tui/src/app/background_requests.rs` ~L823).
2. The app-server runs `BackendClient::from_auth(config.chatgpt_base_url, auth)` → `get_rate_limits_with_reset_credits()` (CX `app-server/src/request_processors/account_processor.rs` L1171-1275).
3. That issues `GET` on `rate_limit_status_url()` (CX `backend-client/src/client/rate_limit_resets.rs` L124-129):
   - `{base}/wham/usage` if the base contains `/backend-api`
   - else `{base}/api/codex/usage`

### 2.2 Base URL
Source: CX `backend-client/src/client.rs` L226-249; `model-provider/src/chatgpt_base_url.rs`.
- Default `https://chatgpt.com/backend-api/`.
- Override with the `config.toml` key `chatgpt_base_url` (top level or profile).
- Normalization:
  - Strip trailing `/`.
  - If the URL starts with `https://chatgpt.com` or `https://chat.openai.com` and lacks `/backend-api`, append `/backend-api`.
- FedRAMP base `https://gov.chatgpt.com/backend-api/` applies only for API-key auth flagged FedRAMP.

**Default: `GET https://chatgpt.com/backend-api/wham/usage`**

### 2.3 Headers
Sources: `Client::headers()` (client.rs L294-312), `BearerAuthProvider::add_auth_headers` (CX `model-provider/src/bearer_auth_provider.rs`), `auth_provider_from_auth` (CX `model-provider/src/auth.rs` L411-429).

| Header | Value | Notes |
|---|---|---|
| `Authorization` | `Bearer <tokens.access_token>` | required |
| `chatgpt-account-id` | `<tokens.account_id>` | Constant `CHATGPT_ACCOUNT_ID_HEADER = "chatgpt-account-id"` (CX `http-client/src/openai_headers.rs` L3). CodexBar spells it `ChatGPT-Account-Id`; headers are case-insensitive. Send it whenever you know it. Whether omitting it fails for personal plans is **UNCONFIRMED**. |
| `User-Agent` | Codex sends `codex_cli_rs/<ver> (<OS> <ver>; <arch>) <terminal>`, fallback literal `codex-cli` | CodexBar sends `CodexBar` and works. No specific UA appears to be required. |
| `Accept` | `application/json` | CodexBar sends it; harmless |
| `X-OpenAI-Fedramp` | `true` | only when the id_token claim `chatgpt_account_is_fedramp` is true |
| `originator` | **not sent by Codex on this request** | The backend client builds its pool with `HttpClientBuilder::new()` and no `default_headers()` (CX `http-client/src/route_aware_client_pool.rs` L424-435). Do not send it. |
| `x-openai-codex-luna-reserve` | **never send** | Opt-in "only for clients that can apply Reserve, not for passive account usage readers" (rate_limit_resets.rs L27-31, L76) |

### 2.4 Response schema
Sources: generated models in CX `codex-backend-openapi-models/src/models/` (`rate_limit_status_payload.rs`, `rate_limit_status_details.rs`, `rate_limit_window_snapshot.rs`, `credit_status_details.rs`, `additional_rate_limit_details.rs`, `spend_control_status_details.rs`, `spend_control_limit_details.rs`); wrapper `RateLimitStatusWithResetCredits`, CX `backend-client/src/types.rs` L54-75.

The example below is based on the app-server fixture (CX `app-server/tests/suite/v2/rate_limits.rs` L141-202) with 5h/weekly windows substituted. The Spark entry comes from CB `Tests/CodexBarTests/CodexAdditionalRateLimitsTests.swift`.

```jsonc
{
  "plan_type": "pro",               // REQUIRED string. Known: guest|free|go|plus|pro|prolite|promax|free_workspace|team|
                                    // self_serve_business_prolite|self_serve_business_usage_based|business|ent26|
                                    // enterprise_cbp_automation|enterprise_cbp_usage_based|education|quorum|k12|enterprise|
                                    // edu|edu_plus|edu_pro|sci|sci_plus (anything else -> "unknown"). Keep the raw string.
  "rate_limit": {                   // optional / nullable
    "allowed": true,                // bool (required inside)
    "limit_reached": false,         // bool (required inside)
    "primary_window": {             // optional / nullable
      "used_percent": 42,           // i32, 0-100 (parse as double to be lenient)
      "limit_window_seconds": 18000,// i32 seconds (18000 = 5h)
      "reset_after_seconds": 120,   // i32 seconds from response time (Codex ignores it)
      "reset_at": 1735693200        // i32 Unix epoch SECONDS
    },
    "secondary_window": { "used_percent": 5, "limit_window_seconds": 604800, "reset_after_seconds": 43200, "reset_at": 1736211600 }
  },
  "credits": {                      // optional / nullable
    "has_credits": true, "unlimited": false,
    "balance": "150.0",             // Option<String> in Codex; CodexBar also accepts a number
    "approx_local_messages": [], "approx_cloud_messages": []   // optional untyped arrays
  },
  "spend_control": {                // optional / nullable
    "reached": false,
    "individual_limit": { "source": "workspace_spend_controls", "limit": "25000", "used": "8000", "remaining": "17000",
                          "used_percent": 32, "remaining_percent": 68, "reset_after_seconds": 43200, "reset_at": 1736211600 }
  },
  "additional_rate_limits": [       // optional / nullable; model-specific quotas
    { "limit_name": "GPT-5.3-Codex-Spark", "metered_feature": "gpt_5_3_codex_spark", "normal_model_slug": null,
      "rate_limit": { "allowed": true, "limit_reached": false,
        "primary_window":   { "used_percent": 30,  "limit_window_seconds": 18000,  "reset_after_seconds": 0, "reset_at": 1766948068 },
        "secondary_window": { "used_percent": 100, "limit_window_seconds": 604800, "reset_after_seconds": 0, "reset_at": 1767407914 } } }
  ],
  "rate_limit_reached_type": { "type": "rate_limit_reached" },  // optional; type in rate_limit_reached |
        // workspace_owner_credits_depleted | workspace_member_credits_depleted |
        // workspace_owner_usage_limit_reached | workspace_member_usage_limit_reached | (other -> unknown)
  "rate_limit_reset_credits": { "available_count": 3 },          // optional
  "account_id": "account-123", "user_id": "user-123",            // optional
  "rate_limit_upsell": { }                                        // optional opaque banner JSON
}
```

**Units and mapping** (confirmed: client.rs L770-850; protocol.rs `RateLimitWindow` doc "Percentage (0-100)", "Unix timestamp (seconds since epoch)"):
- `used_percent` is a 0-100 percentage. In this endpoint it is an integer.
- `reset_at` is in epoch seconds.
- Window minutes = `(limit_window_seconds + 59) / 60`. A value ≤ 0 becomes null. So 18000 → 300 and 604800 → 10080.
- Codex picks the snapshot with `limit_id == "codex"` (the top-level `rate_limit`). Additional limits become snapshots with `limit_id = metered_feature` and `limit_name = limit_name`.

**Observed shapes:**
- Plus/Pro: primary 18000 s, secondary 604800 s.
- **Free (observed by CodexBar, not guaranteed):** `{"plan_type":"free","rate_limit":{"primary_window":{"used_percent":11,"reset_at":...,"limit_window_seconds":2592000},"secondary_window":null}}`. That is a single 30-day window in PRIMARY (CB `Tests/CodexBarTests/ProviderPaceCapabilityTests.swift` L128-150; CB `docs/codex.md` L56-59).
- Additional limits can carry a weekly-only window in `primary_window`.

**HTTP errors:**
- 401 means the token is invalid or expired.
- CodexBar treats 403 as a terminal permission error (no CLI recovery).
- Error-body shapes are **UNCONFIRMED**.
- Codex uses a chatgpt.com Cloudflare cookie store. A plain client works for CodexBar, but Cloudflare challenges (HTML 403) are **UNCONFIRMED**, so treat a non-JSON body as a transient failure.

**Poll cadence:**
- The Codex TUI polls every 60 s, then 30 s at ≥75% used, 15 s at ≥90% and 5 s at ≥99% (CX `tui/src/chatwidget/rate_limits.rs` L195-221).
- 60-300 s is reasonable for a widget.

---

## 3. Token refresh (and why the widget must not do it)

### 3.1 When Codex refreshes
Source: CX `login/src/auth/manager.rs` L221-222, L2499-2513, L3148-3192, L3333-3351.
- **Proactive:** when the access_token JWT `exp` ≤ now + 5 min (`CHATGPT_ACCESS_TOKEN_REFRESH_WINDOW_MINUTES = 5`). If `exp` cannot be parsed, it refreshes when `last_refresh` is more than 8 days old (`TOKEN_REFRESH_INTERVAL = 8`). This runs inside `AuthManager::auth()`.
- **Guarded reload first:** before refreshing, Codex reloads `auth.json` if the account id matches. If another process already rotated the tokens, it skips the refresh.
- **On 401:** `UnauthorizedRecovery` runs (1) reload from disk, then (2) an OAuth refresh.
- **History:**
  - v0.46: `last_refresh` > 28 days
  - v0.80 and v0.122: > 8 days
  - v0.122: refreshes when `exp <= now` (no margin)
  - current main: 5-minute margin

### 3.2 Request (for reference only; do not use)
Sources: `request_chatgpt_token_refresh`, manager.rs L1705-1756; `OAuthClient::refresh`, CX `login/src/oauth/client.rs` L97-112.
- `POST https://auth.openai.com/oauth/token`.
  - Endpoint precedence: env `CODEX_REFRESH_TOKEN_URL_OVERRIDE`, then `{oauth_provenance.issuer}/oauth/token`, then the default.
- `Content-Type: application/json`. Body:
  ```json
  {"client_id":"app_EMoamEEZ73f0CkXaXp7hrann","grant_type":"refresh_token","refresh_token":"<tokens.refresh_token>","refresh_reason":"proactive_expiry"}
  ```
  - `client_id` precedence: `oauth_provenance.client_id`, then env `CODEX_APP_SERVER_LOGIN_CLIENT_ID`, then `CLIENT_ID`.
  - `refresh_reason` is one of `proactive_expiry` | `scheduled` | `unauthorized_recovery` | `forced`.
- Response fields read: `id_token`, `access_token`, `refresh_token`, all optional. Whatever is returned replaces the stored value.
- Persist (`persist_tokens`, L1658-1700):
  1. Lock `auth.lock`.
  2. Re-read the file.
  3. Require `resolved_mode == Chatgpt` and the same login.
  4. Replace the tokens and set `last_refresh = now`.
  5. Save. Unknown JSON fields are dropped.

### 3.3 Rotation and failures
- Reusing a spent refresh token returns `{"error":{"message":"Your refresh token has already been used to generate a new access token. Please try signing in again.","type":"invalid_request_error","param":null,"code":"refresh_token_reused"}}` (CX `login/src/auth/util.rs` L22-38).
- Permanent codes:
  - `refresh_token_expired`
  - `refresh_token_reused`
  - `refresh_token_invalidated`
  - 400 `invalid_grant`
  - any 401
- After a permanent failure Codex does not retry, and the user must run `codex login`.
- OpenAI's CI/CD auth doc (learn.chatgpt.com/docs/auth/ci-cd-auth):
  - "Do not share the same file across concurrent jobs or multiple machines."
  - Reseed when "another machine or concurrent job rotated the token first".
  - "after a successful refresh, Codex writes the new tokens and a new last_refresh back to auth.json".
- **Access-token lifetime for the Codex CLI client: UNCONFIRMED.** No constant exists in the source. OpenAI's separate Sign-in-with-ChatGPT program documents 1 h access tokens and 30-day rotating refresh tokens, but that is a different OAuth program. Always decode `exp`.

### 3.4 Is it safe for a third-party reader to refresh?
**No.**
- A refresh spends the refresh token. If the widget does not write the new tokens back, the CLI's next refresh fails with `refresh_token_reused` and the user is logged out.
- Writing back "like Codex" is fragile for four reasons:
  - Codex's own network refresh is not under the file lock.
  - Codex drops unknown fields.
  - It checks `login_owner_id`.
  - It races with other Codex processes (the CLI, the VS Code extension, the app-server).
- **What the widget may do on 401:** re-read `auth.json`. If `access_token` changed (Codex refreshed meanwhile), retry once. This mirrors Codex's "reload" step and spends nothing.

### 3.5 What CodexBar does, and the safe fallback: the Codex app-server
Sources: CB `docs/codex-oauth.md`; CB `Sources/CodexBarCore/UsageFetcher.swift` L955-1035.
- CodexBar never refreshes and never writes `auth.json`.
- **Fresh token** (exp > now + 5 min, or exp unknown and `last_refresh` < 8 days): it calls `wham/usage` directly.
- **Stale native token:** it spawns `codex -s read-only -a never app-server` and calls JSON-RPC `account/rateLimits/read`.
  - The app-server's `AuthManager::auth()` refreshes proactively, persists `auth.json` under the lock, and returns the limits (manager.rs L2499-2521; account_processor.rs L1171-1275).
  - CodexBar's timeouts are 8 s for `initialize` and 3 s per request.

**JSON-RPC wire format.** Newline-delimited JSON on stdin/stdout. There is **no** `"jsonrpc"` field (CX `app-server-protocol/src/rpc.rs` L1-56):
- request = `{id, method, params?}`
- notification = `{method, params?}`
- response = `{id, result}`
- error = `{id, error:{code, message, data?}}`

Skip any line without your `id`; those are notifications.

```text
-> {"id":1,"method":"initialize","params":{"clientInfo":{"name":"ai-usage-widget","title":null,"version":"1.0.0"},"capabilities":null}}
<- {"id":1,"result":{...}}
-> {"method":"initialized","params":{}}
-> {"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}
<- {"id":2,"result":{
     "rateLimits": {"limitId":"codex","limitName":null,"normalModelSlug":null,
                    "primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1735693200},
                    "secondary":{"usedPercent":5,"windowDurationMins":10080,"resetsAt":1736211600},
                    "credits":{"hasCredits":true,"unlimited":false,"balance":"150.0"},
                    "individualLimit":null,"spendControlReached":null,"planType":"pro","rateLimitReachedType":null},
     "rateLimitsByLimitId": {"codex": { ...same shape... }, "gpt_5_3_codex_spark": { ... }},
     "ordinaryUsageAllowed": true,
     "rateLimitResetCredits": {"availableCount": 0, "credits": null},
     "accountId": "...", "rateLimitUpsell": null }}
```

- **TS schemas** (CX `app-server-protocol/schema/typescript/`):
  - `v2/GetAccountRateLimitsResponse.ts`
  - `v2/RateLimitSnapshot.ts`
  - `v2/RateLimitWindow.ts` = `{usedPercent: number, windowDurationMins: number|null, resetsAt: number|null}`
  - `v2/CreditsSnapshot.ts`
  - `v2/GetAccountRateLimitsParams.ts` = `{supportsLunaReserve?, excludeResetCreditDetails?}`. **Do not set `supportsLunaReserve`.**
  - `InitializeParams.ts` = `{clientInfo:{name, title|null, version}, capabilities|null}`. CodexBar sends only `clientInfo{name, version}`, which works.
- **Errors you may get:**
  - `"codex account authentication required to read rate limits"`
  - `"chatgpt authentication required to read rate limits"`
  - `"failed to fetch codex rate limits: ..."`
- **Launching on Windows** (CX `../codex-cli/bin/codex.js` L17-112):
  - An npm install puts `codex.cmd` on PATH (typically `%APPDATA%\npm`). It runs `node .../@openai/codex/bin/codex.js`, which spawns the native binary `<@openai/codex-win32-x64 package>\vendor\x86_64-pc-windows-msvc\bin\codex.exe` (or the `aarch64-pc-windows-msvc` / `@openai/codex-win32-arm64` equivalent). It falls back to `@openai\codex\vendor\...`.
  - The shim also sets env `CODEX_MANAGED_BY_NPM=1` and `CODEX_MANAGED_PACKAGE_ROOT`.
  - From .NET, either run `cmd.exe /d /c codex -s read-only -a never app-server` with redirected stdio, or resolve `codex.exe` and start it directly.
  - Non-npm installs are **UNCONFIRMED**.
  - Close stdin and kill the process on timeout. Keep the process short-lived, because it is a full Codex process.

### 3.6 Recommended widget strategy
1. Resolve CODEX_HOME. Read `auth.json` with `FileShare.ReadWrite | FileShare.Delete`, retrying up to 3 times about 50 ms apart.
2. If `auth_mode == "apikey"`, show "API key – no plan limits". If `tokens.access_token` is missing (PAT, agent identity, keyring storage, or a missing file), go to step 5.
3. Freshness:
   - Decode the access_token `exp`. Fresh means `exp > now + 5 min`.
   - If `exp` is unknown, fresh means `last_refresh` is under 8 days old.
   - If fresh, call `GET <base>/wham/usage` with `Authorization`, `chatgpt-account-id` (when known) and a UA.
4. On 401: re-read `auth.json`. If `access_token` changed, retry once; otherwise go to step 5.
5. If the token is stale, or step 4 failed, run the app-server `account/rateLimits/read` when `codex` is installed. Otherwise use the rollout-log fallback (§4) and mark the reading "stale – run codex".
6. Never POST to `/oauth/token`, and never write `auth.json`.

---

## 4. Offline fallback: rollout session logs

### 4.1 Files
Sources: CX `rollout/src/recorder.rs` L1770-1792, `rollout/src/rollout_file_name.rs` L62-72, `rollout/src/lib.rs` L86-87, `rollout/src/compression.rs` L369-377.
- Path: `%CODEX_HOME%\sessions\YYYY\MM\DD\rollout-YYYY-MM-DDThh-mm-ss-<threadId>.jsonl`. The date folders and the filename timestamp use **local time**.
- When the rollout id differs from the thread id, the name is `rollout-<ts>-<threadId>_<rolloutId>.jsonl`.
- `archived_sessions\` also exists.
- A background worker compresses rollouts **not modified for 7 days** to `*.jsonl.zst` (zstd). Recent files stay plain `.jsonl`.
- **Approach:**
  1. Enumerate `sessions\**\rollout-*.jsonl` (ignore `.zst`).
  2. Sort by `LastWriteTimeUtc` descending.
  3. Read each file with `FileShare.ReadWrite`, scanning from the end for the last `token_count` line with a usable `rate_limits`.
  4. Fall through to older files if needed.

### 4.2 Line envelope
Sources: CX `history/src/lib.rs` L370-381 (`RolloutLine`, flattened item); `history/src/rollout_payload.rs` (`#[serde(tag="type", rename_all="snake_case")]`, `EventMsg { payload }`); `EventMsg` tagged `"type"` snake_case, `TokenCount(TokenCountEvent)` → `"token_count"` (CX `protocol/src/protocol.rs` L1514-1581).

```json
{"timestamp":"2025-12-17T07:50:42.053Z","ordinal":123,"type":"event_msg","payload":{"type":"token_count","info":{...}|null,"rate_limits":{...}|null}}
```
- `timestamp` is UTC with exactly 3 fractional digits plus `Z`.
- `ordinal` is optional (newer files).
- `info` and `rate_limits` keys are always present, but either may be null.
- `TokenCount` is persisted (CX `rollout/src/policy.rs` L134; also in v0.40).

### 4.3 `rate_limits` variants (verified at release tags)
**A. v0.40.0** (`RateLimitSnapshotEvent`, no reset info). v0.34.0 has no `rate_limits`.
```json
"rate_limits":{"primary_used_percent":12.0,"secondary_used_percent":30.0,"primary_to_secondary_ratio_percent":3.0,"primary_window_minutes":299,"secondary_window_minutes":10079}
```
Values are illustrative; the field names are from source.

**B. v0.41.0 to v0.47.0.** `resets_in_seconds` (u64) is relative to the event time, so reset ≈ line `timestamp` + `resets_in_seconds`.
```json
"rate_limits":{"primary":{"used_percent":0.0,"window_minutes":299,"resets_in_seconds":17940},"secondary":{"used_percent":22.0,"window_minutes":10079,"resets_in_seconds":351406}}
```
Real sample dated 2025-09-27 (xiangz19/codex-ratelimit README), verified.

**C. v0.48.0 onward.** `resets_at` is in epoch seconds.
- `credits` added by v0.62 (absent at v0.58).
- `plan_type` added by v0.66 (absent at v0.64).

```json
"rate_limits":{"primary":{"used_percent":0.0,"window_minutes":300,"resets_at":1765975828},"secondary":{"used_percent":0.0,"window_minutes":10080,"resets_at":1766562628},"credits":{"has_credits":false,"unlimited":false,"balance":null},"plan_type":null}
```
The shape matches the schema, but this sample's provenance is unverified.

**D. Current main** (CX `protocol/src/protocol.rs` L2573-2647):
- `limit_id`/`limit_name` added at v0.100 (absent at v0.98).
- `rate_limit_reached_type` at v0.122.
- `individual_limit` at v0.142.4.
- `spend_control_reached` and `normal_model_slug` after v0.142.4.

```json
"rate_limits":{"limit_id":"codex","limit_name":null,
  "primary":{"used_percent":42.0,"window_minutes":300,"resets_at":1735693200},
  "secondary":{"used_percent":5.0,"window_minutes":10080,"resets_at":1736211600},
  "credits":{"has_credits":true,"unlimited":false,"balance":"150.0"},
  "individual_limit":null,"spend_control_reached":null,"plan_type":"pro","rate_limit_reached_type":null}
```
- `normal_model_slug` is omitted when null.
- `individual_limit` = `{limit: string, used: string, remaining_percent: int, resets_at: int}`.
- `plan_type` uses lowercase account values (`free`, `go`, `plus`, `pro`, `prolite`, `promax`, `team`, `business`, `enterprise`, `edu`, ...; unknown → `"unknown"`).

**Parsing notes:**
- `used_percent` is f64 (0-100).
- `window_minutes` is nullable.
  - Current values come from response headers `x-codex-primary-window-minutes`, `x-codex-primary-reset-at` and `x-codex-primary-used-percent` (and `secondary` equivalents), or from SSE `codex.rate_limits` events (CX `codex-api/src/rate_limits.rs`).
  - Expect 299/300 and 10079/10080.
- A window is **omitted** when `used_percent == 0`, there is no window, and there is no reset (`has_data`, rate_limits.rs L207). A missing window in a recent snapshot most likely means 0% used.
- **Filter by `limit_id`.** The session keeps a single latest snapshot that any limit family can overwrite (`set_rate_limits` / `merge_rate_limit_fields`, CX `core/src/state/session.rs` L365-473).
  - Accept only `limit_id == "codex"` or `limit_id` absent/null (v0.41-v0.98 files).
  - Skip other ids, e.g. `codex_other` or a normalized metered feature like `gpt_5_3_codex_spark`.
  - Current Codex always writes a non-null `limit_id`.
- **Staleness:**
  - If `resets_at` < now, show 0% ("reset since last seen").
  - Always show the line's `timestamp` as "as of".

---

## 5. Which window is 5h and which is weekly

- **Primary = 5h and secondary = weekly is typical but NOT guaranteed.**
  - Free can return a lone 30-day primary.
  - Additional limits can put a weekly window in primary.
  - Old logs use 299/10079.
- **Codex's labeling** (CX `tui/src/chatwidget/rate_limits.rs` L103-145): `get_limits_duration(minutes)` with ±5% tolerance (`m >= e*0.95 && m <= e*1.05`).
  - 300 → `"5h"`
  - 1440 → `"daily"`
  - 10080 → `"weekly"`
  - 43200 → `"monthly"`
  - 525600 → `"annual"`
  - otherwise `"usage"` (primary) or `"secondary usage"` (secondary)
- The `/status` rows read `"5h limit"` and `"Weekly limit"` and display **"% left" = 100 − used_percent**.
- **CodexBar** (CB `Sources/CodexBarCore/Providers/Codex/CodexRateWindowNormalizer.swift`):
  - session if `windowMinutes == 300` exactly, weekly if `10080` exactly
  - swaps when primary is weekly
  - a lone weekly window goes to the weekly lane; a lone session/unknown window goes to the session lane

```csharp
static long? MinutesFromSeconds(long s) => s <= 0 ? null : (s + 59) / 60;   // wham/usage limit_window_seconds
static string Label(long? minutes, bool isSecondary) {
    if (minutes is long m) {
        bool Near(long e) => m >= e * 0.95 && m <= e * 1.05;
        if (Near(300))    return "5h";
        if (Near(1440))   return "Daily";
        if (Near(10080))  return "Weekly";
        if (Near(43200))  return "Monthly";
        if (Near(525600)) return "Annual";
    }
    return isSecondary ? "Secondary" : "Usage";
}
// Assign display slots by label, not position: session = "5h", weekly = "Weekly".
// A single window (e.g. Free "Monthly") is shown alone with its own label.
// Sources of minutes: wham/usage -> MinutesFromSeconds(limit_window_seconds);
// rollout B/C/D -> window_minutes; rollout A -> primary_window_minutes / secondary_window_minutes;
// app-server -> windowDurationMins.
// Reset time: wham reset_at (epoch s); rollout C/D resets_at (epoch s);
// rollout B timestamp + resets_in_seconds; rollout A none; app-server resetsAt (epoch s).
```

---

## Remaining UNCONFIRMED items
- The Codex CLI access-token lifetime. Decode `exp - iat` from a real token to measure it.
- Whether `wham/usage` fails without `chatgpt-account-id` on personal plans. Always send it when known.
- Error-body shapes for 401/403/429 from `wham/usage`, and whether Cloudflare ever challenges a plain HttpClient.
- How non-npm Windows installs lay out the `codex.exe` path.
- How to call `wham/usage` directly with `personalAccessToken` or `agentIdentity` auth. Use the app-server instead.
- How often a non-`codex` `limit_id` overwrites the latest `token_count` snapshot in real logs.


## Verifier: missing_but_important
- OPENAI_API_KEY can be a non-null string even after a ChatGPT login, because Codex token-exchanges the id_token for an API key and stores it (server.rs L446-451, L874). Do not treat a non-null OPENAI_API_KEY as meaning 'API-key user'. Use auth_mode, and try wham/usage whenever tokens.access_token exists and auth_mode != 'apikey'.
- The FileShare setting is critical, not cosmetic. Rust std opens auth.json with FILE_SHARE_READ|WRITE|DELETE. A .NET reader holding FileShare.Read (the default for File.ReadAllText/OpenRead) makes Codex's in-place save fail with a sharing violation. That can happen right after Codex spent its refresh token, which logs the user out. Always use new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), read everything, and close at once.
- Do NOT send the request header 'x-openai-codex-luna-reserve: 1' to wham/usage. In app-server, do not set supportsLunaReserve. Codex documents both as an opt-in that lets the backend record experiment exposure; it is 'not for passive account usage readers' (rate_limit_resets.rs L27-31, L76).
- Codex's file lock covers only the persist step, not the network refresh. Codex's own processes can race on a refresh. This makes any third-party refresh even riskier.
- Safe 401 handling for the widget: re-read auth.json once (Codex may have refreshed already), and retry wham/usage only if the access_token changed. This mirrors Codex's 'Reload' recovery step and spends no refresh token. Never POST to /oauth/token.
- The TUI /status shows '% left' (= 100 - used_percent). Decide explicitly whether the widget shows used or remaining.
- Rollout windows can be absent. codex-api drops a window when used_percent == 0, window_minutes is 0 or absent, and there is no reset (has_data check, rate_limits.rs L207). A missing primary or secondary in a recent snapshot most likely means 0% used, not an error.
- In current rollouts limit_id is never null: merge_rate_limit_fields sets 'codex' when it is missing, and header snapshots always carry an id. limit_id is absent in files from v0.41 to v0.98. Model-specific families appear as other ids, e.g. 'codex_other' or a normalized metered feature such as 'gpt_5_3_codex_spark'.
- App-server stdout interleaves notifications (no 'id') with responses. Read lines until one carries the matching id. A JSON-RPC error has the shape {id, error:{code, message, data?}}. initialize params may omit 'title' and 'capabilities' (CodexBar sends only clientInfo{name, version} and it works). The method may fail with messages such as 'codex account authentication required to read rate limits' or 'chatgpt authentication required to read rate limits'.
- Spawning the app-server is how stale tokens get refreshed safely. It also starts a full Codex process (e.g. background rollout compression). Keep it short-lived: initialize, read, close stdin, kill on timeout (CodexBar: 8 s for initialize, 3 s per request).
- Plan types: wham/usage may return values the app-server maps differently ('education' becomes 'edu'; guest, free_workspace, quorum and k12 become 'unknown'). Keep the raw plan_type string for display.
- Codex keeps a chatgpt.com Cloudflare cookie store for backend requests. CodexBar's plain URLSession calls work without one. Whether a plain .NET HttpClient call ever gets an HTML Cloudflare challenge (403 with a non-JSON body) is UNCONFIRMED, so treat a non-JSON body as a transient error.
- The FedRAMP base URL (gov.chatgpt.com) applies only to API-key auth. ChatGPT FedRAMP accounts use the normal base URL plus the X-OpenAI-Fedramp: true header, sent when the id_token claim https://api.openai.com/auth.chatgpt_account_is_fedramp is true.
- Refresh history: v0.46 used last_refresh > 28 days. v0.80 and v0.122 used 8 days. v0.122 refreshed when exp <= now. Current main refreshes when exp <= now + 5 min. Very old CLIs may leave tokens that look stale but still work.
- ChatGPT-plan users with auth_mode 'personalAccessToken' or 'agentIdentity' do not store a usable OAuth access_token in tokens. How to call wham/usage directly for them is UNCONFIRMED; use the app-server path.

## Verifier: checked claims (non-confirmed only)
- [corrected] OPENAI_API_KEY is null for a ChatGPT login and a string for an API-key login
  -> After a ChatGPT browser login, Codex also runs a token exchange (grant_type urn:ietf:params:oauth:grant-type:token-exchange, requested_token=openai-api-key) and, if it succeeds, stores the result in OPENAI_API_KEY next to the tokens. So OPENAI_API_KEY can be a non-null string on a ChatGPT login. Decide the mode from auth_mode first. If auth_mode is missing, current Codex resolves OPENAI_API_KEY != null to ApiKey. A widget should still try wham/usage whenever tokens.access_token is present and auth_mode is not 'apikey'.
- [corrected] Whether the 'originator: codex_cli_rs' header reaches the wham/usage request is UNCONFIRMED
  -> Resolved from source: it is NOT sent. The backend client's pool is built with HttpClientBuilder::new().with_chatgpt_cloudflare_cookie_store().without_request_logging() and no .default_headers(default_headers()). The originator appears only as the User-Agent prefix 'codex_cli_rs/<ver> (...)'. Not needed.
- [could-not-verify] Variant C sample 'real sample from 2025-12-17 (cli 0.63-era)' with window_minutes 300/10080, credits and plan_type:null
  -> The shape matches the v0.66 schema, but no source for this sample was found. Its provenance and the '0.63-era' label are unverified. Do not rely on servers sending exactly 300/10080; match with tolerance.
- [corrected] Rollout path is %CODEX_HOME%\sessions\YYYY\MM\DD\rollout-YYYY-MM-DDThh-mm-ss-<uuid>.jsonl in local time; '_<rolloutId>' suffix for a 'reverted thread'; archived_sessions also exists
  -> The path and the local-time naming are confirmed. The '<threadId>_<rolloutId>' form is used whenever the rollout id differs from the thread id. The source does not tie it specifically to 'reverted' threads.
- [could-not-verify] Codex CLI access-token lifetime: no constant in source (codex reads JWT exp); the 1-hour figure for Sign-in-with-ChatGPT is not confirmed to apply
  -> Confirmed that the SIWC page states 1 h access tokens and 30-day rotating refresh tokens, but that is a different OAuth program. The lifetime for the Codex CLI client remains UNCONFIRMED.
- [corrected] How to launch `codex app-server` on Windows is UNCONFIRMED
  -> Partly resolved from codex-cli/bin/codex.js. An npm install gives a codex.cmd shim that runs node bin/codex.js, which then spawns the native binary <platform-pkg>/vendor/x86_64-pc-windows-msvc/bin/codex.exe (package @openai/codex-win32-x64, or the aarch64 equivalent; falls back to @openai/codex/vendor/...). codex.js also sets CODEX_MANAGED_BY_NPM=1 and CODEX_MANAGED_PACKAGE_ROOT. From .NET, either run 'cmd.exe /d /c codex -s read-only -a never app-server' or resolve codex.exe directly. Behaviour of non-npm installs is UNCONFIRMED.