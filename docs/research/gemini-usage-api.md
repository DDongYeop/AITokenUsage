# gemini

# Gemini CLI quota and usage: verified notes for a Windows WPF widget

## Sources

Everything below was fetched and checked on 2026-10-28.

**Primary sources**
- **gemini-cli** `main` @ `b8fa817` (2026-10-28), from the full tarball.
- **`@google/gemini-cli` 0.66.0** on npm, the published bundle.
- **google-auth-library 10.9.0**, the exact version gemini-cli pins.
- **github/node-keytar**, the source of `@github/keytar` 7.10.6 that gemini-cli uses.
- **Google pages:**
  - Code Assist for individuals deprecation (updated 2026-10-09)
  - Code Assist sunset (2026-10-26)
  - Code Assist quotas (2026-10-26)
  - Gemini API rate limits (2026-10-26)
  - OAuth 2.0 overview and native-app pages (2026-10-16)
  - Gemini Apps help answer 17004136

**Secondary source**
- **CodexBar** `main` @ `5f93dc3` (2026-10-26): its Gemini provider, tests and docs.

**Markers used below**
- **[SRC]**: confirmed in primary source.
- **[CB]**: CodexBar behaviour. This shows what a working monitor does, but it is not Google-documented.
- **UNCONFIRMED**: not established by any source.

---

## 0. Most important: consumer "Login with Google" was shut down on 2026-06-18

**What Google's deprecation page says [SRC]**
- *"Starting June 18, 2026, Gemini Code Assist IDE extensions stopped serving requests for the Gemini Code Assist for individuals, Google AI Pro, and Google AI Ultra tiers ... you can no longer use the Login with Google option to access the IDE extensions or Gemini CLI."*
- Google recommends "the Antigravity family of products" and links an Antigravity CLI migration guide.
  - The CLI binary is `agy`, but that name comes from CodexBar and third parties. It is not on Google's page.

**Standard and Enterprise are not covered by that shutdown.** They have their own sunset [SRC]:
- No new purchases from 2026-10-09.
- Licenses can still be added until 2027-01-31.
- Existing subscriptions keep working until the contract ends.
- Annual plans can auto-renew until 2027-01-31; monthly plans until 2027-12-31.
- Antigravity is included in Gemini Enterprise editions.
- Neither page says how long Standard/Enterprise users can keep using Gemini CLI itself.

**Workspace and education accounts:** CodexBar says they are outside the consumer shutdown. Google's pages do not mention them, so this is **UNCONFIRMED**.

