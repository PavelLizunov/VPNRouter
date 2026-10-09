# Codebase Health and Audit Scorecard

**Date:** 2026-09-16
**HEAD:** `b101a7e232376b32b855b9e7c4f53eaeaeaf7ad8` (`main`, AppVersion `2.50.0-r9`)
**Recent-change range:** `0bcc8166..HEAD` (2.50 product work after PR #233 merge)
**Skills used:** bug-hunt, change-verification, codebase-audit, security-review, ponytail-review, ponytail-audit, ponytail-debt, audit-overflow-fix, anti-slop (UI overflow / hardcoded White), gemini-swarm (10), opus-swarm (5), grok-swarm (5)

## Executive Summary

- Total Domains Audited: 10 Gemini slices + 5 Opus reasoning reviews + 5 Grok adversarial reviews, then lead source-verified
- High-Priority Findings (lead-verified, new): 6 P1 + several P2
- Large Files Identified: 11 inspection candidates; splits justified only where mixed responsibilities are real
- Potential Dead Code Items: FreeConfigsPageViewModel leftover commands after OpenDesign overhaul
- Mechanical tests: **not run** (`dotnet` SDK absent on this host)

## 1. Domain Health Matrix

| Domain / Slice | Docs / README | Justified Decomposition Candidates | Dead Code Cues | Performance Cues | Quality Rating |
|---|---|---|---|---|---|
| FreeConfigs 2.50 | Partial (AGENTS maps) | VM still hosts unused commands | Retest/ClearFailed/DeepVerifyTop unused | UI-thread GC.Collect | C (false-green + Android VLESS-only) |
| Applications page | Tokens.axaml | Page + Profiles partial | Hardcoded BYPASS | Async scan re-entrancy | C+ (overflow anti-patterns) |
| Android 2.50 | Zone AGENTS | AndroidApp.axaml.cs 2389 LOC | Characterization tests deleted | Safe-area path present | C (apply/verify still VlessUriParser) |
| Share-link / AWG3 | Comments in parser | ServerUriParser 990 LOC vs VlessUriParser dup | Dead awg3 protocol arms in verifier | Span parse is the point | B |
| Zapret / shell | Tests exist | ZapretActions mixed UI helpers | cmd.exe leftover | N/A | B- |
| Tests after prune | test-audit plans | N/A | Unique pins dropped | N/A | C (coverage holes) |
| CLI / Service / GUI | Zone AGENTS | StopCommand already hardened | N/A | N/A | B |
| Packaging / CI | workflows AGENTS | N/A | Stale OPEN-DEFECTS line cites | N/A | B (several P1s already repaired in source) |
| Lifecycle / DNS / firewall | Platform + Services AGENTS | VpnEngine 1890, CustomConfigInjector 2088 | NIGHT ledger stale vs source | Exited-subscribe window | B |
| God files | v3.0 roadmap | MainWindowViewModel 7429, NetworkPage.axaml 2493 | Localization tables large by nature | ConnStats poll | C for VM, B for cohesive generators |

## 2. Targeted Cleanups

- [ ] `FreeConfigsPageViewModel.cs:1354+`: Remove or rebind unused Retest/ClearFailed/KeepVerifiedOnly/ClearAll/OpenLogs/AddUserSource/DeepVerifyTop commands if UI no longer exposes them
- [ ] `ApplicationsPage.axaml:152,215,311,314,404`: wrap Button Content in TextBlock TextWrapping=Wrap
- [ ] `ApplicationsPage.axaml:222,401`: replace `Foreground="White"` with `{DynamicResource AccentOnSolidBrush}`
- [ ] `plans/OPEN-DEFECTS.md` NIGHT-01..08 and release-pipeline cites: reclassify after owner acceptance (source already repaired for several)

## 3. File Decomposition Cues

| File Path | LOC | Proposed Decomposition Boundary |
|---|---|---|
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs` | 7429 | Already partials; remaining god is constructor + Zapret/TgProxy/OpenUrl. Extract shell helpers (OpenUrl, OpenFolder, KillAllZapret) to App/Services. Characterization hash must be re-pinned. |
| `VPNRouter.App/Views/Pages/NetworkPage.axaml` | 2493 | Cohesive settings page; split only by visual sections if overflow work continues, not for LOC. |
| `VPNRouter.Android/AndroidApp.axaml.cs` | 2389 | Already partials; keep extracting feature surfaces. Restore characterization hash test first. |
| `VPNRouter.Core/Services/CustomConfigInjector.cs` | 2088 | DNS stamp / leak / clash_api already mixed; extract IsLocalDetour + DNS rewrite only with tests. |
| `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs` | 1982 | Delete dead commands before splitting. |
| `VPNRouter.Core/Services/VpnEngine.cs` | 1890 | Lifecycle already documented; do not split without v3.0 Micro-Spec. |
| `VPNRouter.Core/Localization/Strings.cs` | 1991 | Data table; do not split for size. |

## 4. Performance and Concurrency Cues

- `FreeConfigsPageViewModel` ReclaimPostSearchMemory: blocking Gen2 GC on UI thread (Gemini finding; not independently timed)
- `ApplicationsPage.axaml.cs` OnSelectRunningProcessClicked: no generation/cancel; late `ShowAt` after dismiss
- `SingBoxManager.Lifecycle.cs:961-993`: Exited subscribed after `_runner.Start`; ultra-fast FATAL can miss crash event (Opus RACE-01)
- ConnStats visibility throttle pin deleted (`ConnStatsVisibilityThrottleTests`)
- HealthMonitor timer-swap pin deleted (`HealthMonitorTimerRaceTests`)

## 5. Code Quality and Modernization

- Inconsistent URI parsers: `ServerUriParser` (desktop deep-verify / ToVlessServerEntry) vs `VlessUriParser` (Android apply, Android deep-verify, FreeConfigAggregator fallback fetch)
- TwoPhaseStartCoordinator line 267 fallthrough returns Connected; currently dead if connectedTcs only TrySetResult
- SafeMode sets local `isFullTunnel=true` but `BuildRoute` still reads `settings.App.RoutingMode` -> split `final=direct` (NIGHT-FOLLOWUP-02 CONFIRMED)

---

## Recent 2.50 change review (change-verification)

**Evaluated Scope:** `0bcc8166..b101a7e2`, AppVersion `2.50.0-r9`, branch `main`.
**Executed Commands:** git log/diff/stat, grep/read of claimed file:line. `dotnet test` **not run** (no SDK).
**Verdict:** `changes_required` for any stable cut; `verified` only as a source-review of current HEAD.

### What landed well
- HTTP(S) OpenUrl gate (#283), explorer ArgumentList (#285), taskkill ArgumentList (#289)
- Zapret RunTests ArgumentList + metachar denylist
- DiagnosticsRedactor separatorless literals (clientsecret/accesstoken/...)
- Desktop FreeConfigDeepVerifier uses ServerUriParser + multi-protocol outbound dispatch
- NIGHT-01..08 product repairs are in current source (KillAll no-op, StrictDns override, marker retain, committed firewall, failover reset, typed readiness, Apply bool restart)
- `sign-android.yml` retired; `verify-release-integrity.yml` no longer executes AppImage; `build.ps1` no longer `--latest`

### Issues Found (lead-verified)

Critical:
- `MainWindowViewModel.FreeConfigs.cs:229-239` paints Connected for StartTaskCompleted/Cancelled after swallowing startTask exceptions (leak-adjacent false-green)

Important:
- Android apply + Android deep-verify + aggregator fallback still `VlessUriParser.Parse` after 2.50 multi-protocol
- `FreeConfigAggregator.MergeWithCache` drops `LastDeepVerifyAt`
- `SubscriptionFetcher` logs raw exception URI
- UpdateChecker size-only SHA fallback
- OpenServiceMenu cmd.exe Arguments; Flowseal probe Arguments
- Test prune dropped unique pins (ConnStats throttle, HealthMonitor timer race, Android characterization, 4 typed-readiness source guards)
- NIGHT-FOLLOWUP-01/02 still present

Minor:
- ApplicationsPage bare Button Content + Foreground=White
- FreeConfigsPage apply button bare Content
- Crash report skips key=value redactor
- Separatorless regex still misses idtoken/appsecret

---

## Ponytail-review (2.50 product diffs)

- `FreeConfigsPageViewModel`: delete unused command surface (Retest/ClearFailed/KeepVerifiedOnly/ClearAll/user-source/DeepVerifyTop). Replacement: nothing until UI needs them.
- `FreeConfigAggregator` vs PoolAggregator ID hash: two helpers. Replacement: one shared id builder.
- `ServerUriParser` duplicates PlaceholderDefense/TryParse/base64url from VlessUriParser. Replacement: call through.
- `TgProxyManager.KillAll`/`KillByPort` empty methods kept for binary compat. yagni unless a plugin actually binds them.

`net: ~-400 lines possible` in FreeConfigs VM dead commands (Gemini estimate; not applied).

No complexity finding on the ArgumentList security patches; those are required.

## Ponytail-audit (repo-wide, ranked)

1. `delete:` unused FreeConfigs VM commands. [VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs]
2. `yagni:` MainWindowViewModel still owns OpenUrl/OpenFolder/KillAllZapret. App/Services already has FileManagerHelper. [MainWindowViewModel.cs]
3. `yagni:` CustomDirectRule model retained after parser deletion (OPEN-DEFECTS F3). [Models]
4. `native:` Zapret OpenServiceMenu cmd.exe wrapper. Start the bat with ArgumentList. [ZapretActions.cs:590]
5. `shrink:` Two URI parsers for the same share-link family. [ServerUriParser / VlessUriParser]

`net: -400 lines, -0 deps possible` (conservative; localization/data tables not counted).

## Ponytail-debt ledger

`build.ps1:707`, always-include .NET runtime in update zip. ceiling: update-download bandwidth. upgrade: gate behind version check if bandwidth becomes the constraint.

`VPNRouter.Core/Services/VpnEngine.cs:519`, TrueSplitDriver hardcoded `"auto"`. ceiling: no off-switch. upgrade: AppConfig.TrueSplitDriver when support needs disable-without-leaving-exclude.

`VPNRouter.Core/Services/VpnEngine.cs:558`, TUN v4-only, driver zeroes v6. ceiling: no Ipv6Address on TunSettings. upgrade: wire v6 if a v6 TUN setting lands.

`VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:423`, reload pf.conf+carrier drops other tools' runtime lines. ceiling: first engage only. upgrade: live-ruleset merge if a coexistence report needs it.

`VPNRouter.Tests/RuntimeStatusAdoptionTests.cs:422`, naive brace count. ceiling: braces inside string/interpolation. upgrade: real parser if the pin starts false-failing.

`5 markers, 0 with no trigger.`

## Overflow audit (audit-overflow-fix)

Bare Button Content still present on ApplicationsPage (SelectAll/ClearAll/ImportSteam/banner/retry), FreeConfigsPage apply, NetworkPage, ServersPage, SubscribePage, TelegramPage, DpiBypassPage, SimplePage, MainWindow conflict buttons. 2.50 Applications modernization did **not** wrap those labels. CheckBoxes on Applications list are content-free + adjacent TextBlock (compliant). `Foreground="White"` on Applications retry/apply violates tokens.

## Anti-slop (UI, AFTER mode, no edits)

HIGH: none of R-17 fake stats. MEDIUM: hardcoded `BYPASS` English badge on ApplicationsPage.axaml:360; `Foreground="White"` instead of tokens. LOW: inline Ru/En toast ternaries in MainWindowViewModel.Profiles.

### Delivery Gate Verification (UI)
- [ ] Hard Gate: NOT VERIFIED (no headless screenshots this session)
- [ ] Purpose Gate: N/A (audit, no new visuals shipped here)
- [ ] Liveliness: N/A
- [ ] Interactive Verification: NOT VERIFIED (no WINBRAT)

---

## NIGHT P1 re-verification (current source, not ledger line numbers)

| ID | Status at HEAD | Evidence |
|---|---|---|
| NIGHT-01 | FIXED in source (ledger still open pending #240 acceptance) | KillAll/KillByPort no-ops `TgProxyManager.cs:597-610`; autostart IsAnyRunning is fail-closed skip |
| NIGHT-02 | FIXED in source | `CustomConfigInjector.IsLocalDetour` checks endpoints |
| NIGHT-03 | FIXED in source | `ConfigGenerator.Dns.cs:154,179` strictDns -> vpn-dns |
| NIGHT-04 | FIXED in source | marker deleted only after confirmed cleanup |
| NIGHT-05 | FIXED in source | `ICommittedFirewallConfig.UpdateCommittedConfig` after start/apply |
| NIGHT-06 | FIXED in source | `VpnEngine.ResetFailoverContext` |
| NIGHT-07 | FIXED in source | TwoPhaseStart + OnEngineStatus guard; **FreeConfigs apply path still bypasses this** |
| NIGHT-08 | FIXED in source | ReloadConfigJsonWithResult bool + RestoreActiveBaseline |
| NIGHT-FOLLOWUP-01 | CONFIRMED | `SingBoxManager.Lifecycle.cs:87-91,674-678` ReleaseTunOwnership in catch after LaunchProcess |
| NIGHT-FOLLOWUP-02 | CONFIRMED | SafeMode `isFullTunnel=true` local only; `BuildRoute` uses `settings.App.RoutingMode` |

Release-pipeline ledger P1s that are **stale vs current files:**
- `sign-android.yml` now 25-line refuse
- `build.ps1:851-881` is Android keystore help, not `--latest`
- `verify-release-integrity.yml` documents never-execute AppImage

Still-open product residual (not NIGHT-04/05):
- Unix CLI/Service never call Linux/Mac `TryCleanupOrphanedRulesSafe`
- Windows full-tunnel `block_on_vpn_fail` creates zero netsh rules (documented per-process design)

## Untested Limits

- No `dotnet test` / Release build on this host
- No WINBRAT, no Android device, no live update extract
- Gemini worker index 2 (Android full narrative) returned incomplete in the swarm dump; Android claims were re-checked by lead via read/grep
- Worker findings that failed lead verification were dropped (NIGHT-01 still-open-as-bug, process_name ToLower, AppImage execute, build.ps1 --latest)
