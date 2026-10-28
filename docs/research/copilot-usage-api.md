# copilot

# GitHub Copilot usage on Windows: implementation facts (verified 2026-10-28)

**How this report was checked.** Every item below was checked against primary sources:
- VS Code `main`: `defaultAccount.ts`, `chatEntitlementService.ts`, `chatStatusDashboard.ts`, `product.json`
- `microsoft/vscode-copilot-chat`
- `@github/copilot` 1.0.40 `app.js`
- `@github/copilot-language-server` 1.562.0 and 1.400.0 `main.js`
- CodexBar Swift sources, tests and docs
- CopilotChat.nvim `providers.lua`
- `cli/cli`, `cli/go-gh` and `zalando/go-keyring`
- `atom/node-keytar` `keytar_win.cc`
- docs.github.com and the GitHub changelog

Anything I could not confirm is marked **UNCONFIRMED**.

---

## 0. Billing context (from 1 June 2026)

- **All Copilot plans moved to usage-based billing** (GitHub AI Credits) on 2026-06-01.
  - Changelog: "premium request units (PRUs) will be replaced by GitHub AI Credits".
  - Annual Pro / Pro+ subscribers "will remain on their existing plan with premium request-based pricing until their plan expires."
- **Allowances per plan** (docs, "Usage-based billing for individuals" and "Plans"):

  | Plan | Price/month | AI credits/month |
  |---|---|---|
  | Pro | $10 | 1,500 (1,000 base + 500 flex) |
  | Pro+ | $39 | 7,000 (3,900 base + 3,100 flex) |
  | Max | $100 | 20,000 (10,000 base + 10,000 flex) |
  | Free / Student | free | "an allowance of AI credits", amount not published. Free: "2000 code completions per month" |

- **Reset:** "at 00:00:00 UTC on the first day of each calendar month". Credits do not carry over.
- **Completions:** "Code completions and next edit suggestions are not billed in AI credits and remain unlimited for all paid plans."
- **Same endpoint, new label.** Usage is still reported in `quota_snapshots.premium_interactions`. When the top-level `token_based_billing` is `true`, VS Code labels that meter **"Credits"**. Otherwise it labels it **"Premium requests"**, or **"Included premium requests"** when `overage_permitted` is true.
- **Session and weekly limits exist, but this endpoint does not expose them.** Verified in Copilot CLI 1.0.40:
  - They arrive only as **response headers on model/inference calls**: `x-usage-ratelimit-session` and `x-usage-ratelimit-weekly`.
  - The values are URL-encoded and parsed by `Mcr()`: `ent` (int, `-1` means unlimited), `ov` (overage, float), `ovPerm` (`"true"`/`"false"`), `rem` (percent remaining), `rst` (reset date string), and optionally `hasQuota` and `tbb`.
  - Related error codes: `user_weekly_rate_limited`, `user_global_rate_limited`, `user_model_rate_limited`, `rate_limited`, `integration_rate_limited`.
  - A passive widget cannot poll these values.
- **Show one monthly meter plus a reset date.**

---

## 1. The usage endpoint

```
GET https://api.github.com/copilot_internal/user
Authorization: Bearer <github_token>      # "token <github_token>" also works
Accept: application/json
User-Agent: AITokenUsage/1.0              # REQUIRED: GitHub rejects requests without a UA; .NET HttpClient sends none by default
X-GitHub-Api-Version: 2025-04-01          # optional (Copilot Chat and CodexBar send it)
```

**What the official clients send** (all verified):

| Client | Headers |
|---|---|
| VS Code core `defaultAccount.ts` (URL from `product.json` `defaultChatAgent.entitlementUrl`) | `'Authorization': \`Bearer ${session.accessToken}\`` only |
| Copilot Chat `copilotTokenManager.ts` `fetchCopilotUserInfo` | `Authorization: token ${githubToken}`, `'X-GitHub-Api-Version': '2025-04-01'` |
| Copilot CLI 1.0.40 `kT()` | `Authorization: Bearer ${e}`, `Accept: "application/json"`. Node fetch adds its own default UA. |
| CodexBar `CopilotUsageFetcher.swift` | `Authorization: token …`, `Accept: application/json`, `Editor-Version: vscode/1.96.2`, `Editor-Plugin-Version: copilot-chat/0.26.7`, `User-Agent: GitHubCopilotChat/0.26.7`, `X-Github-Api-Version: 2025-04-01` |
| CopilotChat.nvim `get_info` | `Authorization: Token <oauth_token>` only |

Editor headers are **not** required.

**GitHub Enterprise Cloud with data residency:** use `https://api.<host>/copilot_internal/user`, for example `api.octocorp.ghe.com`. VS Code, CodexBar and the CLI's `ZS()` all prefix `api.`.

