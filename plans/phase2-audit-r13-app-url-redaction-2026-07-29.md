# Phase 2 — R13 — App-layer URL redaction + wgturn diagnostics scrub

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r13-app-url-redaction-2026-07-29`
**Base**: commit `35bf7ade` (R06 — `fix(security): harden wgturn args and crash tail`).
Verified: `git rev-parse HEAD` = `35bf7ade048c33107cceb7fcf7aac52ffc45ec50`, current
branch `codex/qwen-audit-r13-app-url-redaction-2026-07-29` points at the R06 tip.
**Dependency**: directly stacked on R06 — PR #68
(branch `codex/qwen-audit-r06-security-diagnostics-2026-07-29`, tip `35bf7ade`);
transitively depends on P09 — PR #60
(branch `codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29`, commit `d857fa6e`,
brief `phase1-audit-p09-secrets-acl-diagnostics-2026-07-29.md`).
P09 introduced the `CanaryPolicy.RedactUrl` reuse pattern (applied it to all 10+
`SubscriptionFetcher.cs` Core sinks) and the `DiagnosticsRedactor`. R06 (the base)
rides on P09 and already hardened wgturn launch args + crash tail. R13 extends the
P09 redaction pattern to the App-layer ViewModel sinks and closes the `wgturn://`
gap in the shared log scrubber. Do NOT rebase R13 onto `origin/main` while
P09/R06 are unmerged.
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R13)
**IDs**: R13-A (App-layer raw URL log sinks), R13-B (wgturn diagnostics scrub gap),
R13-P3 (raw `ex.Message` UI surfaces — follow-up, NOT in scope)
**Effort**: ~1 h
**Risk**: LOW (logging-argument substitution with an existing pinned helper + one
regex alternation addition; no control flow, no I/O, no public API change)
**Blast radius**: 2 App ViewModel partials + 1 Core scrubber regex + tests ·
~+12 LOC product · runtime: log line content + diagnostics/crash scrub only
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final verdict / severity / confidence / corrected scope

| ID | Verdict | Final | Conf |
|---|---|---|---|
| R13-A | CONFIRMED | P2 | High (mech) / Med (input) |
| R13-B | CONFIRMED | P2 | High |
| R13-P3 | CONFIRMED (distinct path) | P3 follow-up | High |

Corrected scope:

- **R13-A**: four App-layer ViewModel log sinks write the user's full
  subscription / wgturn URL verbatim to the on-disk log. Same defect class as
  P09/SEC-1 but at the VM layer (4 sites vs P09's 10+ Core sites). Severity P2:
  the high-volume Core fetcher path is already fixed by P09; these are the VM's
  catch-block logs, the URLs are the user's own config (attacker model = local
  log reader / shared box / handed-to-support raw log), so impact is real but
  narrower than SEC-1.
- **R13-B**: `CrashReporter.ScrubSecrets` does NOT recognize the `wgturn://`
  scheme, so a raw wgturn URI in any `vpnrouter*.log` survives into BOTH the
  crash-report tail and the shareable diagnostics bundle. Source redaction of
  R13-A sink #4 alone does NOT close this — historical logs already on user
  machines (written before R13 ships) still carry raw `wgturn://`, and the
  diagnostics bundle pulls the last 40 000 lines per daily-rolled log. A
  scrubber fix is REQUIRED, not optional.
- **R13-P3**: two UI surfaces render raw `ex.Message` to the user. This is a
  DISTINCT data source (exception message, not the URL argument) and is NOT
  covered by R13-A source redaction. Left as a separate P3 follow-up so R13
  stays a tight logging-redaction package.

## 2. Verified current root cause (commit `35bf7ade`)

### R13-A — four raw URL log sinks (App layer)

All four log a USER-controlled URL verbatim as the `{Url}` template argument.
None routes through `CanaryPolicy.RedactUrl`.

[FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:141`
(`RefreshSubscriptionAsync` catch):
```csharp
catch (Exception ex)
{
    _logger.Error(ex, "[VM] RefreshSubscription failed for {Url}", sub.Url);
```

[FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:179`
(`RefreshAllSubscriptionsAsync` per-entry catch):
```csharp
catch (Exception ex)
{
    _logger.Warning(ex, "[VM] Refresh of {Url} failed", s.Url);
```

[FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:320`
(subscription auto-refresh timer path):
```csharp
_logger.Warning(ex, "[SubRefresh] Failed for {Url}", s.Url);
```

[FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.Wgturn.cs:488`
(`AddWgturnConfig` structural-parse failure):
```csharp
if (!EmergencyChannelConfig.TryParse(rawUrl, out _))
{
    _logger.Warning("[Wgturn] AddWgturnConfig: URL failed structural parse: {Url}", rawUrl);
    return;
}
```

The subscription URLs (`sub.Url` / `s.Url`) commonly embed a provider token in
the path/query (same shape as SEC-1: `https://provider.example/api/sub?token=…`).
The wgturn URL (`rawUrl`) is a `wgturn://…` URI carrying wireguard key material
+ a VK link. All four reach the on-disk `vpnrouter*.log` BEFORE any diagnostics
redaction.

### R13-A reuse target — `CanaryPolicy.RedactUrl` (already public, already pinned)

[FACT] `VPNRouter.Core/Services/CanaryPolicy.cs:67`:
```csharp
public static string RedactUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return "(none)";
    if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        return $"{u.Scheme}://{u.Host}";
    var end = url.IndexOfAny(new[] { '/', '?', '#' });
    return end >= 0 ? url[..end] : url;
}
```
Contract: absolute URI → `scheme://host` (drops path, query, fragment AND port);
null/whitespace → `"(none)"`; malformed → coarse redaction up to first `/`,`?`,`#`;
never throws. Namespace `VPNRouter.Core.Services` — already imported by the App
ViewModels (they use other Core services). Pinned by
`VPNRouter.Tests/CanaryPolicyTests.cs` (`RedactUrl_StripsPathAndQuery`,
`RedactUrl_Empty_IsNone`, `RedactUrl_Malformed_DoesNotThrow_AndDropsPath`).

[FACT] P09 already applied this exact helper to the Core fetcher sinks —
`SubscriptionFetcher.cs:62,72,88,103,106,110,326,344` all call
`CanaryPolicy.RedactUrl(...)`, and `VlessDeepVerifier.cs:544` too. R13 reuses the
SAME helper at the 4 App sinks. No new abstraction, no visibility change, no
third redactor (per P09 brief §SEC-1).

### R13-B — `wgturn://` gap in the shared log scrubber

[FACT] `VPNRouter.Core/Services/CrashReporter.cs:170-171`:
```csharp
private static readonly Regex _proxyUriPattern = new(
    @"\b(vless|vmess|trojan|ss|hysteria2?|tuic|naive|amneziawg|awg)://\S+",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
```
The scheme alternation lacks `wgturn`. `ScrubSecrets` (`CrashReporter.cs`,
`public static`) applies, in order: `_proxyUriPattern` → `scheme://[redacted]`;
`_httpUrlPattern` (`https?://` only); `_uuidPattern`; `_longBase64Pattern`
(≥40 chars); `_tokenParamPattern` (`token=` only). A raw `wgturn://…` URI matches
NONE of these as a URI: it is not http(s); any embedded UUID/base64 substring is
redacted piecemeal but the `wgturn://` scheme, host and short params survive.

[FACT] Two export paths route free-form log text through `ScrubSecrets`:
- Crash report tail — `CrashReporter.WriteReport`:
  `foreach (var line in DiagnosticsExporter.TailLines(logs, 200).Split(...)) sb.AppendLine(ScrubSecrets(line));`
- Diagnostics bundle — `VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs:469-470`:
  ```csharp
  var tail = TailLines(sourcePath, LogTailLines);          // LogTailLines = 40_000 (:28)
  File.WriteAllText(Path.Combine(staging, outName), DiagnosticsRedactor.RedactLogText(tail));
  ```
  and `DiagnosticsRedactor.RedactLogText` calls `CrashReporter.ScrubSecrets(lines[i])`
  per line. The bundle pulls the last 40 000 lines of each daily-rolled
  `vpnrouter*.log` over `LogWindowDays` (`DiagnosticsExporter.cs:95,490-491`).

[FACT] Structured config export is NOT the gap: `DiagnosticsRedactor.UrlKeys`
already includes `"wgturn_url"`, `"vk_link"`, `"url"` → `RedactUrlKeepHost`
(keeps scheme+host, drops path/query). So `config.yaml` / `current.json`
structured values are handled; only the free-form LOG-TEXT path leaks wgturn.

[INFER] Therefore a raw `wgturn://` URI written to any log (R13-A sink #4 today,
or historical logs already on disk) survives into both the crash report and the
shareable diagnostics bundle. Adding `wgturn` to `_proxyUriPattern` is the
minimal, idiomatic close (existing pattern, no new abstraction); it yields
`wgturn://[redacted]`, matching the proxy-URI treatment (full payload dropped,
scheme kept for diagnostic value).

### R13-P3 — raw `ex.Message` UI surfaces (distinct path, out of scope)

[FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:231`
(`SyncSubscriptionAsync` catch):
```csharp
catch (Exception ex)
{
    _logger.Error(ex, "[VM] Subscription sync failed");
    StatusText = Strings.SyncFailed(ex.Message);
}
```
(Note: the LOG at :230 is already safe — it does not pass the URL. Only the UI
`StatusText` renders raw `ex.Message`.)

[FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs:492-493`
(subscription refresh catch):
```csharp
SmpErrorText = IsRussian
    ? $"Не удалось получить подписку: {ex.Message}"
    : $"Couldn't fetch the subscription: {ex.Message}";
```
These render the raw exception message to the user. An upstream exception message
can echo a request URI, so this is a real but separate disclosure surface. It
reads `ex.Message`, NOT the URL argument, so R13-A source redaction does not cover
it. Tracked as a P3 follow-up; not expanded into R13.

## 3. Why

R13-A: the App-layer ViewModel sinks leak the user's full subscription token /
wgturn credential into the on-disk log verbatim — the same defect P09 fixed at
the Core fetcher, surviving at the VM layer. R13-B: even with R13-A fixed, raw
`wgturn://` URIs already in historical logs (and any future log line) leak into
the crash report and the support-shareable diagnostics bundle because the shared
scrubber does not know the `wgturn` scheme. Both have an existing in-repo pattern
that fixes them minimally.

## 4. What

1. **R13-A** — wrap the four `{Url}` arguments in `CanaryPolicy.RedactUrl(...)`:
```diff
- _logger.Error(ex, "[VM] RefreshSubscription failed for {Url}", sub.Url);
+ _logger.Error(ex, "[VM] RefreshSubscription failed for {Url}", CanaryPolicy.RedactUrl(sub.Url));
```
```diff
- _logger.Warning(ex, "[VM] Refresh of {Url} failed", s.Url);
+ _logger.Warning(ex, "[VM] Refresh of {Url} failed", CanaryPolicy.RedactUrl(s.Url));
```
```diff
- _logger.Warning(ex, "[SubRefresh] Failed for {Url}", s.Url);
+ _logger.Warning(ex, "[SubRefresh] Failed for {Url}", CanaryPolicy.RedactUrl(s.Url));
```
```diff
- _logger.Warning("[Wgturn] AddWgturnConfig: URL failed structural parse: {Url}", rawUrl);
+ _logger.Warning("[Wgturn] AddWgturnConfig: URL failed structural parse: {Url}", CanaryPolicy.RedactUrl(rawUrl));
```
Ensure `using VPNRouter.Core.Services;` is present in both partials (it is — they
already use Core services; verify, do not add a duplicate).

2. **R13-B** — add `wgturn` to the `_proxyUriPattern` alternation in
`CrashReporter.cs:171`:
```diff
- @"\b(vless|vmess|trojan|ss|hysteria2?|tuic|naive|amneziawg|awg)://\S+",
+ @"\b(vless|vmess|trojan|ss|hysteria2?|tuic|naive|amneziawg|awg|wgturn)://\S+",
```
No other scrubber change. `RedactLogText` (diagnostics) and `WriteReport`
(crash) both flow through this regex, so one edit closes both export paths and
also covers historical logs already on disk.

## 5. How (ordered minimal steps)

1. Read P09's applied pattern in `SubscriptionFetcher.cs` (already in this base)
   to match style exactly; confirm `CanaryPolicy.RedactUrl` is the reuse target.
2. R13-A: apply the four `CanaryPolicy.RedactUrl(...)` wraps. Verify each partial's
   `using VPNRouter.Core.Services;`.
3. R13-B: add `wgturn` to `_proxyUriPattern`. Confirm no other scheme list in the
   file needs it (only `_proxyUriPattern` matches proxy-style schemes; `_httpUrlPattern`
   is https?-only by design).
4. Add the two minimal tests below. Static review: grep the four sites + the regex
   to confirm no raw URL/`wgturn://` survives; confirm no new log line introduces a
   raw secret.

### Tests written (one per DISTINCT security path)

- **Source log redaction path (R13-A)** —
  `VPNRouter.Tests/AppUrlRedactionSourceTests.cs` (new file, one `[Theory]` over
  the 4 sinks = 4 cases, source-shape contract; per-file `FindRepoFile` mirrors
  `HealthMonitorRecoveryGapTests`). Each case reads its partial and pins that the
  `{Url}` argument IS `CanaryPolicy.RedactUrl(...)` (positive) and that the raw
  unwrapped argument is GONE (negative). Source-shape, not runtime:
  `MainWindowViewModel` builds its own non-injectable Serilog logger and three
  sinks fire only on a network failure, so a capturing-logger test would need a
  production seam + broad VM fixture (out of scope). **Limitation**: verifies call
  shape, not rendered output — the redaction shape (scheme://host, token dropped)
  is pinned by `CanaryPolicyTests` + P09's `SubscriptionUrlRedactionTests`, so
  together they prove no raw URL reaches the log.
- **wgturn diagnostics scrub path (R13-B)** — extended the EXISTING
  `VPNRouter.Tests/CrashReporterScrubberTests.cs` (+1 `[Fact]`,
  `ScrubSecrets_RedactsWgturnUri`): `ScrubSecrets("add config
  wgturn://abc123/xyz?k=secret failed")` → contains `wgturn://[redacted]` (scheme
  marker retained) and `DoesNotContain("secret")` (payload gone). One test
  suffices: both export paths (`CrashReporter.WriteReport` crash tail and
  `DiagnosticsRedactor.RedactLogText` diagnostics bundle) funnel through the SAME
  `ScrubSecrets`/`_proxyUriPattern`. Greedy `\S+` payload consumption is already
  pinned for five schemes by `ScrubSecrets_RedactsAllProxyProtocols`, and the
  `RedactLogText → ScrubSecrets` / crash-tail routing by `DiagnosticsRedactorTests`
  / `WriteReport_TailStillScrubbed`, so wgturn is covered end to end transitively.

### Verification approach

Static source-shape assertions (R13-A) + pure-string scrub assertions (R13-B)
only. No live subscription fetch, no wgturn-cli launch, no installer, no
filesystem mutation beyond xUnit temp state, no new production seam. Execution in
remote GitHub CI.

## 6. Affected callers / consumers + invariants

- **R13-A consumers**: `MainWindowViewModel.Subscriptions.cs`
  (`RefreshSubscriptionAsync`, `RefreshAllSubscriptionsAsync`, auto-refresh timer),
  `MainWindowViewModel.Wgturn.cs` (`AddWgturnConfig`). Invariant: log level,
  message template, and exception attachment unchanged — ONLY the `{Url}` argument
  value is redacted to `scheme://host`. No behavior change to refresh/add logic.
- **R13-B consumers**: `CrashReporter.ScrubSecrets` (public — also used by an
  Android uncaught-handler bridge per its doc), `CrashReporter.WriteReport` crash
  tail, `DiagnosticsRedactor.RedactLogText` (diagnostics bundle). Invariant: every
  previously-scrubbed scheme keeps its exact behavior; `wgturn` is additive only.
  `wgturn://[redacted]` keeps the scheme (diagnostic: "this is a wgturn URI") and
  drops the payload, matching the existing proxy-URI treatment.
- `CanaryPolicy.RedactUrl` is reused UNCHANGED (already pinned).
- `DiagnosticsRedactor.RedactUrlKeepHost` / `UrlKeys` are UNCHANGED (structured
  config export already handles `wgturn_url`).

## 7. Exact expected file list

Product:
- `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs` (R13-A: 3 sinks @ :141, :179, :320)
- `VPNRouter.App/ViewModels/MainWindowViewModel.Wgturn.cs` (R13-A: 1 sink @ :488)
- `VPNRouter.Core/Services/CrashReporter.cs` (R13-B: `_proxyUriPattern` @ :171)

Tests:
- `VPNRouter.Tests/AppUrlRedactionSourceTests.cs` (new — R13-A source-shape contract, 1 `[Theory]` × 4 sinks)
- `VPNRouter.Tests/CrashReporterScrubberTests.cs` (extended — R13-B scrub path, +1 `[Fact]` `ScrubSecrets_RedactsWgturnUri`)

## 8. Non-goals

- Do NOT modify `CanaryPolicy.RedactUrl` (already correct + pinned).
- Do NOT modify `DiagnosticsRedactor.RedactUrlKeepHost` / `UrlKeys` (structured
  config export already handles `wgturn_url`; the gap is log-text only).
- Do NOT touch the raw `ex.Message` UI surfaces (R13-P3 — separate follow-up).
- Do NOT redact the Core `{Url}` sinks at `GeoDataDownloader.cs:142`,
  `TgProxyUpdater.cs:168`, `ZapretUpdater.cs:245` — those log HARDCODED PUBLIC
  download URLs (no user token); out of scope. (`VlessDeepVerifier.cs:544` is
  already redacted.)
- Do NOT touch `MainWindowViewModel.cs:6727` (`OpenUrl` Debug-level log) — it logs
  user-clicked https links whose path/query the scrubber's `_httpUrlPattern`
  already redacts downstream; Debug level; not a confirmed R13 sink. Noted only.
- Do NOT add a new redaction helper / abstraction / interface.
- Do NOT change wgturn-cli flag semantics, ACLs, or run any installer/binary.

## 9. Security / concurrency / data-loss / platform review

- **Security**: R13-A removes verbatim subscription tokens / wgturn credentials
  from the on-disk log (local log reader / shared-box / handed-to-support threat
  model — same class as P09/SEC-1). R13-B closes the wgturn leak in the
  support-shareable diagnostics bundle and crash report, including historical logs
  already on disk. `RedactUrl` keeps the host (diagnostic) and drops the
  path/query (where tokens live); the wgturn scrub keeps the scheme and drops the
  whole payload (wireguard key material). New code introduces no new log line that
  carries a raw secret.
- **Concurrency**: none new (logging-argument substitution + a compiled regex edit).
- **Data-loss**: none. Diagnostics/crash content is reduced (redacted), never
  dropped; report file paths/return values unchanged.
- **Platform**: `CanaryPolicy.RedactUrl` and the regex are pure managed code,
  identical on Windows/macOS/Linux/Android. The wgturn scrub also protects the
  Android uncaught-handler bridge that reuses `ScrubSecrets`.

## 10. Dependencies / overlaps

- **Base is R06** (`35bf7ade`, PR #68 — branch
  `codex/qwen-audit-r06-security-diagnostics-2026-07-29`); **transitive dependency
  is P09** (PR #60, commit `d857fa6e`, branch
  `codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29`), which introduced the
  `CanaryPolicy.RedactUrl` reuse pattern + `DiagnosticsRedactor`. R13 is stacked
  directly on R06 (PR #68) and composes transitively on P09 (PR #60). Do NOT
  rebase onto `origin/main` while P09/R06 are unmerged.
- R13-A touches App ViewModel partials that no other audit package edits.
- R13-B touches `CrashReporter._proxyUriPattern`; R06 already edited
  `CrashReporter.WriteReport` (OBS-2 bounded tail) — different region (the regex
  field block vs the tail loop), already composed in the base. No conflict.
- No overlap with R07/ZAP-2 (emergency-channel process disposal).

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new R13-A + R13-B tests pass; P09
      `SubscriptionUrlRedactionTests` + `CanaryPolicyTests` + `DiagnosticsRedactorTests`
      stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged (no architecture change).
- [ ] Gate 4 — Self-review: static secret-scan of the four edited sites + the regex
      (confirm no raw URL/`wgturn://` survives); security review of the diff.
- [ ] Gate 5 — MCP verify: N/A (logging + scrubber only, no UI surface change).
- [ ] Gate 6 — Characterization diff: N/A (no god-file split; public surface unchanged).

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING (expected +5 cases: AppUrlRedactionSourceTests 1 `[Theory]` × 4 sinks + CrashReporterScrubberTests +1 `[Fact]`)
**Files changed**:
- `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs` (R13-A, 3 sinks)
- `VPNRouter.App/ViewModels/MainWindowViewModel.Wgturn.cs` (R13-A, 1 sink)
- `VPNRouter.Core/Services/CrashReporter.cs` (R13-B, `_proxyUriPattern`)
- `VPNRouter.Tests/AppUrlRedactionSourceTests.cs` (new, +1 `[Theory]`/4 cases)
- `VPNRouter.Tests/CrashReporterScrubberTests.cs` (extended, +1 `[Fact]`)

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [-] Gate 5: N/A — logging + scrubber only
- [-] Gate 6: N/A

**Surprises encountered**: PENDING
**Follow-ups spawned**: R13-P3 (raw `ex.Message` UI surfaces —
`Subscriptions.cs:231`, `SimpleMode.cs:492-493`)

## 13. Rollback

`git revert <commit>` on the R13 branch, or delete
`codex/qwen-audit-r13-app-url-redaction-2026-07-29`. R13 is additive over R06/P09:
reverting restores the raw `{Url}` log arguments and removes `wgturn` from the
scrubber alternation; P09 Core redaction, R06 wgturn-args + bounded crash tail,
and the structured-config redactor all remain intact. No state written.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r13-app-url-redaction-2026-07-29.md через
Qwen Code. IDs: R13-A (P2, App-layer raw URL log sinks), R13-B (P2, wgturn
diagnostics scrub gap). R13-P3 (raw ex.Message UI) — НЕ в scope, follow-up.
Base commit 35bf7ade = R06 (PR #68, branch
codex/qwen-audit-r06-security-diagnostics-2026-07-29); транзитивная зависимость
P09 = PR #60 (commit d857fa6e, branch
codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29). Сначала прочитай brief
целиком, AGENTS.md, plans/CLAUDE.md, VPNRouter.Core/CLAUDE.md,
VPNRouter.App/CLAUDE.md, VPNRouter.Tests/CLAUDE.md. R13-A: оберни четыре
{Url}-аргумента логов в существующий public CanaryPolicy.RedactUrl(...) —
MainWindowViewModel.Subscriptions.cs:141,179,320 и
MainWindowViewModel.Wgturn.cs:488. Никакого нового helper/abstraction.
R13-B: добавь схему wgturn в CrashReporter._proxyUriPattern
(CrashReporter.cs:171), чтобы raw wgturn:// URI скрывался в crash tail и
diagnostics bundle (включая исторические логи); НЕ трогай
DiagnosticsRedactor.RedactUrlKeepHost/UrlKeys (structured config уже обрабатывает
wgturn_url). Тесты: один focused на source log redaction path (capturing Serilog
sink, токен отсутствует / redacted host присутствует) и один-два pure на wgturn
scrub path (ScrubSecrets + RedactLogText → wgturn://[redacted], payload absent).
НЕ запускай локальные build/test/restore/app/binary/installer, не делай
VM/WinRM/ADB/MCP/live-проверок/загрузок. Только чтение/поиск/редактирование и
запись тестов. Commit/push/CI делает orchestrator. Без release/merge/tag/deploy.
Без emoji. Заполни Outcome шаблоном PENDING.
```
