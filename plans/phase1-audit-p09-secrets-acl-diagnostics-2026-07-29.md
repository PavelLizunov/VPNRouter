# Phase 1 Audit Remediation — P09 Secrets, ACL and Diagnostics

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~3-4 h
**Risk**: MEDIUM (logging changes are broad; ACL change is Windows-specific and must preserve service access)
**Blast radius**: 3 Core product files + 1 packaging script + tests
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| SEC-1 | P1 | CONFIRMED | **P1** | High |
| SEC-2 | P1 | CONFIRMED | **P1** | High (code) / Med (exploit) |
| OBS-1 | P1 | CONFIRMED | **P1** | High |

ONLY SEC-1, SEC-2, OBS-1. Explicitly NOT in scope: SEC-3 (wgturn argument
injection, P2), OBS-2 (crash log tail memory, P2) — separate packages.

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

Three distinct security/diagnostics defects:

- **SEC-1** — full subscription URLs (commonly embedding provider tokens in the
  path/query) are written verbatim to the primary log at 10+ sites in
  `SubscriptionFetcher.cs`. The on-disk log is written BEFORE any diagnostics
  redaction. Any local log reader or third party handed the raw log obtains
  subscription-provider credentials.
- **SEC-2** — `%ProgramData%\VPNRouter` is created with no restrictive ACL.
  `install.ps1` adds Defender exclusions only. Via standard Windows default
  `%ProgramData%` inheritance, a local unprivileged user on a shared box can
  read `config.yaml`, `current.json`, and logs containing VPN credentials,
  UUIDs, and subscription tokens.
- **OBS-1** — `ClashLogStream` builds a `ws(s)://.../logs?level=info&token=<secret>`
  URI when a secret is supplied and logs that URI verbatim at Information on each
  /logs reconnect. Today `VpnEngine` constructs the stream WITHOUT a secret
  (`VpnEngine.cs:983`), so no token is currently logged — but the ctor already
  accepts `secret`, making this a LATENT leak that activates the moment the secret
  is wired through. The crash-report scrubber would NOT catch it: it does not
  recognize `ws://`/`wss://` schemes (scheme list lacks them), matches only
  `https?://` for HTTP URLs, and needs >=40 chars for base64 — while the clash
  secret is only 32 hex chars — so a `ws://...&token=<32hex>` line is untouched.

## Current root cause (verified against current code)

### SEC-1
- [FACT] `VPNRouter.Core/Services/SubscriptionFetcher.cs:62` —
  `logger?.Information("[Subscription] Fetching {Url}", url);`
- [FACT] `:72` — `logger?.Warning("[Subscription] HTTP {Status} from {Url}", httpResp.StatusCode, url);`
- [FACT] `:88` — `logger?.Warning("[Subscription] Empty response from {Url}", url);`
- [FACT] `:101-103` — placeholder-drop warning logs `{Url}` with the raw URL.
- [FACT] `:106` — `logger?.Information("[Subscription] Fetched {Count} servers from {Url}", result.Count, url);`
- [FACT] `:110` — `logger?.Error(ex, "[Subscription] Fetch failed for {Url}", url);`
- [FACT] `:324-326` — `RefreshEntryAsync` placeholder warning logs `entry.Url`.
- [FACT] `:343-344` — `RefreshEntryAsync` zero-server warning logs `entry.Url`.
- [FACT] The only `ScrubSecrets` call in the file is at `:272` on a failing
  parse-line's CONTENT (the URI line, not the subscription URL).
- [FACT] A URL-redaction helper ALREADY EXISTS and is the correct reuse target:
  `CanaryPolicy.RedactUrl(string? url)` — `public static`, in
  `VPNRouter.Core/Services/CanaryPolicy.cs:67`, same `VPNRouter.Core.Services`
  namespace as `SubscriptionFetcher`. It returns `scheme://host` (drops path,
  query, fragment), maps null/empty → `"(none)"`, never throws on a malformed
  URL (coarse redaction up to the first `/`,`?`,`#`). It is already used for
  exactly this "never log a full URL" purpose at `VlessDeepVerifier.cs:544` and
  is pinned by `VPNRouter.Tests/CanaryPolicyTests.cs` (`RedactUrl_StripsPathAndQuery`,
  `RedactUrl_Empty_IsNone`, `RedactUrl_Malformed_DoesNotThrow_AndDropsPath`).