**Status handling** (VS Code `defaultAccount.ts`):
- `200`: parse the body.
- `401`: "oauth token being unavailable (expired/revoked)". Try the next token source.
- `404`: "missing scopes/permissions, service pretends the endpoint doesn't exist". Try the next token source.
- Rate limited means any of:
  - `429`
  - `403` with `x-ratelimit-remaining: 0`
  - **any non-2xx response that carries `Retry-After`**

  Back off for `Retry-After` seconds, defaulting to 60.
- CodexBar treats `401` and `403` as "needs re-auth". A 403 "Resource not accessible by integration" has been reported for a third-party (OpenCode) OAuth token. That is a **secondary source and does not say which endpoint**.

**Polling:** VS Code caches entitlements for `ACCOUNT_DATA_POLL_INTERVAL_MS = 60 * 60 * 1000`. For a widget, poll every 5–15 minutes and offer a manual refresh.

---

## 2. Response schema

Sources:
- VS Code `IEntitlementsData` / `IQuotaSnapshotData`
- Copilot CLI 1.0.40 zod `oei` (user) and `RKe` (snapshot)
- Copilot Chat `CopilotUserInfo` / `CopilotUserQuotaInfo`

### 2.1 Top-level fields (treat all as optional)

| field | type | notes |
|---|---|---|
| `login` | string | in the CLI schema |
| `access_type_sku` | string | Known values: `free_limited_copilot` (Free, VS Code), `free_educational_quota` (Student, VS Code), `copilot_for_business_seat_quota` (Business, CodexBar #1258). Paid individual SKUs are **UNCONFIRMED**. |
| `copilot_plan` | string | VS Code: `individual`=Pro, `individual_pro`=Pro+, `individual_max`=Max, `individual_edu`=Student, `business`, `enterprise`. `"free"` appears only in CodexBar test fixtures (**UNCONFIRMED**). |
| `analytics_tracking_id` | string | |
| `assigned_date` | string, nullable | |
| `can_signup_for_limited` | bool | `true` means no plan, but Free is available |
| `chat_enabled` | bool | |
| `organization_login_list` | string[] | |
| `organization_list` | `[{login,name}]`, nullable | |
| `endpoints` | `{api, "origin-tracker", proxy, telemetry}` | |
| `token_based_billing` | bool | `true` means AI-credit billing; label the meter "Credits" |
| `can_upgrade_plan`, `is_staff`, `cloud_session_storage_enabled`, `codex_agent_enabled`, `is_mcp_enabled`, `restricted_telemetry`, `copilotignore_enabled` | bool | not needed |
| `quota_reset_date` | string | Usually `yyyy-MM-dd`. A CodexBar fixture also has `"2026-08-01T00:00:00Z"`, so parse ISO8601 first. |
| `quota_reset_date_utc` | string | Includes the time. VS Code prefers it. |
| `limited_user_reset_date` | string | Legacy Free. It is a number in the older `/v2/token` envelope, so parse leniently. |
| `monthly_quotas` | `{chat, completions}` numbers | legacy Free allowance |
| `limited_user_quotas` | `{chat, completions}` numbers | legacy Free **remaining** |
| `quota_snapshots` | object | Keys `chat`, `completions`, `premium_interactions`. The CLI uses `.catchall(snapshot.nullable())`, so unknown keys and nulls can appear. CodexBar tests include `chat_messages`. |

### 2.2 Snapshot fields

| field | type | notes |
|---|---|---|
| `quota_id` | string | |
| `entitlement` | number, or string in VS Code's type | `-1` means unlimited (CLI `CBe()`: `unlimited===true \|\| entitlement===-1`) |
| `remaining` | number | |
| `quota_remaining` | number | VS Code: `used = entitlement - quota_remaining` |
| `percent_remaining` | number | 0–100, may be fractional. CodexBar tests include `-15` (over quota). VS Code clamps it to [0,100]. |
| `unlimited` | bool | may be **absent** (Business TBB payload) |
| `has_quota` | bool | VS Code: "Under TBB, has_quota is always false at the per-snapshot level". Use it only for unlimited (pooled) snapshots. |
| `overage_permitted` | bool | |
| `overage_count` | number | |
| `overage_entitlement` | number | VS Code draws the "Additional Budget" bar when this is > 0 |
| `credits_used` | number | "Aggregate credits consumed… no denominator" |
| `quota_reset_at` | number | **Unix seconds.** It wins over the account-level date. |
| `timestamp_utc` | string | |
| `token_based_billing` | bool | |

### 2.3 Example payloads

**A. Paid individual, legacy premium requests (illustrative values).** The field names are confirmed; the values are made up.

```json
{
  "login": "octocat", "access_type_sku": "<UNCONFIRMED>", "copilot_plan": "individual",
  "chat_enabled": true, "can_signup_for_limited": false, "token_based_billing": false,
  "quota_reset_date": "2026-11-01", "quota_reset_date_utc": "2026-11-01T00:00:00.000Z",
  "quota_snapshots": {
    "chat":        { "quota_id": "chat", "entitlement": 0, "remaining": 0, "percent_remaining": 100, "unlimited": true, "overage_count": 0, "overage_permitted": false },
    "completions": { "quota_id": "completions", "entitlement": 0, "remaining": 0, "percent_remaining": 100, "unlimited": true, "overage_count": 0, "overage_permitted": false },
    "premium_interactions": { "quota_id": "premium_interactions", "entitlement": 300, "remaining": 71, "quota_remaining": 71,
      "percent_remaining": 23.7, "unlimited": false, "overage_count": 0, "overage_permitted": true, "overage_entitlement": 0,
      "timestamp_utc": "2026-10-28T09:00:00.000Z" }
  }
}
```

**UNCONFIRMED:** what a monthly Pro/Pro+/Max account on AI credits returns. It is probably the same shape with `token_based_billing: true`. Whether `entitlement` is base plus flex (1500/7000/20000) or base only is unknown. Capture a real response.

**B. Free, legacy fields** (VS Code `parseQuotas` handles them):

```json
{ "access_type_sku": "free_limited_copilot",
  "monthly_quotas": { "chat": 50, "completions": 2000 },
  "limited_user_quotas": { "chat": 31, "completions": 1650 },
  "limited_user_reset_date": "2026-11-01" }
```

**C. Business seat, token-based billing.** This is the **actual** CodexBar fixture for issue #1258. Note that there is **no `unlimited` field**.

```json
{ "copilot_plan": "business", "token_based_billing": true, "quota_reset_date": "2026-09-01",
  "quota_snapshots": {
    "premium_interactions": { "entitlement": 0, "remaining": 0, "percent_remaining": 100, "quota_id": "premium_interactions", "credits_used": 31 },
    "chat": { "entitlement": 0, "remaining": 0, "percent_remaining": 100, "quota_id": "chat", "credits_used": 0 } } }
```

The issue body also reports `access_type_sku: copilot_for_business_seat_quota` and a `completions` snapshot with the same zero values. Treat this shape as a placeholder:
- Show "N credits used", read from `credits_used` even though the snapshot itself is skipped.
- Never show "0% used".

### 2.4 C# parsing (System.Text.Json)

Parse with `JsonDocument`, or use DTOs with nullable `double` everywhere, plus `NumberHandling = JsonNumberHandling.AllowReadingFromString`.

Parse each `quota_snapshots` entry on its own (a `JsonElement` per key), so that one odd entry cannot fail the whole response.

```csharp
public sealed class CopilotUser {
  [JsonPropertyName("login")] public string? Login { get; set; }
  [JsonPropertyName("access_type_sku")] public string? AccessTypeSku { get; set; }
  [JsonPropertyName("copilot_plan")] public string? CopilotPlan { get; set; }
  [JsonPropertyName("can_signup_for_limited")] public bool? CanSignupForLimited { get; set; }
  [JsonPropertyName("token_based_billing")] public bool? TokenBasedBilling { get; set; }
  [JsonPropertyName("quota_reset_date")] public string? QuotaResetDate { get; set; }
  [JsonPropertyName("quota_reset_date_utc")] public string? QuotaResetDateUtc { get; set; }
  [JsonPropertyName("limited_user_reset_date")] public JsonElement? LimitedUserResetDate { get; set; } // string or number
  [JsonPropertyName("monthly_quotas")] public Dictionary<string, double?>? MonthlyQuotas { get; set; }
  [JsonPropertyName("limited_user_quotas")] public Dictionary<string, double?>? LimitedUserQuotas { get; set; }
  [JsonPropertyName("quota_snapshots")] public Dictionary<string, JsonElement>? QuotaSnapshots { get; set; } // deserialize each to QuotaSnapshot in try/catch
}
public sealed class QuotaSnapshot {
  [JsonPropertyName("quota_id")] public string? QuotaId { get; set; }
  [JsonPropertyName("entitlement")] public double? Entitlement { get; set; }        // may be "300"
  [JsonPropertyName("remaining")] public double? Remaining { get; set; }
  [JsonPropertyName("quota_remaining")] public double? QuotaRemaining { get; set; }
  [JsonPropertyName("percent_remaining")] public double? PercentRemaining { get; set; }
  [JsonPropertyName("unlimited")] public bool? Unlimited { get; set; }
  [JsonPropertyName("has_quota")] public bool? HasQuota { get; set; }
  [JsonPropertyName("overage_permitted")] public bool? OveragePermitted { get; set; }
  [JsonPropertyName("overage_count")] public double? OverageCount { get; set; }
  [JsonPropertyName("overage_entitlement")] public double? OverageEntitlement { get; set; }
  [JsonPropertyName("credits_used")] public double? CreditsUsed { get; set; }
  [JsonPropertyName("quota_reset_at")] public double? QuotaResetAt { get; set; }    // unix SECONDS (number, not guaranteed integer)
  [JsonPropertyName("timestamp_utc")] public string? TimestampUtc { get; set; }
  [JsonPropertyName("token_based_billing")] public bool? TokenBasedBilling { get; set; }
}
```

---

## 3. What to display (VS Code logic, verified)

1. **Plan name.** Follow the VS Code order exactly:
   - `access_type_sku=="free_limited_copilot"` → Copilot Free
   - `access_type_sku=="free_educational_quota"` → Copilot Student
   - `can_signup_for_limited` → no plan (Free available)
   - `copilot_plan`:
     - `individual_edu` → Student
     - `individual` → Pro
     - `individual_pro` → Pro+
     - `individual_max` → Max
     - `business` → Business
     - `enterprise` → Enterprise
   - Anything else → Unavailable (no Copilot)
2. **Primary meter.**
   - Free: `chat ?? premium_interactions` (CLI `p_t`). Label it "Credits" when token-billed, otherwise "Chat messages".
   - Paid: `premium_interactions`. Label it "Credits" when token-billed, otherwise "Premium requests".
   - Legacy fallback: `pctRemaining = limited_user_quotas.X / monthly_quotas.X * 100`, clamped to 0..100.
3. **Skip or alter the meter.**
   - `!unlimited && entitlement==0` → skip it as a placeholder, but still read `credits_used` if present (Business TBB case).
   - `unlimited && has_quota !== false && credits_used is a number` → "N credits used", with no denominator.
   - `unlimited && has_quota === false` → "Organization limit reached".
   - `unlimited`, no credits → "Included with your organization's plan".
4. **Numbers.**
   - `usedPct = max(0, 100 - clamp(percent_remaining,0,100))`. VS Code shows `floor(usedPct)%`.
   - `used = quota_remaining != null ? entitlement - quota_remaining : entitlement*(100 - pct)/100`, and `total = entitlement`. An entitlement of 0 means there is no total.
   - Overage: if `overage_entitlement > 0`, show the "Additional Budget" bar `(overage_entitlement - overage_count)/overage_entitlement`.
5. **Status.** CLI `$_r` thresholds:
   - `unlimited` → "unlimited"
   - `rem <= 0` → "overage" if `overage_permitted`, else "exhausted"
   - `rem <= 25` → "warning"
   - otherwise → "normal"

   **Do not** copy the CLI's `hasQuota===false` → exhausted rule for metered snapshots. Under TBB, `has_quota` is always false.
6. **Reset time.**
   - `snapshot.quota_reset_at` (unix seconds) wins.
   - Then `quota_reset_date_utc` (has a time), then `quota_reset_date`, then `limited_user_reset_date`.
   - Otherwise use the next 1st of the month at 00:00 UTC (the documented reset).
   - VS Code's format is "Resets {date} at {time}" when it has a time, otherwise "Resets {date}".
7. **Chat and completions.**
   - Paid plans: completions and NES are unlimited. VS Code shows the chat and completions ("Inline Suggestions") meters only if the snapshot is not unlimited **and** (the plan is not TBB **or** the user is Free).
   - Widget: show the completions meter (2,000 per month) only for Free. For paid plans show "Completions: unlimited".

---

## 4. Getting a token without our own login

Validate every candidate with a live call:
- `200` plus a parsable body: use it.
- `401`, `403` or `404`: try the next candidate.

**Prefix filter** (Copilot CLI 1.0.40):
- Accept `gho_`, `ghu_` and `github_pat_`. These are validated via `copilot_internal/user`.
- Reject `ghp_`: "Classic PATs are not supported…".
- The CLI also accepts `ghs_` but **without** validating it against this endpoint. Treat `ghs_` as **UNCONFIRMED**.

**Evidence on which token types work with `copilot_internal/user`:**
- **VS Code GitHub sessions:** confirmed. These are `gho_` tokens from OAuth app `01ab8ac9400c4e429b23` with scope sets `[read:user,user:email,repo,workflow]`, `[user:email]` or `[read:user]`. VS Code calls the endpoint with them.
- **`ghu_` tokens from the Copilot GitHub App `Iv1.b507a08c87ecfe98`:** confirmed. Used by CodexBar, copilot-api and CopilotChat.nvim.
- **gh CLI `gho_` tokens** (client `178c6fc778ccc68e1d6a`): the official Copilot CLI runs `gh auth token --hostname <host>` and immediately calls `kT()`, which is `GET /copilot_internal/user`. tpope's 2025 remark that gh tokens "don't support Copilot" was about the LS `/v2/token` exchange, which returned 404.
- **Fine-grained PAT:** it needs the "Copilot Requests" account permission and must be owned by a personal account (Copilot CLI docs). The CLI validates it via `copilot_internal/user`.
- **Classic PAT (`ghp_`):** **UNCONFIRMED** server-side. Every official client rejects it client-side.
- **Third-party OAuth apps:** reported to fail (404 at `/v2/token` in hermes-agent #16551; 403 "Resource not accessible by integration" in opencode-mystatus). Both are **secondary sources**. The idea that GitHub keeps an allowlist of OAuth clients is an inference.

### (a) Environment variables

- **Copilot CLI:** `COPILOT_GITHUB_TOKEN` → `GH_TOKEN` → `GITHUB_TOKEN`. The 1.0.40 code also tries a `GITHUB_ASKPASS` token. The host comes from `COPILOT_GH_HOST` or `GH_HOST`.
- **copilot-language-server (1.562.0):**
  - `GH_COPILOT_TOKEN` (ignored if it contains `=`)
  - `GITHUB_COPILOT_TOKEN`
  - `GITHUB_TOKEN`, only when `CODESPACES=="true"`
- `GITHUB_TOKEN` is often an unrelated classic PAT. Apply the prefix filter and validate.

### (b) GitHub CLI (`gh`): recommended

- **Command:** run `gh auth token --hostname github.com` in a hidden window with roughly a 5 s timeout, and trim stdout. This is exactly what the Copilot CLI does.
- **Refresh:** with no flags, `gh auth token` calls `getTokenWithRefresh`, so it returns an **already refreshed** token if gh starts issuing expiring credentials. `--no-refresh` turns that off.
- **Environment:** remove `GH_TOKEN` and `GITHUB_TOKEN` from the child process environment, otherwise they shadow the stored login.

**Storage details, if you want to read it directly (not recommended):**
- **Config dir** (go-gh `ConfigDir()`): `GH_CONFIG_DIR`, else `%XDG_CONFIG_HOME%\gh`, else `%AppData%\GitHub CLI`, else `~/.config/gh`. The file is `hosts.yml`.
- **`hosts.yml`** holds `hosts.<host>.users.<user>.oauth_token`, and the host-level `oauth_token`, only with insecure storage.
- **Windows Credential Manager** (go-keyring, `credName = service + ":" + username`), `CRED_TYPE_GENERIC`, UTF-8 blob:
  - `gh:github.com:<user>` (per user)
  - `gh:github.com:` (active / unkeyed; `activateUser` copies the active token here)
- **Refreshable credentials** live under service `gh:_refreshable:github.com`. Another reason to use the subprocess.

### (c) GitHub Copilot CLI

- **Docs:** OAuth tokens are stored in Windows Credential Manager under service `copilot-cli`. If no keychain is available, the CLI falls back to `~/.copilot/config.json`.
- **From 1.0.40:**
  - keytar service `copilot-cli`, account `` `${host}:${login}` `` with host `https://github.com`
  - keytar on Windows: `TargetName = service + '/' + account`, so **`copilot-cli/https://github.com:<login>`**
  - `UserName = account`, and the blob is the UTF-8 `gho_…` token
  - Enumerate with `CredEnumerate("copilot-cli*")`
- **Plaintext fallback:** `%COPILOT_HOME%` or `%USERPROFILE%\.copilot\config.json` (also a file named `config`), key `"copilotTokens": { "https://github.com:<login>": "gho_…" }`. It is used when `storeTokenPlaintext` is true or the keychain fails.
- **Current version:** 1.0.105 is a Node SEA `copilot.exe` with a compressed payload. **UNCONFIRMED** that it still uses this naming, so enumerate rather than hard-code.
- **CLI OAuth app:** `Ov23ctDVkRmgkPke0Mmm`, scope `read:user,read:org,repo,gist`.

### (d) copilot-language-server (copilot.vim, copilot.lua, and others; JetBrains location **UNCONFIRMED**)

**Config dir:** `%XDG_CONFIG_HOME%\github-copilot`, only if that path is absolute. Otherwise on win32 it is `%USERPROFILE%\AppData\Local\github-copilot`.

1. **`auth.db`** (LS 1.562.0, SQLite via `node:sqlite`, WAL, `busy_timeout 5000`).
   - Table `oauth_tokens(token_id, auth_authority, oauth_client_id, user_login, scopes, token_ciphertext BLOB, token_schema_version, source_editor_id, created_at, updated_at, last_used_at, UNIQUE(auth_authority,oauth_client_id,user_login))`.
   - Also `active_sessions(editor_id, token_id, activated_at)`, `editor_signout`, and `metadata` (key `legacy_files_migration_done`).
   - **Version 0:** UTF-8 plaintext. The current writer is `plaintextOnly()`.
   - **Version 1:** AES-256-GCM, laid out as `[12-byte nonce][ciphertext][16-byte tag]`.
     - The key is the base64 32-byte value of keytar `copilot-language-server` / `oauth-token-key`, which is Credential Manager target `copilot-language-server/oauth-token-key`.
     - The LS downgrades v1 rows to v0 when it reads them.
   - **Query:** `SELECT * FROM oauth_tokens WHERE auth_authority='github.com' ORDER BY last_used_at DESC, token_id DESC LIMIT 1`.
   - **Safe reading:** copy `auth.db`, `auth.db-wal` and `auth.db-shm` to a temp directory, or open with `Mode=ReadOnly` and a busy timeout.
2. **`apps.json`** (legacy; read during migration and not deleted).
   - Shape: `{ "github.com:Iv1.b507a08c87ecfe98": { "user": "<login>", "oauth_token": "ghu_…", "githubAppId": "Iv1.b507a08c87ecfe98" } }`. LS 1.400.0 also writes `dev_override`.
   - The key is `` `${authAuthority}:${githubAppId}` ``; split it at the **last** `:`.
   - The reader accepts `oauth_token||access_token`, `user||login`, `githubAppId`, `authAuthority` and `scopes`.
   - The default LS app id is `Iv1.b507a08c87ecfe98`. A key with no `:` defaults to `Ov23liV9UpD7Rnfnskm3`.
3. **`hosts.json`** (older legacy): `{ "github.com": { "user": "…", "oauth_token": "…" } }`. The LS **deletes** it after migration.
4. **CopilotChat.nvim reading order:** `hosts.json` first, then `apps.json`; it takes the first key containing `github.com` that has an `oauth_token`. Legacy tokens may be stale, so always validate.

### (e) VS Code

- Sessions are stored through `context.secrets`, key `github.auth`.
- The storage key is `secret://` + `JSON.stringify({extensionId:"vscode.github-authentication", key:"github.auth"})`, **encrypted** via `IEncryptionService` (Electron safeStorage).
- The file is `%APPDATA%\Code\User\globalStorage\state.vscdb` (path is a secondary source).
- **Not recommended:** reading it means decrypting Chromium OSCrypt, which is brittle and invasive.

### (f) Suggested order

1. Our own saved token (DPAPI)
2. Env vars, with the filter
3. `gh auth token`
4. Copilot CLI Credential Manager entries (`copilot-cli*`)
5. LS `auth.db`
6. LS `apps.json` / `hosts.json`
7. "Sign in with GitHub" button

### P/Invoke sketch

```csharp
[DllImport("advapi32.dll", EntryPoint="CredEnumerateW", CharSet=CharSet.Unicode, SetLastError=true)]
static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr pCredArray); // PCREDENTIALW*
[DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
static extern bool CredRead(string target, int type /*CRED_TYPE_GENERIC=1*/, int flags, out IntPtr cred);
[DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);
[StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
struct CREDENTIAL { public int Flags, Type; public string TargetName, Comment;
  public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
  public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist, AttributeCount;
  public IntPtr Attributes; public string TargetAlias, UserName; }
// CredEnumerate: read `count` IntPtrs from pCredArray, Marshal.PtrToStructure each, then CredFree(pCredArray).
// Blob → Encoding.UTF8.GetString(bytes, 0, CredentialBlobSize)   (keytar and go-keyring both write UTF-8)
// Targets: "gh:github.com:" / "gh:github.com:<login>"; filter "copilot-cli*"; "copilot-language-server/oauth-token-key"
```

---

## 5. Fallback: our own device-flow sign-in

- **Client:** `Iv1.b507a08c87ecfe98`, the Copilot GitHub App. CodexBar mislabels it as "VS Code Client ID"; VS Code's real OAuth app is `01ab8ac9400c4e429b23`.
- **Scope:** `read:user`, as CodexBar and copilot-api use. For GitHub Apps, scope does not control permissions; only `offline_access` has meaning.

```
POST https://github.com/login/device/code
Accept: application/json
Content-Type: application/x-www-form-urlencoded      (the LS sends a JSON body instead; both work)
client_id=Iv1.b507a08c87ecfe98&scope=read:user
→ { "device_code", "user_code", "verification_uri", "expires_in" (default 900), "interval" }
Show user_code and open verification_uri (https://github.com/login/device).
verification_uri_complete: CodexBar uses it if present; GitHub docs do not list it (UNCONFIRMED).

POST https://github.com/login/oauth/access_token     (poll every `interval` seconds)
Accept: application/json
client_id=Iv1.b507a08c87ecfe98&device_code=<device_code>&grant_type=urn:ietf:params:oauth:grant-type:device_code
→ success: { "access_token": "ghu_…", "token_type": "bearer", "scope": "…" }
→ error (HTTP 200 body): authorization_pending | slow_down | expired_token | access_denied | incorrect_device_code | unsupported_grant_type | incorrect_client_credentials | device_flow_disabled
```

- **Polling errors:**
  - `authorization_pending`: keep polling.
  - `slow_down`: add **5 s** to the interval.
  - `expired_token`: restart the flow.
  - `access_denied`: the user cancelled.
  - Check for `error` in the 200 body before looking for `access_token`.
  - GitHub limits browser code submissions to 50 per hour per app.
- **Without `Accept: application/json`**, GitHub replies form-encoded.
- **Token lifetime:** GitHub App user tokens expire after 8 hours **by default** (`expires_in: 28800` plus `refresh_token`) unless the app opts out.
  - Every Copilot client (LS `auth.db` has no refresh column; CodexBar; CopilotChat.nvim) stores only `access_token`, which implies the Copilot app has opted out. This is **inferred**.
  - If a `refresh_token` ever appears, you cannot refresh without the app's client secret. Do not reuse the secrets bundled in the LS. Handle 401 by asking the user to sign in again.
- **Store** with `ProtectedData.Protect(…, DataProtectionScope.CurrentUser)`. Then call `GET https://api.github.com/user` (fields `id`, `login`) to label the account, as CodexBar does.
- **Alternative clients:**
  - Copilot CLI `Ov23ctDVkRmgkPke0Mmm`, scope `read:user,read:org,repo,gist`.
  - gh `178c6fc778ccc68e1d6a`, scopes `repo read:org gist`.

  Both are broader, so not recommended.
- **Risk:** reusing a GitHub-owned client id and calling an undocumented `copilot_internal` endpoint is unofficial. Say so in the settings UI.

---

## 6. Optional documented fallback (usage only, no allowance or reset)

```
GET https://api.github.com/users/{username}/settings/billing/ai_credit/usage
GET https://api.github.com/users/{username}/settings/billing/premium_request/usage
(also /settings/billing/usage and /usage/summary [preview])
Accept: application/vnd.github+json
X-GitHub-Api-Version: 2026-03-10
```

- **Query:** `year`, `month`, `day`, `model`, `product`.
- **Response:** `timePeriod{year,month,day}`, `user`, optional `product` and `model`, and `usageItems[]{product, sku, model, unitType, pricePerUnit, grossQuantity, grossAmount, discountQuantity, discountAmount, netQuantity, netAmount}`.
- **Scope:** "only applicable if the user has purchased their own Copilot plan". Org-managed seats are excluded.
- **Required fine-grained permission:** **UNCONFIRMED** in the docs. The community says Account permissions → "Plan: Read-only".

---

## 7. Open questions still UNCONFIRMED

- Real `premium_interactions` values for monthly Pro, Pro+ and Max under AI credits: is `entitlement` base plus flex, or base only?
- Paid individual `access_type_sku` strings.
- Whether a classic PAT ever works against `/copilot_internal/user`.
- Whether `ghs_` tokens work against `/copilot_internal/user`.
- Whether Copilot CLI 1.0.105 still uses the same keytar target naming.
- Whether `Iv1.b507a08c87ecfe98` device-flow tokens can expire or come with a `refresh_token`.
- Whether `verification_uri_complete` is ever returned.
- Where JetBrains stores its token.


## Verifier: missing_but_important
- Session and weekly Copilot limits exist only as response headers on inference calls: x-usage-ratelimit-session and x-usage-ratelimit-weekly, URL-encoded with ent, ov, ovPerm, rem, rst, hasQuota and tbb (Copilot CLI 1.0.40). The widget cannot poll them from copilot_internal/user, so it can only show the monthly meter. Showing them would require making paid model calls, which is not recommended.
- 'gh auth token' (no flags) refreshes refreshable credentials itself (getTokenWithRefresh). Refreshable records live in keyring service 'gh:_refreshable:<host>', so read through the subprocess, not the keyring. A GH_TOKEN or GITHUB_TOKEN set in the widget's environment would shadow the stored token; run gh with those variables removed from the child environment if you want the stored login.
- VS Code's own GitHub sessions come from OAuth app client id 01ab8ac9400c4e429b23, with scope sets [read:user,user:email,repo,workflow], [user:email] or [read:user]. VS Code calls copilot_internal/user with any of these, so the endpoint does not need special scopes for GitHub's own clients. Which OAuth client is accepted seems to be the real gate (inferred).
- has_quota: VS Code says it is always false per snapshot under token-based billing. Use it only for unlimited (pooled org) snapshots: unlimited && has_quota===false means 'Organization limit reached'. Never treat it as 'exhausted' for metered snapshots.
- In the real Business TBB payload (issue #1258 / CodexBar fixture) the snapshots have no 'unlimited' field. They are {entitlement:0, remaining:0, percent_remaining:100, credits_used:N}. A widget must read credits_used even when it skips the 0-entitlement snapshot, and must not show '0% used'.
- GitHub App user tokens (ghu_) expire after 8 hours by default unless the app opts out. If a device-flow response ever includes refresh_token/expires_in (28800), the widget cannot refresh without the app's client secret. Do not lift the client secrets bundled in copilot-language-server. Treat expiry or 401 as 'sign in again'. All Copilot clients store no refresh token, which suggests the Copilot app has opted out of expiry.
- Device flow: error JSON (authorization_pending etc.) arrives with HTTP 200, so parse the body before checking for access_token. Browser code submissions are rate-limited to 50 per hour per app. The copilot-language-server sends a JSON body; CodexBar sends form-encoded. Both work.
- copilot-language-server auth.db is opened in WAL mode with busy_timeout 5000. To read it safely while an editor is running, copy auth.db plus auth.db-wal and auth.db-shm to a temp folder, or open it with Mode=ReadOnly and a busy timeout.
- Copilot Chat and the CLI also read live quota from x-quota-snapshot-<name> response headers. This is irrelevant for polling, but it explains why VS Code's numbers can be fresher than copilot_internal/user.
- quota_reset_date can include a time (CodexBar fixture '2026-08-01T00:00:00Z'), so parse ISO8601 first and fall back to yyyy-MM-dd as UTC midnight.
- The legacy apps.json/hosts.json may be stale or revoked after migration to auth.db. Always validate every candidate token with a live call and fall through on 401/403/404.

## Verifier: checked claims (non-confirmed only)
- [corrected] Business TBB accounts report entitlement=0, remaining=0, percent_remaining=100 (access_type_sku copilot_for_business_seat_quota) - placeholder; example C in report includes "unlimited": true on both snapshots
  -> The issue body confirms copilot_plan business, token_based_billing true, access_type_sku copilot_for_business_seat_quota, and all three snapshots with entitlement=0, remaining=0, percent_remaining=100. However, neither the issue nor the CodexBar fixture contains an "unlimited" field; the report ADDED "unlimited": true. The real fixture is {entitlement:0, remaining:0, percent_remaining:100, quota_id, credits_used:31}. With VS Code logic that snapshot is skipped (!unlimited && entitlement==0), so credits_used must be read separately from the skipped snapshot, as CodexBar does.
- [corrected] Non-allowlisted OAuth app tokens (OpenCode) get 403 'Resource not accessible by integration' on /copilot_internal/*
  -> The secondary source quotes 'GitHub Copilot API request failed (403): "Resource not accessible by integration"' for the OpenCode OAuth token, but does not say which endpoint (user or v2/token). Treat it as weak evidence. The 'allowlist' idea is an inference.
- [corrected] Source of Copilot session/weekly rate-limit numbers is UNCONFIRMED; not in copilot_internal/user
  -> Found in Copilot CLI 1.0.40. Session and weekly limits arrive only as RESPONSE HEADERS on model (inference) calls: 'x-usage-ratelimit-session' and 'x-usage-ratelimit-weekly' (prefix Pcr='x-usage-ratelimit-', next to 'x-quota-snapshot-<name>'). Values are URL-encoded and parsed by Mcr(): ent (int, -1=unlimited), ov (overage float), ovPerm ('true'/'false'), rem (percent remaining), rst (reset date string), and optional hasQuota and tbb. The CLI warns at 50/75/90/95% used. These cannot be polled from copilot_internal/user, so a passive widget cannot show them without making inference calls.
- [corrected] Copilot CLI $_r status: unlimited / exhausted (remaining<=0, no overage) / overage / warning (<=25%) / normal
  -> Actual logic: isUnlimitedEntitlement → 'unlimited'. Then hasQuota===false || remainingPercentage<=0 → 'overage' if overage permitted, else 'exhausted'. Then remainingPercentage<=25 → 'warning'. Otherwise 'normal'. VS Code notes that has_quota is always false per snapshot under TBB, so do NOT copy the hasQuota===false branch for metered TBB snapshots. Use it only for unlimited (pooled) snapshots, as VS Code isChatQuotaExceeded does.
- [could-not-verify] A Free user may show copilot_plan "free"
  -> This appears only in CodexBar unit-test fixtures. VS Code identifies Free by access_type_sku=='free_limited_copilot' and does not map copilot_plan 'free'. Treat it as UNCONFIRMED.
- [could-not-verify] OpenUsage uses 'gh api /copilot_internal/user'; avante.nvim and UsageButtons read the first github.com key's oauth_token
  -> I did not check these. Only CopilotChat.nvim was verified from source. None of them is needed for the implementation.
- [could-not-verify] Exact numeric semantics of premium_interactions for monthly individual AI-credit plans and paid individual access_type_sku strings
  -> Still UNCONFIRMED. No primary source shows a real Pro/Pro+/Max TBB payload. CodexBar docs say GitHub publishes no included-credit ceiling for Business seats on any documented endpoint.
- [corrected] C# DTO: QuotaResetAt as long?, MonthlyQuotas as Dictionary<string,double>
  -> quota_reset_at is a zod number (ln()) and a JS number, not necessarily an integer. Use double? and convert with DateTimeOffset.FromUnixTimeMilliseconds((long)(v*1000)). Dictionary values should be double? so that a null cannot throw. Better still, parse quota_snapshots entries one by one with JsonElement so that one malformed entry does not fail the whole response (the CLI uses catchall(nullable)).