**What consumer accounts now get back** [CB, from CodexBar's fixtures, which it labels as mirroring real responses; no live call checked]:
- `loadCodeAssist` returns **HTTP 200** with no `currentTier` and an `ineligibleTiers[]` entry with `"reasonCode":"UNSUPPORTED_CLIENT"`.
- `retrieveUserQuota` returns **HTTP 403** with `error.details[].reason == "SUBSCRIPTION_REQUIRED"`.
- Note: `UNSUPPORTED_CLIENT` is not in gemini-cli's own `IneligibleTierReasonCode` enum. Treat `reasonCode` as a free-form string.

**What to build:**
- Build the Gemini CLI provider for Standard/Enterprise and possibly Workspace users, and detect the shutdown cleanly (section 2.5).
- AI Pro and AI Ultra users need a separate **Antigravity** provider. It has 5-hour and weekly windows and uses a different OAuth client; see section 7.

---

## 1. Credential files and settings

### 1.1 Locations [SRC]
These come from `packages/core/src/utils/paths.ts` and `packages/core/src/config/storage.ts`.

- **Home directory:** `homedir()` returns `GEMINI_CLI_HOME` if it is non-empty, otherwise `os.homedir()`, which is `%USERPROFILE%` on Windows.
  - `GEMINI_CLI_HOME` replaces the **home** directory. It does not point at the `.gemini` folder itself.
- **`GEMINI_DIR`** is `'.gemini'`.
- **OAuth credentials:** `<home>\.gemini\oauth_creds.json` (`OAUTH_FILE`).
- **User settings:** `<home>\.gemini\settings.json`.
- **System settings on Windows:** `C:\ProgramData\gemini-cli\settings.json`, overridable with `GEMINI_CLI_SYSTEM_SETTINGS_PATH`.
- **Accounts cache:** `<home>\.gemini\google_accounts.json`.
  - It lives in `getGlobalRuntimeDir()`, which is `<home>\.cache\.gemini` only when `SANDBOX=sandbox-exec` (macOS).
- **`.env` files** (`findEnvFile`, `packages/cli/src/config/settings.ts`):
  1. From the current directory upward, the CLI looks for `.gemini\.env`, then `.env`. The `.gemini\.env` files are used **only in trusted folders**.
  2. It then falls back to `<home>\.gemini\.env` (trusted only), then `<home>\.env`.
  - Values already in the process environment are **never** overwritten by `.env` values.
  - In untrusted folders, only `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `GOOGLE_CLOUD_PROJECT` and `GOOGLE_CLOUD_LOCATION` are loaded.
  - **Widget approximation:**
    1. process environment;
    2. `%USERPROFILE%\.gemini\.env`;
    3. `%USERPROFILE%\.env`.

### 1.2 `oauth_creds.json` [SRC]
The file is google-auth-library's `Credentials` object, serialized with `JSON.stringify(obj, null, 2)`. Fields:
- `access_token` (string)
- `refresh_token` (string; present because the CLI uses `access_type: 'offline'`)
- `expiry_date` (number, **epoch milliseconds**)
- `token_type` (`"Bearer"`)
- `id_token` (JWT)
- `scope` (space-delimited string)

`expires_in` is converted to `expiry_date` and deleted before the file is written.

**How `cacheCredentials()` writes the file:**
1. It reads the existing file, with up to 3 retries.
2. It drops null and undefined values from the new credentials.
3. It merges `{...existing, ...new}`.
4. It writes `.oauth_creds.json.<pid>.<ms>.<n>.tmp` with mode 0600.
5. It renames that file over `oauth_creds.json`. If the rename fails, it writes the file directly.

**Gaps:**
- gemini-cli code never references `id_token` or `openid`. Whether `id_token` is present depends on Google's token response; CodexBar expects it.
- Whether `scope` contains `openid` is **UNCONFIRMED** (cosmetic).

Example (structure from source, values made up):
```json
{
  "access_token": "ya29.a0AfB_...",
  "scope": "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile",
  "token_type": "Bearer",
  "id_token": "eyJhbGciOiJSUzI1NiIs...",
  "expiry_date": 1761654321000,
  "refresh_token": "1//0gAbC..."
}
```

- **Scopes requested** (`OAUTH_SCOPE`): `https://www.googleapis.com/auth/cloud-platform`, `.../auth/userinfo.email`, `.../auth/userinfo.profile`.
- **`id_token` claims:** `email`, plus `hd` for Workspace accounts [CB].
- **Parsing `expiry_date`:** read it as a **double**. CodexBar writes it back as a Double.

### 1.3 Other credential stores
**A. Keychain or encrypted mode [SRC]**

This mode is used when `GEMINI_FORCE_ENCRYPTED_FILE_STORAGE=true`.
- The CLI uses `HybridTokenStorage("gemini-cli-oauth")` with the account key `"main-account"`.
- On first load, it **migrates `oauth_creds.json` into storage and deletes the file**.
- The value it stores is a JSON string. There is **no `id_token`** in this mode:
  ```json
  {"serverName":"main-account","token":{"accessToken":"ya29...","refreshToken":"1//...","tokenType":"Bearer","scope":"...","expiresAt":1761654321000},"updatedAt":1761650000000}
  ```

**Windows Credential Manager backend** (`@github/keytar`, `keytar_win.cc`; **confirmed**):

| Property | Value |
|---|---|
| Type | `CRED_TYPE_GENERIC` (= 1) |
| `TargetName` | `gemini-cli-oauth/main-account` (service + `'/'` + account) |
| `UserName` | `main-account` |
| `CredentialBlob` | raw **UTF-8** bytes of the JSON above |
| `Persist` | `CRED_PERSIST_ENTERPRISE` |

**Encrypted-file backend.** Used when keytar fails to load, when `GEMINI_FORCE_FILE_STORAGE=true`, or under WSL.
- **File:** `<home>\.gemini\gemini-credentials.json`.
- **Format:** hex `iv:authTag:ciphertext`, AES-256-GCM, 12-byte IV, 16-byte tag.
- **Key:** `scryptSync('gemini-cli-oauth', '<os.hostname()>-<os.userInfo().username>-gemini-cli', 32)`, using Node's scrypt defaults.
- **Plaintext:** `{"gemini-cli-oauth":{"main-account":"<JSON string above>"}}`.
- **Recommendation:** v1 can skip this backend. .NET has no built-in scrypt.

A WPF app started from Explorer usually cannot see `GEMINI_FORCE_ENCRYPTED_FILE_STORAGE`. Detect the store by presence instead:
1. `oauth_creds.json`;
2. else the Credential Manager entry `gemini-cli-oauth/main-account`;
3. else the encrypted file (unsupported in v1).

**B. Access-token override [SRC].** `GOOGLE_GENAI_USE_GCA` (any truthy value) plus `GOOGLE_CLOUD_ACCESS_TOKEN` supply an access token directly.

**C. ADC fallback [SRC].** The file named by `GOOGLE_APPLICATION_CREDENTIALS` is also tried.

### 1.4 `settings.json` auth selection [SRC]
**The file format is JSONC.** It is parsed with `strip-json-comments` and may contain `$VAR` references. In C#, use `JsonCommentHandling.Skip` and `AllowTrailingCommas = true`.

- **Path:** `security.auth.selectedType`.
- **Values** (`AuthType` enum): `"oauth-personal"`, `"gemini-api-key"`, `"vertex-ai"`, `"cloud-shell"`, `"compute-default-credentials"`, `"gateway"`.
- **Related keys:** `security.auth.enforcedType` and `security.auth.useExternal` also exist.
- **Legacy key:** the old v1 top-level `selectedAuthType` is still mapped to `security.auth.selectedType` in `packages/a2a-server/src/config/settings.ts` (`MIGRATION_MAP`). Reading it as a fallback is reasonable.

**Environment detection** (`getAuthTypeFromEnv`) checks, in order:
1. `GOOGLE_GENAI_USE_GCA=='true'`
2. `GOOGLE_GENAI_USE_VERTEXAI=='true'`
3. `GOOGLE_GEMINI_BASE_URL` (gateway)
4. `GEMINI_API_KEY`
5. `CLOUD_SHELL=='true'` or `GEMINI_CLI_USE_COMPUTE_ADC=='true'`

`GEMINI_DEFAULT_AUTH_TYPE` also exists.

**CodexBar's handling [CB]:**
- `"gemini-api-key"`, the legacy `"api-key"` and `"vertex-ai"` are unsupported.
- Everything else, including a missing value, `"gateway"` and `"cloud-shell"`, is treated as "try OAuth".

### 1.5 `google_accounts.json` [SRC]
```json
{ "active": "user@gmail.com", "old": ["previous@gmail.com"] }
```
- `active` may be `null`; on logout the active account is moved to `old`.
- The email comes from `GET https://www.googleapis.com/oauth2/v2/userinfo`.
- The file is written non-atomically.
- For display, use `active`, or the `email` claim in `id_token`.
- In keychain mode, this file is the only local source of the email.

---

## 2. Endpoints

### 2.1 Base URL and headers [SRC]
From `code_assist/server.ts`:
- `CODE_ASSIST_ENDPOINT = 'https://cloudcode-pa.googleapis.com'` and `CODE_ASSIST_API_VERSION = 'v1internal'`. Both can be overridden with env vars of the same names.
- The method URL is `${endpoint}/${version}:${method}`.
- Every call is a `POST` with `Content-Type: application/json` and `Authorization: Bearer <access_token>`.
- gemini-cli retries 429, 499 and 5xx up to 3 times.
- gemini-cli also sends `User-Agent: GeminiCLI/<version>/<model> (<platform>; <arch>; <surface>)` on these calls. CodexBar sends none and works [CB], so the header is optional.

### 2.2 `loadCodeAssist`: project and tier
Request: `POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`.

**Request body**
- **gemini-cli** (`setup.ts`, [SRC]). When `GOOGLE_CLOUD_PROJECT` or `GOOGLE_CLOUD_PROJECT_ID` is set, the body adds `cloudaicompanionProject` and `metadata.duetProject`. Otherwise the body is:
  ```json
  {"metadata":{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}
  ```
- **CodexBar** [CB]: `{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}`.
- **Enum values:** `ideType` can be `"GEMINI_CLI"`; `platform` can be `"WINDOWS_AMD64"`.
- **Optional `mode`:** `"MODE_UNSPECIFIED" | "FULL_ELIGIBILITY_CHECK" | "HEALTH_CHECK"`. gemini-cli sends `HEALTH_CHECK` only when refreshing credits.

**Response: `LoadCodeAssistResponse` [SRC]**
- `currentTier?`, `allowedTiers?[]`, `ineligibleTiers?[]`, `cloudaicompanionProject?` (string), `paidTier?`.

**`GeminiUserTier` fields:**
- `id` (`"free-tier" | "legacy-tier" | "standard-tier" | string`)
- `name`, `description`
- `userDefinedCloudaicompanionProject`, `isDefault`
- `privacyNotice{showNotice, noticeText}`
- `hasAcceptedTos`, `hasOnboardedPreviously`
- `availableCredits[{creditType: "GOOGLE_ONE_AI"|"CREDIT_TYPE_UNSPECIFIED", creditAmount: "<int64 string>"}]`

**`IneligibleTier` fields:**
- `reasonCode`, `reasonMessage`, `tierId`, `tierName`
- `validationErrorMessage`, `validationUrl`, `validationUrlLinkText`, `validationLearnMoreUrl`, `validationLearnMoreLinkText`

**Known `reasonCode` values in gemini-cli:** `DASHER_USER`, `INELIGIBLE_ACCOUNT`, `NON_USER_ACCOUNT`, `RESTRICTED_AGE`, `RESTRICTED_NETWORK`, `UNKNOWN`, `UNKNOWN_LOCATION`, `UNSUPPORTED_LOCATION`, `VALIDATION_REQUIRED`. Plus `UNSUPPORTED_CLIENT` from the server [CB].

**How gemini-cli resolves the result [SRC]:**
- **Project:** the response's `cloudaicompanionProject`, otherwise the env project. A numeric env value is rejected with `InvalidNumericProjectIdError`.
- **Tier:** `paidTier.id ?? currentTier.id ?? "standard-tier"`.
- **Tier name:** `paidTier.name ?? currentTier.name`.
- **Missing project:** if `currentTier` is present but there is no project and no env project, the CLI throws:
  - `IneligibleTierError` when `ineligibleTiers` is non-empty;
  - otherwise `ProjectIdRequiredError` ("requires setting the GOOGLE_CLOUD_PROJECT or GOOGLE_CLOUD_PROJECT_ID env var").
- **VPC-SC:** a `SECURITY_POLICY_VIOLATED` error is treated as standard-tier.
- **Missing `currentTier`:** the CLI calls `onboardUser`, a long-running operation. **The widget must NOT call `onboardUser`**; show "run `gemini` once" instead.
- **Caching:** results are cached for 30 s.

**CodexBar specifics [CB]:**
- It also accepts `cloudaicompanionProject` as an object `{id}` or `{projectId}`.
- **Project fallback when there is none:**
  1. `GET https://cloudresourcemanager.googleapis.com/v1/projects` (Bearer).
  2. Pick the first `projects[].projectId` that starts with `gen-lang-client` or has `labels["generative-language"]`.
  3. If none matches, POST `{}` to `retrieveUserQuota`.

**Example success response** (field names from source, values illustrative; based on CodexBar's `loadCodeAssistGoogleOneProResponse` fixture):
```json
{
  "currentTier": {"id": "standard-tier", "name": "Gemini Code Assist", "hasOnboardedPreviously": true},
  "cloudaicompanionProject": "my-gcp-project-123",
  "paidTier": {"name": "Gemini Code Assist in Google One AI Pro", "availableCredits": [{"creditType": "GOOGLE_ONE_AI", "creditAmount": "1000"}]}
}
```

**Example shutdown response** (CodexBar fixture, [CB]):
```json
{
  "allowedTiers": [{"id":"standard-tier","name":"Gemini Code Assist","userDefinedCloudaicompanionProject":true,"isDefault":true}],
  "ineligibleTiers": [{"reasonCode":"UNSUPPORTED_CLIENT","reasonMessage":"This client is no longer supported for Gemini Code Assist for individuals. To continue using Gemini, please migrate to the Antigravity suite of products: https://antigravity.google","tierId":"free-tier","tierName":"Gemini Code Assist for individuals"}]
}
```

### 2.3 `retrieveUserQuota`: the usage data [SRC]
Request: `POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota`.
- **Body:** `{"project":"<cloudaicompanionProject>"}`.
  - The type is `RetrieveUserQuotaRequest {project: string; userAgent?: string}`.
  - gemini-cli sends only `{project}`, and only when it has a project.
  - CodexBar sends `{}` when it has no project [CB].

**Response type:**
```ts
interface BucketInfo { remainingAmount?: string; remainingFraction?: number; resetTime?: string; tokenType?: string; modelId?: string; }
interface RetrieveUserQuotaResponse { buckets?: BucketInfo[]; }
```

**Example.** The first bucket is gemini-cli's `server.test.ts` fixture; the others are illustrative:
```json
{"buckets":[
  {"modelId":"gemini-2.5-pro","tokenType":"REQUESTS","remainingFraction":0.75,"resetTime":"2025-10-22T16:01:15Z"},
  {"modelId":"gemini-2.5-flash","tokenType":"REQUESTS","remainingFraction":0.98,"resetTime":"2025-10-22T16:01:15Z"},
  {"modelId":"gemini-2.5-flash-lite","tokenType":"REQUESTS","remainingFraction":1,"resetTime":"2025-10-22T16:01:15Z"}
]}
```

**Field notes:**
- **`remainingFraction`** runs from 0 to 1 and is the remaining share.
- **`remainingAmount`** is an int64 sent as a string and is optional. In `Config.refreshUserQuota()`:
  - when it is present, `limit = round(remaining / remainingFraction)`;
  - when only the fraction is present, `limit = 100` and `remaining = round(fraction*100)`;
  - buckets without `modelId` or with a null fraction are skipped.
- **`resetTime`** is an ISO-8601 UTC timestamp.
- **`tokenType`:** only `"REQUESTS"` has been seen, in a test fixture. Other values are **UNCONFIRMED**. CodexBar's comment ("input tokens usually") hints that one model can have several buckets, so always take the **minimum per model**.
- **Model IDs** (`models.ts` and `defaultModelConfigs.ts`):

  | Tier | Model IDs |
  |---|---|
  | pro | `gemini-2.5-pro`, `gemini-3-pro-preview`, `gemini-3.1-pro-preview`, `gemini-3.1-pro-preview-customtools` |
  | flash | `gemini-2.5-flash`, `gemini-3-flash-preview`, `gemini-3-flash` (legacy, remapped), `gemini-3.5-flash`, `gemini-3.8-flash` |
  | flash-lite | `gemini-2.5-flash-lite`, `gemini-3.1-flash-lite`, `gemini-3.1-flash-lite-preview`, `gemini-3.5-flash-lite` |

  Classify by substring: `flash-lite`, then `flash`, then `pro`.

### 2.4 How the CLI and CodexBar compute a percentage
**gemini-cli `/stats` (alias `/usage`), in `ModelQuotaDisplay.tsx` [SRC]:**
1. Keep buckets that have `modelId` and `remainingFraction`.
2. Group them by the model definition's `tier`. Unknown models are grouped by their own `modelId`.
3. Keep the **lowest** `remainingFraction` per group.
4. `usedPercentage = (1 - remainingFraction) * 100`.
5. Colour the value as a warning at 80% or more and as an error at 100% or more.
6. Show "Resets: …".

**gemini-cli pooled figure [SRC].** In `auto` mode, `/stats` and the footer also show a pooled number:
- remaining = Pro remaining + Flash remaining, and limit = Pro limit + Flash limit, each normalised to 100 when only fractions are sent;
- reset = the latest of the two `resetTime` values.

**gemini-cli staleness [SRC]:** `refreshUserQuotaIfStale(30_000)` refreshes after content generation.

**CodexBar `GeminiStatusProbe.swift` [CB]:**
1. Take the minimum fraction per `modelId`; `percentLeft = fraction*100`.
2. Build the windows:
   - primary = Pro minimum;
   - secondary = Flash minimum;
   - tertiary = Flash-Lite minimum;
   - each one is `RateWindow(usedPercent: 100 - percentLeft, windowMinutes: 1440, resetsAt: resetTime)`.
3. "Automatic" menu-bar figure (from `docs/gemini.md`): the first exhausted tier, otherwise Pro → Flash → Flash Lite.
4. The reset label is "Resets in Xh Ym".

### 2.5 Errors and shutdown detection (follows CodexBar [CB])

| Response | Meaning | What to show |
|---|---|---|
| `loadCodeAssist` 200, no `currentTier`, any `ineligibleTiers[].reasonCode` or `reasonMessage` matching `UNSUPPORTED_CLIENT`, AND no `paidTier.name` AND no `hd` claim | Consumer shutdown | "Gemini CLI consumer access ended 2026-06-18 — use Antigravity" |
| `retrieveUserQuota` 403 with `error.details[].reason=="SUBSCRIPTION_REQUIRED"` (domain `cloudaicompanion.googleapis.com`, message "You do not have a valid license of this product… (#3501)") | No licence. Count it as the shutdown only if the same fetch saw `UNSUPPORTED_CLIENT` and the tier is not `standard-tier` | Shutdown message, or "No Code Assist licence" |
| Any non-200 body containing `unsupported_client`, `IneligibleTierError`, "no longer supported" + "gemini code assist", or "migrate" + "antigravity" + "gemini" (case-insensitive) | Shutdown | Shutdown message |
| 401 on quota | Token problem | Refresh once and retry, then show "Not logged in, run `gemini`" |
| `ineligibleTiers[].reasonCode=="VALIDATION_REQUIRED"` with `validationUrl` [SRC] | Account validation needed | Link to `validationUrl` |
| `ProjectIdRequiredError` case (`currentTier` present, no project) | Standard tier without a project | "Set GOOGLE_CLOUD_PROJECT" |

---

## 3. Token refresh

### 3.1 Public installed-app client [SRC]
From `packages/core/src/code_assist/oauth2.ts`:
- `OAUTH_CLIENT_ID = '<gemini-cli public client id>.apps.googleusercontent.com'`
- `OAUTH_CLIENT_SECRET = 'GOCSPX-<gemini-cli public client secret>'`

The literal values are deliberately **not** reproduced in this repository. They are public (installed application; Google's OAuth overview says *"the client secret is obviously not treated as a secret."*), but GitHub Push Protection blocks them as secrets. Read them from the gemini-cli source or the installed package at runtime.

The same values are in the published npm bundle 0.66.0 (`bundle/chunk-*.js`), written as `OAUTH_CLIENT_ID = "..."` with **double quotes**. The npm package has no `dist/src/code_assist/oauth2.js`.

**CodexBar's resolution order [CB]:**
1. `GEMINI_OAUTH_CLIENT_ID` + `GEMINI_OAUTH_CLIENT_SECRET`;
2. `GEMINI_OAUTH2_JS_PATH`;
3. a regex over the installed package: `(?:const|let|var)?\s*OAUTH_CLIENT_ID\s*=\s*['"]([\w\-\.]+)['"]\s*;`, and the same with `([\w\-]+)` for the secret.

**Decision for the widget:** do not hard-code the values. Resolve them at runtime like CodexBar: env vars first, then the same regex over the installed package (`<npm prefix>\node_modules\@google\gemini-cli\bundle\*.js`, or `@google\gemini-cli-core\dist\src\code_assist\oauth2.js`). `%APPDATA%\npm` is npm's usual Windows global prefix, not verified for every install, so also check next to `gemini` on PATH.

### 3.2 Refresh request [SRC]
This matches google-auth-library 10.9.0 `refreshTokenNoCache`, the native-app doc, and CodexBar:
```
POST https://oauth2.googleapis.com/token
Content-Type: application/x-www-form-urlencoded

client_id=<OAUTH_CLIENT_ID>&client_secret=<OAUTH_CLIENT_SECRET>&refresh_token=<url-encoded>&grant_type=refresh_token
```

The doc's example response:
```json
{"access_token":"...","expires_in":3920,"scope":"...","token_type":"Bearer"}
```
Real `expires_in` values are about 3599.

**Request details:**
- The doc now marks `client_secret` as "Optional" and mentions an optional `DPoP` header. Send `client_secret` anyway, as gemini-cli does.
- `id_token` in the refresh response is **UNCONFIRMED**. Treat it as optional; the email and `hd` claims don't change, so the old `id_token` is fine for display.

**Handling the response:**
- Set `expiry_date = now_ms + expires_in*1000`.
- **When to refresh:** when `expiry_date <= now + 5 min` (`DEFAULT_EAGER_REFRESH_THRESHOLD_MILLIS = 5*60*1000`). If `expiry_date` is missing, google-auth-library treats the token as not expiring. CodexBar refreshes only once the token has actually expired.
- **Errors:** 400 `{"error":"invalid_grant"}` means the token was revoked or expired. Show "run `gemini` to log in again".

### 3.3 Refresh tokens don't rotate [SRC]
- Google's documented refresh response has no `refresh_token`.
- `refreshAccessTokenAsync()` sets `tokens.refresh_token = this.credentials.refresh_token`.
- The `'tokens'` event that gemini-cli persists carries no `refresh_token`. gemini-cli's `{...existing, ...new}` merge is what keeps it in the file.
- Keychain mode does the same: `mergedRefreshToken = credentials.refresh_token ?? existing.refreshToken`.
- Google never explicitly promises non-rotation. If a `refresh_token` ever comes back, use it.

### 3.4 Refresh in memory only (recommended)
**Why it is safe:**
- A refresh does not invalidate the file's refresh token.
- gemini-cli refreshes on its own at startup: `getAccessToken()` followed by a `getTokenInfo()` check.
- Refreshing in memory avoids racing a running gemini-cli.

**If you do write back:** read the current file, merge only `access_token`, `expiry_date` and `id_token`, keep every other key, write a temp file in the same folder, then replace the original. CodexBar does write back.

**Never run your own OAuth login with this client ID.** Google allows 100 live refresh tokens per Google Account per client ID and **silently invalidates the oldest** when a new one is issued.

**Refresh tokens also die when** [SRC, OAuth overview]:
- the user revokes access;
- the token is unused for 6 months;
- the account goes over the live-token limit;
- time-based access expires;
- an admin sets a service to Restricted (`admin_policy_enforced`);
- a Google Cloud session-length policy is exceeded. This applies because the CLI requests `cloud-platform`.

---

## 4. API-key mode (`GEMINI_API_KEY`)
- **There is no usage or quota endpoint for API keys** [SRC, rate-limits page updated 2026-10-26].
  - The page only says *"View your active rate limits in AI Studio"*, which needs a browser login.
  - *"Rate limits are applied per project, not per API key."* They are measured as RPM, TPM and RPD, plus IPM/TPD for some models.
  - *"Requests per day (RPD) quotas reset at midnight Pacific time."*
  - The page mentions no rate-limit response headers. Their existence is **UNCONFIRMED**.
- CodexBar refuses API-key and Vertex modes (`unsupportedAuthType`).
- **Widget behaviour:** if `selectedType` is `"gemini-api-key"`, `"vertex-ai"` or `"gateway"` (or the env detection says so), show "usage not available (API key / Vertex)".

---

## 5. Windows and the best single "usage %"
**What `retrieveUserQuota` returns:** per-model request buckets. CodexBar models each one as a 24 h window (`windowMinutes: 1440`).
- Whether `resetTime` is a rolling 24 h or a fixed daily reset is **UNCONFIRMED**. Always display the bucket's own `resetTime`.

**Per-minute limits exist but are not exposed** [SRC, from `utils/googleQuotaErrors.ts`]. They only appear as errors:
- 429 `RATE_LIMIT_EXCEEDED` from the cloudcode-pa domain is retryable only when a `RetryInfo` delay is present and short enough; otherwise it is terminal.
- `QUOTA_EXHAUSTED` is terminal.
- A `QuotaFailure` `quotaId` containing `PerDay` or `Daily` is terminal: "You have exhausted your daily quota on this model."
- `MODEL_CAPACITY_EXHAUSTED`/`EXCEEDED` and `INSUFFICIENT_G1_CREDITS_BALANCE` are terminal.

**Documented daily caps** (gemini-cli `docs/resources/quota-and-pricing.md`; not yet updated for the shutdown):

| Tier | Requests per user per day |
|---|---|
| Code Assist for individuals | 1,000 |
| Google AI Pro | 1,500 |
| Google AI Ultra | 2,000 |
| Code Assist Standard | 1,500 |
| Code Assist Enterprise | 2,000 |
| Workspace AI Ultra | 2,000 |
| Unpaid API key | 250 |

**Google's quotas page** (2026-10-26) lists only Standard (1,500) and Enterprise (2,000). It also says:
- CLI and agent-mode quotas are *combined*;
- daily limits are *"aggregated across all interactions with any model version or family"*.

**Unresolved conflict:** the API returns separate per-model buckets, and gemini-cli's error text says "daily quota on this model". The sources cannot settle whether the buckets are separate caps or views of one pool.

**Recommended display:**
1. Group buckets into Pro, Flash and Flash-Lite (substring match) and take the minimum `remainingFraction` per group.
2. If any group is exhausted (`remainingFraction == 0`), show it.
3. Otherwise show **Pro used % = (1 - minPro)*100**, with that bucket's `resetTime`, labelled "Daily (Pro)".
4. Fall back to Flash, then Flash-Lite.
5. Optional second line: Flash daily %, or gemini-cli's pooled Pro+Flash figure.
6. Colour at 80% (warning) and 100% (error).
7. **Plan label:** `paidTier.name`, otherwise derived:
   - `standard-tier` → "Paid";
   - `free-tier` + `hd` → "Workspace";
   - `free-tier` → "Free";
   - `legacy-tier` → "Legacy".
8. **Polling:**
   - quota every 2–5 minutes;
   - cache `loadCodeAssist` results (project, tier) for about an hour;
   - never call `onboardUser`.

---

## 6. C# outline
```csharp
// ClientId / ClientSecret: resolved at runtime from env vars or the installed gemini-cli package (see §3.1).
// Never hard-code them in this repository (GitHub Push Protection blocks them).
static string GeminiHome() {
    var h = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
    return string.IsNullOrEmpty(h) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : h;
}
static string GeminiDir() => Path.Combine(GeminiHome(), ".gemini");
var jsonc = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
// 1. settings.json (JSONC): security.auth.selectedType ?? top-level selectedAuthType.
//    "gemini-api-key" | "api-key" | "vertex-ai" | "gateway" => "usage not available".
// 2. Credentials:
//    a) oauth_creds.json: open with FileShare.ReadWrite|FileShare.Delete; retry on IOException;
//       ignore *.tmp files in a FileSystemWatcher.
//       Fields: access_token, refresh_token, expiry_date (GetDouble -> long ms), id_token.
//    b) else Credential Manager "gemini-cli-oauth/main-account"
//       (CredReadW, CRED_TYPE_GENERIC = 1; blob = UTF-8 JSON;
//       use token.accessToken / token.refreshToken / token.expiresAt).
//    c) else gemini-credentials.json => unsupported in v1.
// 3. If the access token is empty or expiry <= now + 5 min, refresh in memory:
var form = new FormUrlEncodedContent(new Dictionary<string,string>{
  ["client_id"]=ClientId, ["client_secret"]=ClientSecret, ["refresh_token"]=rt, ["grant_type"]="refresh_token"});
var tok = await http.PostAsync("https://oauth2.googleapis.com/token", form); // access_token, expires_in[, id_token]
// 4. loadCodeAssist
var lca = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist") {
  Content = JsonContent.Create(new { metadata = new { ideType = "IDE_UNSPECIFIED", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI" } }) };
lca.Headers.Authorization = new("Bearer", accessToken);
// Project = cloudaicompanionProject (string, or object.id / object.projectId)
//        ?? env GOOGLE_CLOUD_PROJECT ?? GOOGLE_CLOUD_PROJECT_ID
//        ?? ~/.gemini/.env ?? ~/.env
//        ?? optional CodexBar fallback: GET cloudresourcemanager v1/projects
//           (gen-lang-client* or label generative-language).
// Shutdown check: currentTier == null
//   && ineligibleTiers.Any(t => t.reasonCode == "UNSUPPORTED_CLIENT")
//   && paidTier?.name == null && idToken.hd == null.
// 5. retrieveUserQuota
var q = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota") {
  Content = JsonContent.Create(new { project = projectId }) };
q.Headers.Authorization = new("Bearer", accessToken);
// 6. buckets: min remainingFraction per modelId, then per tier ("flash-lite" / "flash" / "pro");
//    used% = (1 - f) * 100; reset = DateTimeOffset.Parse(resetTime, CultureInfo.InvariantCulture).
```

Win32 interop for path (b): declare `CredReadW(string TargetName, uint Type, uint Flags, out IntPtr Credential)` and `CredFree(IntPtr)`, both in advapi32 with `CharSet.Unicode`. The `CREDENTIALW` struct fields, in order:
`Flags`, `Type`, `TargetName`, `Comment`, `LastWritten` (FILETIME), `CredentialBlobSize`, `CredentialBlob`, `Persist`, `AttributeCount`, `Attributes`, `TargetAlias`, `UserName`.

---

## 7. Related sources (not Gemini CLI; separate research needed)
**Antigravity**, the replacement for AI Pro/Ultra [CB, `docs/antigravity.md`]:
- **Main endpoint:** `POST https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`.
  - Groups are under `response.groups[].displayName`.
  - Buckets are `response.groups[].buckets[].{bucketId, displayName, remaining.remainingFraction, description}`.
  - There are **5-hour and weekly** windows for "Gemini Models" and for "Claude and GPT models". Starter accounts may have weekly windows only.
- **Fallbacks:** `cloudcode-pa .../v1internal:fetchAvailableModels` and `retrieveUserQuota`.
- **Local language server:** `https://127.0.0.1:<port>/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary`, which needs a CSRF token.
- **Credentials:** Antigravity uses its **own OAuth client**. Its migration page says the CLI moves tokens into the OS keyring, so the Gemini CLI credentials cannot be reused.

**Gemini app:**
- Google support answer 17004136: *"compute-based usage limits that will refresh every 5 hours until you reach your weekly limit"*.
- CodexBar reads these limits with a cookie-authenticated GET to `https://gemini.google.com/_/BardChatUi/graphql/schemas/GEMINI_WEB_GRAPHQL/executeQuery`.
  - The response has `data.viewer.rpcs.getUsageInfo.pools` with entries `GXU_FIVE_HOURLY` and `GXU_WEEKLY`.
  - `percentageUsed` is a fraction and `gxu.windowRefreshesAt` is the reset time.
- This needs browser cookies, so it does not fit the "existing local CLI credentials" model.

---

## Still UNCONFIRMED
- Whether Workspace and education accounts are exempt from the shutdown; only CodexBar says so.
- Whether `tokenType` can be anything other than `"REQUESTS"`.
- Whether `resetTime` is a rolling or a fixed daily reset.
- Whether per-model buckets are separate caps or one pool.
- Whether the refresh response includes `id_token`.
- Whether the stored `scope` includes `openid`.
- How long Standard/Enterprise users can keep using Gemini CLI itself.
- The live shapes of the shutdown responses; only CodexBar's fixtures were checked.


## Verifier: missing_but_important
- Windows Credential Manager target is CONFIRMED as `gemini-cli-oauth/main-account`. It is a CRED_TYPE_GENERIC (=1) credential with UserName 'main-account', and its CredentialBlob is the UTF-8 bytes of {serverName, token:{accessToken, refreshToken, tokenType, scope, expiresAt}, updatedAt}. Read it with CredReadW + CredFree. This is used when gemini-cli runs with GEMINI_FORCE_ENCRYPTED_FILE_STORAGE=true, which a WPF app launched from Explorer usually cannot see in its environment. In that mode oauth_creds.json is deleted after migration and there is no id_token.
- settings.json is parsed with strip-json-comments and may contain comments and `$VAR` references. In C#, use JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }.
- CodexBar's project-ID fallback: when loadCodeAssist has no cloudaicompanionProject, it does GET https://cloudresourcemanager.googleapis.com/v1/projects (Bearer token) and picks the first projects[].projectId starting with 'gen-lang-client' or with labels['generative-language']. If that also fails, it POSTs {} to retrieveUserQuota. gemini-cli itself never calls retrieveUserQuota without a project.
- gemini-cli's IneligibleTierReasonCode enum does not include UNSUPPORTED_CLIENT. Treat reasonCode and tierId as free-form strings and don't use a closed enum.
- .env loading is conditional. `.gemini/.env` files are read only in trusted workspaces, and existing process environment variables always win over .env values. For a widget, a reasonable approximation is: process env → %USERPROFILE%\.gemini\.env → %USERPROFILE%\.env, for GOOGLE_CLOUD_PROJECT and GOOGLE_CLOUD_PROJECT_ID.
- The temp file pattern `.oauth_creds.json.<pid>.<ms>.<n>.tmp` appears in the .gemini folder during writes, and there is a fallback direct write. A FileSystemWatcher should ignore *.tmp files and re-read oauth_creds.json after a short debounce. Open the file with FileShare.ReadWrite | FileShare.Delete.
- The published npm bundle (@google/gemini-cli 0.66.0, bundle/chunk-*.js) contains `OAUTH_CLIENT_ID = "..."` with DOUBLE quotes, and there is no dist/src/code_assist/oauth2.js. Any regex fallback must accept either quote style.
- gemini-cli shows a pooled figure in /stats (alias /usage) and the footer when the model is 'auto'. It sums remaining and limit of the primary Pro model and the matching Flash model, uses limit 100 per bucket when only remainingFraction is sent, and takes the latest resetTime. This is an alternative single % that matches the CLI's own UI.
- refreshUserQuota remaps bucket modelIds 'gemini-3-flash', 'gemini-3.5-flash' and 'gemini-3.8-flash' to the flash model the account has access to. It also sets preview-model access when any bucket's modelId is a preview model. Buckets with no modelId or a null remainingFraction are skipped.
- Google's 100-refresh-tokens-per-account-per-client limit invalidates the OLDEST token silently. The widget must never run its own OAuth login with gemini-cli's client ID; it should only reuse the CLI's refresh_token.
- The Code Assist sunset page also says licenses can be added until 2027-01-31, and that Antigravity is included in Gemini Enterprise editions. Neither Google page says how long Standard/Enterprise users can keep using Gemini CLI itself.
- If loadCodeAssist fails with a VPC-SC SECURITY_POLICY_VIOLATED error, gemini-cli treats the user as standard-tier. If currentTier is present but there is no project, it throws ProjectIdRequiredError ('This account requires setting the GOOGLE_CLOUD_PROJECT or GOOGLE_CLOUD_PROJECT_ID env var'). The widget should show 'set GOOGLE_CLOUD_PROJECT' in that case.
- Antigravity (the replacement for AI Pro/Ultra) uses its own OAuth client and its own credentials, which the Antigravity migration page says live in the OS keyring. It cannot reuse ~/.gemini/oauth_creds.json and needs a separate provider research pass.

## Verifier: checked claims (non-confirmed only)
- [corrected] If GEMINI_FORCE_ENCRYPTED_FILE_STORAGE=true, credentials go to the keychain (service 'gemini-cli-oauth', account 'main-account') or to the encrypted <home>\.gemini\gemini-credentials.json, not oauth_creds.json. The Windows Credential Manager target name (probably 'gemini-cli-oauth/main-account') is UNCONFIRMED.
  -> Everything in the claim is confirmed, and the open question is now resolved. @github/keytar 7.10.6 (repo github/node-keytar) stores the credential on Windows like this:
- CRED_TYPE_GENERIC with TargetName = service + '/' + account, so `gemini-cli-oauth/main-account`.
- UserName = 'main-account'.
- CredentialBlob = the raw UTF-8 bytes of the JSON string.
- Persist = CRED_PERSIST_ENTERPRISE.

The JSON is {serverName:'main-account', token:{accessToken, refreshToken, tokenType, scope, expiresAt(ms)}, updatedAt}. No id_token is stored in this mode.

When keytar is unavailable, or GEMINI_FORCE_FILE_STORAGE=true, or under WSL, the encrypted file is used. Its decrypted JSON is {"gemini-cli-oauth":{"main-account":"<that JSON as a string>"}}. The cipher is AES-256-GCM with a 12-byte IV and a 16-byte tag, stored as hex iv:tag:cipher. The key is scryptSync('gemini-cli-oauth', `${os.hostname()}-${os.userInfo().username}-gemini-cli`, 32).

On first load in this mode, the CLI migrates the plain oauth_creds.json into storage and DELETES it.
- [corrected] Older v1 settings used a top-level selectedAuthType; that key no longer appears in current source.
  -> The CLI package no longer migrates v1 settings. However, packages/a2a-server/src/config/settings.ts still has MIGRATION_MAP { selectedAuthType: 'security.auth.selectedType' }, so the legacy key is still known in-repo. Reading top-level `selectedAuthType` as a fallback is reasonable.
- [corrected] .env lookup: from cwd upward (.gemini/.env then .env), falling back to <home>/.gemini/.env then <home>/.env.
  -> The order is correct, with conditions. `.gemini/.env` files (both in the project and in home) are considered ONLY when the workspace is trusted. A bare `.env` is skipped when advanced.ignoreLocalEnv or --ignore-env is set, unless it is in the home dir. Variables already in process.env are NEVER overwritten by .env values. In untrusted folders only GEMINI_API_KEY, GOOGLE_API_KEY, GOOGLE_CLOUD_PROJECT and GOOGLE_CLOUD_LOCATION are loaded, and they are sanitized.
- [corrected] Current gemini-cli model IDs: gemini-2.5-pro, gemini-3-pro-preview, gemini-3.1-pro-preview (pro); gemini-2.5-flash, gemini-3-flash, gemini-3.5-flash, gemini-3.8-flash (flash); gemini-2.5-flash-lite, gemini-3.1-flash-lite, gemini-3.5-flash-lite (flash-lite).
  -> The list is incomplete. defaultModelConfigs.ts also defines:
- gemini-3-flash-preview (PREVIEW_GEMINI_FLASH_MODEL, tier flash);
- gemini-3.1-pro-preview-customtools (tier pro);
- gemini-3.1-flash-lite-preview.

'gemini-3-flash' is LEGACY_CCPA_FLASH_MODEL, which refreshUserQuota remaps to gemini-3.5-flash or gemini-3.8-flash. Substring classification still works.
- [corrected] Per-minute limits show up only as 429 RATE_LIMIT_EXCEEDED (retryable); daily exhaustion as QUOTA_EXHAUSTED (terminal).
  -> There is a nuance for errors from the cloudcode-pa domain. RATE_LIMIT_EXCEEDED is retryable only when a RetryInfo delay is present and at or below the max; with no delay, or a longer one, it is TERMINAL. QUOTA_EXHAUSTED is terminal. A QuotaFailure quotaId containing 'PerDay'/'Daily' is terminal. MODEL_CAPACITY_EXHAUSTED/EXCEEDED and INSUFFICIENT_G1_CREDITS_BALANCE are terminal.
- [could-not-verify] Workspace and education accounts (hd claim) are outside the consumer shutdown.
  -> Only CodexBar states this, in docs/gemini.md and GeminiConsumerTierMigration.swift. Google's deprecation page and the Antigravity migration page do not mention Workspace or education. This remains UNCONFIRMED.
- [could-not-verify] tokenType values other than 'REQUESTS'; whether resetTime is rolling or fixed; whether the refresh response includes id_token; whether the stored scope includes 'openid'.
  -> Still UNCONFIRMED. The only tokenType seen in any primary source is 'REQUESTS' (gemini-cli server.test.ts). CodexBar's comment 'keeping lowest per model (input tokens usually)' hints that one model can have several buckets, so always take the minimum per model. The native-app doc's refresh section does not mention id_token.