- [FACT] A second helper exists but is NOT the reuse target:
  `DiagnosticsRedactor.RedactUrlKeepHost(string value)` is `private static`
  (`VPNRouter.Core/Services/Diagnostics/DiagnosticsRedactor.cs:296`), keeps the
  port and appends `/[REDACTED]`, and is only reachable inside `DiagnosticsRedactor`
  for URL-keyed config values. Reusing it from `SubscriptionFetcher` would require
  widening its visibility — a larger change than calling the already-public
  `CanaryPolicy.RedactUrl`. Do NOT add a third redactor.
- [INFER] SEC-1 is therefore a CALL-SITE fix: route the 10+ `{Url}` log arguments
  through the existing `CanaryPolicy.RedactUrl`. No new helper, no visibility change.

### SEC-2
- [FACT] `VPNRouter.Core/AppPaths.cs:112` —
  `Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter")`.
- [FACT] `:99-108` — `EnsureDirectories` = plain `Directory.CreateDirectory`
  for `DataDir`, `LogsDir`, `CacheDir`, `BinDir`.
- [FACT] `packaging/windows/install.ps1` — creates `$DataRoot` + Defender
  exclusions only. Zero `icacls`/`Set-Acl`/`FileSystemAccessRule`/`SetAccessControl`
  matches in `packaging/windows`.
- [INFER] Via standard Windows default `%ProgramData%` inheritance, Users get
  read on subfolders. Impact: credential disclosure on shared/multi-user boxes.
  Exploitability rests on default-ACL inheritance (Medium confidence).

