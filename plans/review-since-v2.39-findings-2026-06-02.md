# Adversarial bug/regression review — all code since v2.39 (2026-06-02)

Multi-agent workflow: 7 reviewers (one per changed surface) → adversarial
verify of every finding (skeptic told to refute) → synthesis. **23 raised,
15 confirmed real, 8 refuted.** 30 agents, ~3.4M tokens.

Routing legend: **[main]** = v2.39 cycle code, IN the soaking r7 binary →
fixing means a `v2.39.0-r8` (restart soak). **[branch]** = on the
`claude/v2.40-fc-interaction-gates` branch → folds into v2.40.0-r1.
**[pre-existing]** = predates v2.39.

## Resolution — v2.40.0-r1 (2026-06-02)

User command "Исправляй все": **all 15 confirmed findings fixed** (the v2.40
branch merged into main, so the [main]/[branch] split dissolved — everything
ships in one combined `v2.40.0-r1`). Status:

| # | Sev | Status | Where fixed |
|---|---|---|---|
| H1 | HIGH | ✓ FIXED | `CustomConfigInjector` fail-CLOSED dns.final + `EnsureSynthesizedRemoteDns` + `FindRemoteDnsTag` excludes `dns-direct`; +Theory (sing-box check verifies synth) |
| M1 | MED | ✓ FIXED | `DiagnosticsRedactor` SecretKeys += `obfs_password`/`obfs-password`/`plugin_opts`; +test |
| M2 | MED | ✓ FIXED | `_urlKeepHost` drops `userinfo@`; +test |
| M3 | MED | ✓ FIXED | `_logKeyValueSecret` += authorization/proxy-authorization keywords + scheme-word skip; +test |
| M4 | MED | ✓ FIXED | `ct.ThrowIfCancellationRequested()` between VerifyOne and MergeRecheckResult (both recheck paths) |
| M5 | MED | ✓ FIXED | `ScrubRoutingForApp` scrubs BOTH Include+Exclude lists (name + bare) |
| L1 | LOW | ✓ FIXED | `FreeConfigModels` doc corrected to "set on success only, never cleared" |
| L2 | LOW | ✓ FIXED | Android dedup by checking `foundHosts.Contains` + add only in Verified branch |
| L3 | LOW | ✓ FIXED | `OnExcludeRuChanged` → ApplyFiltersAndStats |
| L4 | LOW | ✓ FIXED | max-ping clamp [50,2000] at display site too |
| L5 | LOW | ✓ FIXED | gate tests broadened to 8 statuses + busy-guard test |
| L6 | LOW | ✓ FIXED | audit-#7 comment scoped to Search tab |
| N1 | NIT | ✓ FIXED | Android status text = `pool.Count` (real loop bound) |
| N2 | NIT | DEFERRED | sub-ms redundant SaveSettings writes — documented perf nit, backlog |
| N3 | NIT | ✓ FIXED | forward-facing v2.40 brief/FC-contract wording corrected (in-method `if (IsBusy) return` is the real Apply block, not the button binding) |

All 8 adversarially-refuted items remain non-bugs (no action). Verification:
desktop build 0 errors, affected suites 105/105 green (incl. the H1 sing-box
check on the synthesized DNS server), Android build 0 errors.

## HIGH (1)

### H1 [main] Residual DNS leak in custom-JSON full-tunnel / exclude mode
`CustomConfigInjector.cs:212-225`. The 8c83203 `dns.final` fix is fail-OPEN:
it overrides `dns.final` only when `FindRemoteDnsTag` returns a server with a
proxy detour. For a custom config whose DNS servers have NO detour (common),
`StripUnsupportedFeatures` rewrites them to `detour:"dns-direct"`, and the
override then points `dns.final` at that **real-NIC** resolver. Net: in
full/exclude mode `route.final=proxy` tunnels traffic but DNS resolves direct
→ **DNS leak** — the exact class #147 set out to close. `ConfigGenerator`
always synthesizes a `vpn-dns` (detour=proxy) server; the injector doesn't.
LeakProtection doesn't catch it (custom mode bypasses `ValidateConfig`). All
test fixtures include a `detour:"proxy"` DNS server, masking the gap.
**Fix:** when `wantRemoteDns` and no genuinely-remote DNS tag exists,
synthesize a proxy-detour DoH server (mirror `BuildDns`) and point `dns.final`
at it; make `FindRemoteDnsTag` exclude `dns-direct`. Add a no-detour fixture.

## MEDIUM (5)

### M1 [main] `obfs_password` (Hysteria2 Salamander) numeric leak — diagnostics
`DiagnosticsRedactor.cs`. The numeric-secret `SecretKeys` set missed the flat
YAML alias `obfs_password`. An all-digit Salamander passphrase survives verbatim
into the exported `config.redacted.yaml`. **Fix:** add `obfs_password`
(+ `plugin_opts`) to `SecretKeys`; regression test with a numeric `obfs_password`.