### OBS-1
- [FACT] `VPNRouter.Core/Services/ClashLogStream.cs` `BuildLogsUri` (~:93-94) —
  `$"&token={Uri.EscapeDataString(secret)}"` is appended to the ws(s) URI ONLY
  when a non-empty `secret` is supplied (the ctor's `secret` param defaults null).
- [FACT] `RunAsync` (~:133) — `Information("[ConnHealth] Clash /logs stream connected ({Uri})", _logsUri)`
  logs `_logsUri` verbatim, so IF a secret is wired through, the full
  `ws(s)://.../logs?level=info&token=<secret>` URI lands in the primary log.
- [FACT] **`VpnEngine` does NOT currently pass the secret.** The only production
  construction, `VpnEngine.TryStartConnectionHealthStream` (`VpnEngine.cs:983`),
  calls `new ClashLogStream($"http://127.0.0.1:{clashPort}", _connHealth,
  proxyEndpoints: null, logger: _logger)` with NO `secret:` argument → `secret`
  is null → today's logged URI carries no `&token=`. The disclosure is therefore
  LATENT: the ctor already accepts `secret`, so any future change that wires the
  secret through (e.g. to authenticate the /logs stream) would start logging the
  token unless the log statement is fixed first. This fix removes that latent leak.
- [FACT] `VPNRouter.Core/Services/CrashReporter.cs:169-171` — `_proxyUriPattern`
  schemes: `vless|vmess|trojan|ss|hysteria2?|tuic|naive|amneziawg|awg` — NO ws/wss.
- [FACT] `:173-175` — `_httpUrlPattern` = `https?://` only (does not match ws/wss).
- [FACT] `:181-183` — `_longBase64Pattern` = `[A-Za-z0-9+/_\-]{40,}` needs >=40 chars.
- [FACT] The clash_api secret is exactly **32 hex chars** —
  `AppSettingsSane.GenerateClashApiSecret()` (`AppSettingsSane.cs:20-22`) is
  `Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()`
  (doc: "32-hex-char cryptographically random Clash-API bearer secret"). 32 < 40,
  so `_longBase64Pattern` does NOT match it; combined with the ws/wss scheme gap,
  a `ws://...&token=<32hex>` line survives `ScrubSecrets` untouched.
- [FACT] `:191` — `ScrubSecrets` applies all patterns.
- [INFER] The clash_api_secret is generated once when settings are sanitized
  (`AppSettingsSane.EnsureSane` fills an empty `SingBox.ClashApiSecret`), persisted
  in `config.yaml`, and injected into the generated sing-box runtime config
  (`current.json` `clash_api.secret`) by `CustomConfigInjector.EnsureClashApi`
  (~:1365). It is not a user credential but grants local API access to the
  sing-box Clash API (can read connection metadata).

## What

### Minimal expected file list
- `VPNRouter.Core/Services/SubscriptionFetcher.cs` — redact URLs in all log
  statements (SEC-1).
- `VPNRouter.Core/Services/ClashLogStream.cs` — remove token from the logged
  URI (OBS-1).
- `VPNRouter.Core/Services/CrashReporter.cs` — extend `ScrubSecrets` to cover
  `ws://`/`wss://` and `token=`/key-value forms (OBS-1).
- `packaging/windows/install.ps1` — add restrictive ACL on `$DataRoot` (SEC-2).
- `VPNRouter.Core/AppPaths.cs` — add runtime ACL enforcement in
  `EnsureDirectories` (SEC-2, Windows-only).
- `VPNRouter.Tests/SubscriptionUrlRedactionTests.cs` (new, SEC-1).
- `VPNRouter.Tests/CrashReporterScrubberTests.cs` (extend, OBS-1).

### Explicit non-goals
- Do NOT fix SEC-3 (wgturn argument injection, P2) — separate package.
- Do NOT fix OBS-2 (crash log tail memory, P2) — separate package.
- Do NOT change the subscription fetch/parse pipeline behavior.
- Do NOT change the Clash WebSocket connection logic.
- Do NOT add a new/third URL-redaction helper and do NOT widen
  `DiagnosticsRedactor.RedactUrlKeepHost` visibility — SEC-1 reuses the existing
  public `CanaryPolicy.RedactUrl` (see root cause). `CrashReporter.ScrubSecrets`
  stays the crash-report scrubber; it is NOT the URL redactor for SEC-1.
- Do NOT change Linux/macOS file permissions (POSIX umask is the default;
  SEC-2 is Windows-specific per the `%ProgramData%` path).
- Do NOT recursively ACL the entire install directory (only the data root).

## How (ordered; fix each shared root cause once)

### SEC-1 (subscription URL redaction)
1. Reuse the existing public `CanaryPolicy.RedactUrl(string? url)`
   (`VPNRouter.Core/Services/CanaryPolicy.cs:67`, same namespace) — do NOT add a
   new helper. It returns `scheme://host` (strips path, query, fragment), maps
   null/empty → `"(none)"`, and never throws on malformed input. Example:
   `https://provider.com/api/sub?token=abc123` → `https://provider.com`. (Note:
   it uses `Uri.Host`, so a non-default port is dropped — acceptable here; the
   goal is provider identity, not endpoint detail.)
2. Replace all `{Url}` log arguments with `CanaryPolicy.RedactUrl(url)` /
   `CanaryPolicy.RedactUrl(entry.Url)` at the 10+ sites listed in the root cause
   (:62,:72,:88,:101-103,:106,:110,:324-326,:343-344). The log message templates
   stay the same; only the argument changes. Leave the `:272`
   `CrashReporter.ScrubSecrets(line)` parse-line scrub as-is (different concern).
3. Preserve diagnosability: the scheme+host identifies the provider; the
   path/query (token) is the sensitive part.

### OBS-1 (clash token logging + scrubber)
4. In `ClashLogStream.cs:133`, log only the host+path portion of `_logsUri`,
   NOT the query string containing `token=`. Example:
   ```csharp
   var safeUri = $"{_logsUri.Scheme}://{_logsUri.Host}:{_logsUri.Port}{_logsUri.AbsolutePath}";
   Information("[ConnHealth] Clash /logs stream connected ({Uri})", safeUri);
   ```
5. In `CrashReporter.cs`, extend `_proxyUriPattern` to include `ws|wss` in the
   scheme alternation. Add a new `_tokenParamPattern` regex:
   `[?&]token=[^&\s]+` → replace with `[?&]token=[REDACTED]`. Apply it in
   `ScrubSecrets` after the existing patterns. This catches any residual
   `token=` in crash-report log tails.

### SEC-2 (ProgramData ACL)
6. In `packaging/windows/install.ps1`, after creating `$DataRoot`, add an
   idempotent ACL restriction:
   ```powershell
   # Remove inherited Users read; grant SYSTEM + Administrators FullControl,
   # current user Modify. Idempotent: re-running does not stack ACEs.
   $acl = Get-Acl $DataRoot
   $acl.SetAccessRuleProtection($true, $true)  # disable inheritance, keep existing
   $usersRule = New-Object System.Security.AccessControl.FileSystemAccessRule("Users","ReadAndExecute","ContainerInherit,ObjectInherit","None","Allow")
   $acl.RemoveAccessRule($usersRule) | Out-Null
   Set-Acl $DataRoot $acl
   ```
   Preserve: SYSTEM FullControl, Administrators FullControl, current user Modify.
   Remove: Users ReadAndExecute (the default inheritance that exposes credentials).
7. In `AppPaths.cs` `EnsureDirectories`, add a Windows-only runtime ACL check
   (guarded by `OperatingSystem.IsWindows()`): if the data directory inherits
   Users-read, apply the same restriction. This covers the case where the app
   creates the directory at runtime (first-run without installer). Use
   `DirectoryInfo.GetAccessControl()` / `SetAccessControl()`. Idempotent.
8. Preserve Windows Service access: the service runs as `LocalSystem` (SYSTEM),
   which retains FullControl. The current user retains Modify (needed for
   `SettingsLoader.Save`, log writes, cache writes).

## Callers / consumers to preserve

SEC-1:
- `SubscriptionFetcher.FetchWithDiagnosticsAsync` — the fixed method. All
  callers (`FetchAsync`, `RefreshEntryAsync`, `SubscriptionResolver`,
  `MainWindowViewModel.Subscriptions.cs`) receive the same entries; only log
  output changes.
- `CanaryPolicy.RedactUrl` — the reused public URL redactor (already called by
  `VlessDeepVerifier.cs:544`); unchanged contract, no visibility change.
- `CrashReporter.ScrubSecrets` — still reused by `SubscriptionFetcher:272`
  (parse-line scrub) and `DiagnosticsExporter`; unchanged contract. It is NOT
  the URL redactor for the `{Url}` sites.

OBS-1:
- `ClashLogStream` — created by `VpnEngine.TryStartConnectionHealthStream`
  (`VpnEngine.cs:983`, post-start, env-flag gated via `VPNROUTER_CONN_HEALTH`),
  currently WITHOUT a `secret`. The WebSocket connection URI is unchanged; only
  the LOG output changes. The fix is latent-leak hardening (see root cause).
- `CrashReporter.ScrubSecrets` — extended; all existing callers benefit.
- `DiagnosticsExporter.TailLines` — reads log tails; benefits from the
  extended scrubber on crash-report copies.

SEC-2:
- `AppPaths.EnsureDirectories` — called at app startup, CLI start, service start.
- `install.ps1` — called by the Windows one-liner installer.
- `SettingsLoader.Save/Load` — reads/writes `config.yaml` in `DataDir`;
  the current user retains Modify → unaffected.
- `VPNRouter.Service` — runs as SYSTEM; retains FullControl → unaffected.
- P05 (DATA-1) atomic save: creates `config.yaml.tmp` in `DataDir`; inherits
  the directory ACL; `File.Move(overwrite:true)` preserves the TARGET's ACL.
  **Coordinate**: confirm with P05 that the temp file needs no explicit ACL
  before rename (Windows inherited dir ACL covers it).

## Regression tests (exact)

The pure redaction shape is ALREADY pinned by `VPNRouter.Tests/CanaryPolicyTests.cs`
(`RedactUrl_StripsPathAndQuery`, `RedactUrl_Empty_IsNone`,
`RedactUrl_Malformed_DoesNotThrow_AndDropsPath`) — do NOT duplicate it. The new
tests target the SEC-1 CALL-SITE behavior in `SubscriptionFetcher`.

New `VPNRouter.Tests/SubscriptionUrlRedactionTests.cs`:
- `FetchAsync_LogsDoNotContainToken` — **core SEC-1 pin.** Use `FakeHttpClient`
  with a subscription URL containing `?token=secret123` (and a non-trivial path).
  Capture log output (Serilog `Log.Logger` with a test sink). Assert NO log line
  contains `token=secret123` or the URL path. Assert at least one log line
  contains the redacted `scheme://host`.
- `RefreshEntryAsync_LogsDoNotContainToken` — same assertion for the
  `RefreshEntryAsync` warning sites (`:324-326`, `:343-344`) using a subscription
  entry whose `Url` embeds a token.
- (Optional, only if a direct-helper assertion is wanted) match the REAL
  `CanaryPolicy.RedactUrl` contract: `RedactUrl("https://provider.com/api/sub?token=abc123")`
  → `"https://provider.com"`; `RedactUrl("http://example.com:8080/path")` →
  `"http://example.com"` (port dropped — `Uri.Host` excludes it); `RedactUrl(null)`
  / `RedactUrl("")` → `"(none)"` (NOT the input unchanged).

Extend `VPNRouter.Tests/CrashReporterScrubberTests.cs` (or new file if none exists):
- `ScrubSecrets_RedactsWsTokenUri` — input:
  `"connected ws://127.0.0.1:9090/logs?level=info&token=abc123"`. Assert output
  does NOT contain `token=abc123`. Assert output contains `ws://127.0.0.1:9090/logs`
  (host/path preserved, token redacted).
- `ScrubSecrets_RedactsWssTokenUri` — same with `wss://`.
- `ScrubSecrets_ExistingProxyUriPatterns_Unchanged` — input with `vless://...`
  still redacted. Pins no regression on existing patterns.

SEC-2 — no automated ACL test (requires Windows + multi-user setup). Static
verification: grep `install.ps1` for `Set-Acl`/`FileSystemAccessRule` presence.
Note in Outcome: "ACL change verified by static inspection; live multi-user
test deferred."

Must stay green: `SubscriptionFetcherParserTests.cs` (all), existing
`CrashReporter` tests (if any), `SettingsLoaderRobustnessTests.cs`.

## Risks

- **Security**: SEC-1 removes credential-bearing URLs from the primary log.
  OBS-1 removes the clash API token from logs and crash reports. SEC-2 restricts
  the data directory ACL. All three reduce the credential-disclosure surface.
- **Compatibility**: SEC-1 log output changes (host-only instead of full URL).
  Diagnosability preserved (scheme+host identifies the provider). OBS-1 log
  output changes (no query string). SEC-2 ACL change is Windows-only; Linux/macOS
  use POSIX umask (unchanged). The ACL change is idempotent and preserves
  SYSTEM/Administrators/current-user access.
- **Cross-platform**: SEC-1 and OBS-1 are cross-platform Core changes. SEC-2 is
  Windows-only (guarded by `OperatingSystem.IsWindows()` in AppPaths; `install.ps1`
  is Windows-only by nature).
- **Rollback**: per-file revert. No schema/migration/wire-format change.
- **P05 coordination**: P05's atomic save creates `config.yaml.tmp` in `DataDir`.
  The temp file inherits the directory ACL; `File.Move(overwrite:true)` preserves
  the target's ACL. No explicit ACL needed on the temp file. Confirm ordering:
  if P09 lands first, P05's temp file inherits the restricted ACL (correct).
  If P05 lands first, P09's ACL change applies to the directory (correct).

## Dependencies and file overlap with the other seven packages

- **P05 (DATA-1)**: coordinate per Risks (ACL interaction with atomic save temp
  file). Different files (`SettingsLoader.cs` vs `AppPaths.cs`/`install.ps1`).
  Sequence-independent but verify ACL inheritance.
- **P01 (UPD-1/UPD-2)**: no overlap.
- **P02 (FAIL-1)**: no overlap.
- **P06 (FLOW-1)**: no overlap.
- **P07 (CLI/Android)**: no overlap (P07's AND-1 is Android Java; P09's OBS-1
  is C# Core).
- **P08 (SUP-1)**: no overlap.
- **P10 (ZAP-1)**: no overlap.
- No blocking dependency on any other package.

## Zone CLAUDE.md constraints

- `VPNRouter.Core/CLAUDE.md`: Core is a pure C# library; `SubscriptionFetcher`
  and `ClashLogStream` are Core services. `CanaryPolicy.RedactUrl` is the shared
  URL redactor (SEC-1 reuse target); `CrashReporter.ScrubSecrets` is the
  crash-report scrubber (OBS-1 extends it). `InternalsVisibleTo VPNRouter.Tests`
  configured.
- `packaging/CLAUDE.md`: documents the install.ps1 one-liner, sha256 sidecar
  verification, Defender exclusions. ACL addition is in-zone.
- `.github/workflows/CLAUDE.md`: N/A (P09 does not touch CI workflows).
- No emoji (AGENTS.md #9).

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; CI compiles 0 errors. Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: new `SubscriptionUrlRedactionTests` + extended scrubber tests green in CI; full existing suite stays green.
- [ ] **Gate 3 — Docs**: brief Outcome filled after CI; no README change expected.
- [ ] **Gate 4 — Self-review**: Qwen static self-review; **security review** of the URL redaction, token scrubbing, and ACL change (all security-relevant).
- [ ] **Gate 5 — UI/live**: DEFERRED by explicit owner constraint (no local launch/MCP/VM). Do NOT fake PASS. Note "deferred — ACL change not live-verified on multi-user box" in Outcome.
- [ ] **Gate 6 — Characterization**: N/A (no god-file split; no MVM surface change).

## Outcome

**Status**: IMPLEMENTED / REMOTE CI GREEN
**Commits**: `d857fa6e` (fix(security): redact secrets and restrict data ACL)
**Pushed**: draft PR #60, branch `codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29`
**Test deltas**: +175 / -0 (1 new test file: `SubscriptionUrlRedactionTests.cs` +115; extended existing `CrashReporterScrubberTests.cs` +32, `ClashLogStreamTests.cs` +28)
**Files changed**: 8 · +323 / -9

**Gate results:**
- [x] Gate 1 build (remote CI): PASS — dotnet test run 30446800880 SUCCESS
- [x] Gate 2 tests (remote CI): PASS — run 30446800880 SUCCESS; new `SubscriptionUrlRedactionTests`, extended `CrashReporterScrubberTests` and `ClashLogStreamTests` green; full existing suite stayed green
- [x] Gate 3 docs: PASS — Outcome filled; no README change needed
- [x] Gate 4 self-review / security-review: PASS — static self-review performed during implementation; URL redaction (SEC-1), token scrubbing (OBS-1), and ACL change (SEC-2) reviewed
- [-] Gate 5 UI/live: deferred (owner constraint) — ACL change not live-verified on multi-user box; multi-user ACL live validation deferred
- [-] Gate 6 characterization: N/A

**Local build/test**: NOT run. The mandatory git hook attempted SDK resolution and found SDK 10.0.301 absent; this is not a pass.
**Surprises encountered**: none
**Follow-ups spawned**: raw subscription URL exception logs in App ViewModels remain outside this package (SEC-1 covers `SubscriptionFetcher.cs` Core sites only).
**Rollback**: `git revert d857fa6e` / branch delete