### M2 [main] URL userinfo leak — diagnostics
`DiagnosticsRedactor.cs:114-115,282-286`. `RedactUrlKeepHost` keeps
`user:pass@host` (the regex authority class doesn't exclude `@`). A subscription
URL with embedded basic-auth leaks the credentials. **Fix:** strip a
`userinfo@` prefix (or re-emit via `System.Uri` as `scheme://host[:port]`).

### M3 [main] `Authorization: Bearer/Basic <token>` log leak — diagnostics
`DiagnosticsRedactor.cs:127-129`. The key=value scrubber redacts only the
scheme word (`Bearer`), leaving the token; `Authorization:` itself fails the
`\bauth\b` boundary entirely. Short tokens also dodge the ≥40-char base64
scrubber. Defense-in-depth gap (no confirmed active log site today). **Fix:**
header-aware redaction (redact rest-of-value after Bearer/Basic/Token/Digest).

### M4 [main] #146 cancel-during-deep-verify → false "failed last check"
`FreeConfigDeepVerifier.cs:234-239` + `FreeConfigFreshness.MergeRecheckResult`.
`VerifyOneAsync` swallows `OperationCanceledException` (no `when` filter, no
rethrow), so a user-cancel during the multi-second deep-verify returns with no
fresh `LastDeepVerifyAt` stamp → `MergeRecheckResult` takes the FAILURE branch →
spurious "failed last check" marker, persisted. (Pre-#146 this was a false
*success*; the fix flipped it to a false *failure*.) **Fix:** caller calls
`ct.ThrowIfCancellationRequested()` between verify and merge (covers both the
HTTP-probe and bind cancel windows); regression test.

### M5 [main] r6 scrub leaves stale entry in the INACTIVE Exclude list
`MainWindowViewModel.Profiles.cs` (ScrubRoutingForApp) + `RoutingAppListEditor.cs:112`.
`TryRemoveProcessName` only scrubs `RoutingAppsInclude`; the active-list uncheck
no-ops on the inactive list. Sequence: Exclude-mode add X → flip to Include →
remove X's row (scrub misses Exclude) → flip back to Exclude → **X reappears and
bypasses the VPN** (leak-from-intent). `SaveSettings` never rebuilds the routing
lists, so it persists. **Fix:** scrub the process from BOTH lists directly
(OrdinalIgnoreCase) on row removal.

## LOW (6)

### L1 [main] Stale `LastDeepVerifyAt` doc comment
`FreeConfigModels.cs:101-102` says "cleared on subsequent non-Verified re-test" —
false; #146 now depends on it being monotonic/never-cleared. Doc-only footgun.
**Fix:** correct the comment to the real "set on success only, never cleared".

### L2 [pre-existing] Android per-host dedup burns a host before deep-verify
`AndroidFreeConfigsOrchestrator.cs:240-246`. `foundHosts.Add(c.Host)` claims a
host at surface time; if the first (lower-latency) candidate fails deep verify,
a working alt-config on the same host is never tried. Pre-dates v2.39. **Fix:**
dedup by Id, or move `foundHosts.Add` into the Verified branch.

### L3 [main] ExcludeRu has no `OnExcludeRuChanged` hook
`FreeConfigsPageViewModel.cs`. Toggling ExcludeRu ON doesn't re-filter already-
displayed RU rows (visible/selectable until next search/country-change).
Self-inflicted, transient. **Fix:** add `partial void OnExcludeRuChanged(...) => ApplyFiltersAndStats();`.

### L4 [branch] v2.40 max-ping clamp on search gate but not display filter
`FreeConfigsPageViewModel.cs:386-388 vs ~1862`. F3 clamps the search gate to
[50,2000] but `ApplyFiltersAndStats` reads the raw value. Only the lower band
[20,49] is UI-reachable (Maximum=1000) → wasted verify work + a "found N but
list shows fewer" mismatch. **Fix:** clamp at the display site too (or share one
resolved property).

### L5 [branch] v2.40 tests under-cover the plan
`FreeConfigsApplyGateTests.cs`. Only the Verified-gate is tested; F1
(blocked-during-search), F3 (clamps), and the MVM backstop have no coverage; the
[Theory] omits 4 non-Verified statuses. **Fix:** add the missing tests.

### L6 [branch] ExcludeRu filter on Search but not Saved list (comment overclaims)
`FreeConfigsPageViewModel.cs`. The audit-#7 comment says RU is "never a Connect
candidate" when ExcludeRu is on, but the Saved tab is unfiltered and a saved RU
row is connectable. By-design (Saved is user-curated) → comment overclaim only.
**Fix:** scope the comment to the Search tab.

## NIT (3)

- **N1 [main]** `AndroidFreeConfigsOrchestrator.cs:197-208` — status text "Testing
  first N (≤800)" + "hard cap at queue.Count" comment misstate the real loop
  bound (full queue until target/exhausted). Cosmetic.
- **N2 [main]** `MainWindowViewModel.Profiles.cs` — category/multi-app removal
  does ~2N+1 redundant `SaveSettings` writes (sub-ms each; already comment-ack'd).
- **N3 [branch]** v2.40 commit narrative claims desktop blocks Apply "via button
  binding"; the button binds `HasSelection`, not `!IsBusy` — the in-method
  `if (IsBusy) return` is what actually blocks. Narrative-only.

## Refuted by adversarial verify (8 — NOT bugs)
- default_domain_resolver fallback picking a proxy-detour server (bootstrap) — NIT, not reachable as harmful.
- Long-base64 scrubber over-trim at `+//` — not exploitable.
- Search 6h skipDeep window reporting a dead server fresh — orthogonal/not reachable as claimed.
- Android deep-verifier not downgrading Status — not the #146 twin (Android keeps Ok on fail).
- Deep-verify bridge-unavailable full-pool sweep — has distinguishing status.
- v2.40 Verified-gate "normally unreachable" comment vs Saved tab — Saved is Verified-only by retention.
- Redactor allowlisting `address` (DoH/DoT URL credential) — `address` is host-only here.
- Compressed-size guard needing Content-Length — bounded incremental check holds without it.

## Recommendation
The **[main] findings (H1, M1–M5) are in the soaking r7 binary** → the residual
DNS leak (H1) and the privacy leaks (M1–M3) argue for a **`v2.39.0-r8`** that
fixes H1 + M1–M5 before any stable cut (restart the soak on fixed code). The
**[branch] LOWs/NITs (L4–L6, N3) fold into v2.40.0-r1**. The [pre-existing]/doc
items (L1, L2, L3, N1, N2) are backlog.
