# Phase — Fix Overnight Audit Findings (Gemini)

**Owner**: Pavel Lizunov per canonical contract (`docs/agent-contract.md`)
**Author**: Gemini (implementation author of code, tests, and documentation; lead is reviewer and coordinator)
**Branch**: `dsh/fix-night-audit-gemini-2026-09-04` (based on accepted `main` `b7ce0e4f`, not tracking `origin/main`; tracks task origin branch)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` · `plans/OPEN-DEFECTS.md` (Overnight audit 2026-09-04)
**Baseline**: All source line references explicitly baselined against overnight audit commit `9f8c8b5a` (`9f8c8b5a8b34f264762294f8a11842b4edab90a9`)
**Effort**: 4 sequential batches with overlapping files (`VpnEngine`, `StartupPipeline`, `MainWindowViewModel`)
**Risk / Rollback**: HIGH. Rollback dependent batches newest-first after review; delete task branch on abort.

---

## Why

The overnight deep audit on baseline `9f8c8b5a` (`plans/overnight-audit-morning-report-2026-09-04.md`) identified 12 source-verified defects (8 P1, 4 P2) caused by subsystem semantic mismatches: port treated as process ownership (NIGHT-01), custom WireGuard endpoint detours rewritten to direct DoH (NIGHT-02), StrictDns bypassed by smart-DNS rules (NIGHT-03), failed firewall cleanup deleting recovery markers (NIGHT-04), stale `current.json` caching and missing AAAA endpoints (NIGHT-05), failover caching old state across changes (NIGHT-06), start coordinator misinterpreting early task completion as tunnel readiness (NIGHT-07), forced Apply committing on failed Stop/Restart (NIGHT-08), unbounded parallel probes (NIGHT-09), unhandled cancelled UDP probe receive (NIGHT-10), ConnStats retaining stale metrics on API error (NIGHT-11), and Clash WebSocket telemetry omitting API secrets (NIGHT-12).

---

## What

Twelve confirmed defects executed across four sequential batches with overlapping files. All line references are against baseline `9f8c8b5a`.

### Batch 1 — Destructive & Privacy Boundaries (P1)
- **NIGHT-01 (P1)**: `MainWindowViewModel.cs:7015–7030`, `TgProxyManager.cs:605–650`, `RuntimeStatusDetector.cs:109–120`. Remove port-only `KillAll` and process name sweeps on Windows Quit/toggle/update. Require verified owned process handle before termination; prevent false "Running" state from foreign listeners.
- **NIGHT-02 (P1)**: `CustomConfigInjector.cs:150–182, 895–938, 1498–1535`. Unify destination classification across `outbounds` and `endpoints`. Prevent `StripUnsupportedFeatures` from treating WireGuard endpoint destinations as local and rewriting synthesized `vpnrouter-vpn-dns` detours to `dns-direct`.
- **NIGHT-03 (P1)**: `ConfigGenerator.Dns.cs:134–171`, `LeakProtection.cs:209–235`. Ensure effective `StrictDns == true` overrides `DnsMode == "smart"` on specific process include/exclude rules, routing all matched DNS queries through `vpn-dns`.

### Batch 2 — Unix Firewall Reliability & Freshness (P1)
- **NIGHT-04 (P1)**: `LinuxFirewallManager.cs:304–317`, `MacFirewallManager.cs:441–466`. Retain orphan recovery marker when `nft`/`pf` rule deletion fails; delete only upon confirmed clean deletion, preserving recovery trigger for subsequent runs.
- **NIGHT-05 (P1)**: `StartupPipeline.cs:407–477`, `LinuxFirewallManager.cs:219–273`, `MacFirewallManager.cs:346–402`. Do not invent guaranteed resolved IP generator data or a new snapshot struct. Use minimum internal committed endpoint contract using actual available schema (outbound servers and WireGuard endpoint peer addresses (not local `endpoints.address`), IPv4 and IPv6/AAAA with maintained DNS deadlines) instead of pre-reading stale or missing `current.json`. Refresh bypass on successful Apply.

### Batch 3 — Lifecycle, Readiness & Commit Truthfulness (P1)
- **NIGHT-06 (P1)**: `VpnEngine.cs:1651–1673`, `AutoFailoverEngine.cs:239–242`. Update failover state ONLY on public new intent and SUCCESSFUL Apply. Preserve lazy wiring and internal retry/tried caps; do not unconditionally instantiate on every Start/Apply; retain same snapshot pool and restart across routine calls.
- **NIGHT-07 (P1)**: `TwoPhaseStartCoordinator.cs:203–236`, `MainWindowViewModel.Connection.cs:80–100, 405–441`. Phase B ignores successful completion of `startTask`, awaiting typed `Connected` event. Faults and cancellations are observed immediately; original deadline is never reset. Do not paint UI green on legacy log strings before typed readiness.
- **NIGHT-08 (P1)**: `VpnEngine.cs:825–839`, `SingBoxManager.Lifecycle.cs:537–543`. Return explicit failure from Restart when Stop fails; abort candidate commit and metadata updates if replacement was declined.

### Batch 4 — Resource Bounds, Cancellation & Telemetry (P2)
- **NIGHT-09 (P2)**: `ServerHealthProbe.cs:51–71`. Bound concurrency in existing `ProbeAllAsync` using constant worker limit. Remove unrequested early-exit-first-server (preserves caller best-server selection semantics), config knob, and benchmark claim. Preserve caller result selection.
- **NIGHT-10 (P2)**: `TcpTlsProbe.cs:574–607`, `ServerHealthProbe.cs:59–67`. Await `receiveTask` on UDP probes; catch and propagate `OperationCanceledException` so cancelled probes never report alive or slow.
- **NIGHT-11 (P2)**: `MainWindowViewModel.ConnStats.cs:116–158, 184–195`, `ClashSingBoxApi.cs:233–276`. Distinguish API error from zero metrics; show stale indicator or clear stats on failure; bind updates to active session generation.
- **NIGHT-12 (P2)**: `VpnEngine.cs:1084–1105`, `ClashLogStream.cs:55–94`. Supply `settings.SingBox.ClashApiSecret` to `ClashLogStream` constructor to authenticate WebSocket connection without leaking secrets in logs.

### Excluded & Follow-ups
- **NIGHT-DOC (P3)**: Subsystem maps (`Platform/AGENTS.md`, `Services/AGENTS.md`) exist only in PR #235 and are absent on `main` `b7ce0e4f`. Track correction in PR #235 review feedback.
- **NIGHT-MEASURE (P2)**: Research only for owner monitor (`TunOwnershipLock.cs`, `ProcessOwnership.cs`). Profile before refactoring. No live systems, releases, or direct pushes to `origin/main`.
- **NIGHT-FOLLOWUP-01 (P1)**: Preexisting survivor identified during NIGHT-08 review. `SingBoxManager.Lifecycle.cs` `StartWithJsonCore` catch (~line 88) and `RestartCore` catch (~line 590) unconditionally call `ReleaseTunOwnership` after `LaunchProcess` can throw from `Started` callback with retained live handle (evidence: `startedHandle`/`Started` invoke near line 905). Not introduced by NIGHT-08; outside approved 12 repair scope, deferred owner follow-up not fixed.

---

## How

- **Roles**: Owner is Pavel Lizunov per canonical contract (`docs/agent-contract.md`). Gemini is implementation author (code, tests, documentation). Lead is reviewer and coordinator.
- **Execution**: Four sequential batches with overlapping files (`VpnEngine`, `StartupPipeline`, `MainWindowViewModel`). Disjoint file sets apply only to simultaneously dispatched tasks. Each batch must achieve green CI before the next proceeds.
- **Environment & CI**: Local SDK unavailable on `harness-test`; CI is execution vehicle. No promise existing CI runs full solution build until workflows are checked; gate mark evidence pending, exact commands must be inspected. Pre-commit diff checks do not replace test evidence.
- **Rollback**: Claim of independent batch reverts is removed. Rollback dependent batches newest-first after review, or abort task branch.

---

## Verification Gates

- [ ] **Gate 1 — Build clean**: **EVIDENCE PENDING** (Control plane lacks .NET SDK; exact workflow commands must be inspected before asserting full solution build; Batch 1, Batch 2 [NIGHT-04, NIGHT-05 full integration], Batch 3 [NIGHT-08, NIGHT-07, NIGHT-06 `da3c21e7`], and Batch 4 [NIGHT-09/10 `530ca9e7`] green on CI; NIGHT-12 minimal secret wiring pending CI; NIGHT-11 pending; limits full solution/live/macOS unchanged; red witnesses/full solution/native gaps remain; no live VPN/infra).
- [ ] **Gate 2 — Tests green**: **BLOCKED** on control plane (CI execution required; non-headless test suite; Batch 1, Batch 2 [NIGHT-04, NIGHT-05 full integration on `8ee89105635bc750932084c6dad09467cbe1d8b9`, run 33954240530: 3163 total / 3105 passed / 58 skipped], Batch 3 [NIGHT-08 `1ca812f4`, run 33951772995; NIGHT-07 `5ecbe4d2`, run 33955634699: 3184 total / 3126 passed / 58 skipped; NIGHT-06 `da3c21e7`, run 33956946168: 3194 total / 3136 passed / 58 skipped], and Batch 4 [NIGHT-09/10 `530ca9e7`, run 33957757306: 3221 total / 3163 passed / 58 skipped] PASS on CI; NIGHT-12 minimal secret wiring pending CI; NIGHT-11 pending; red witnesses/full solution/native gaps remain; no live VPN/infra).
- [ ] **Gate 3 — Docs**: **PASS** (Brief revised; `plans/OPEN-DEFECTS.md` active; report imported; NIGHT-FOLLOWUP-01 registered; ledger all still open pending closure evidence; no fake full gate; no closure claim for all 12).
- [ ] **Gate 4 — Independent review**: **PENDING** (Adversarial review / bug-hunt per batch; Batch 1, Batch 2 [NIGHT-04, NIGHT-05 full integration], Batch 3 [NIGHT-08, NIGHT-07, NIGHT-06], and Batch 4 [NIGHT-09/10] green on CI; NIGHT-12 minimal secret wiring pending CI; NIGHT-11 pending; red witnesses/full solution/native gaps remain; no live VPN/infra; full review gate remains open; ledger all still open pending closure evidence; no fake full gate; no closure claim for all 12).
- [ ] **Gate 5 — UI verify**: **N/A / PENDING** (No visual redesign; ViewModel status/readiness verified via headless/characterization tests on CI; NIGHT-07 UI source guard only, coordinator behavioral fake events tests; no live run/fullsolution/native mac).
- [ ] **Gate 6 — Characterization diff**: **PENDING** (Coordinator state transitions and error propagation baselined).

---

## Outcome

**Status**: BATCH 1, BATCH 2 (NIGHT-04, NIGHT-05 FULL INTEGRATION), BATCH 3 (NIGHT-08, NIGHT-07, NIGHT-06) & BATCH 4 (NIGHT-09, NIGHT-10) VERIFIED ON CI / NIGHT-12 MINIMAL SECRET WIRING PENDING CI / NIGHT-11 PENDING / DEFECT LEDGER ALL STILL OPEN PENDING CLOSURE EVIDENCE / NO CLOSURE CLAIM ALL 12 (RED WITNESSES / FULL SOLUTION / NATIVE GAPS REMAIN; NO LIVE VPN/INFRA)
**Commits**: Batch 1 on `4d029a54` (prior commits: `c7bd9c48`, `ff4bf3d4`, `389963cc`). Batch 2 (NIGHT-04) on `4ce2fc309c0bf96ff8734d3cd783639ec83d97a7`. Batch 2 (NIGHT-05 endpoint substep) on `ef82aadc4389de08f3a67ef356e2f203d866c1bb`. Batch 3 (NIGHT-08) on `1ca812f4400c9e8d881532e0baf9277513fa4109` (preceded by `143d2adf`). Batch 2 (NIGHT-05 full integration) on `8ee89105635bc750932084c6dad09467cbe1d8b9` (preceded by `b7406456`, `ecb682ef`). Batch 3 (NIGHT-07) on `5ecbe4d2ad275766d3c52f9589ad94b0be4b53b3`. Batch 3 (NIGHT-06) on `da3c21e740c7b5905158cbf6d4b568d922ff8bf7`. Batch 4 (NIGHT-09 & NIGHT-10) on `530ca9e7c8c8074267b67398d80d67f2022ff4df`. NIGHT-12 minimal secret wiring working tree pending CI.
**Branch**: `dsh/fix-night-audit-gemini-2026-09-04` (PR #240)
**Test deltas**: Batch 1 all 4 checks PASS on CI (`4d029a54`, run 33947035219). NIGHT-04 all 4 CI checks PASS (`4ce2fc309c0bf96ff8734d3cd783639ec83d97a7`, run 33948404811: 3102 total / 3045 passed / 57 skipped on Ubuntu; Unix classes explicitly ran on Ubuntu; Windows filter does not select Unix tests). NIGHT-05 endpoint substep all 4 checks PASS on CI (`ef82aadc4389de08f3a67ef356e2f203d866c1bb`, run 33949288379: Ubuntu 3114 total / 3057 passed / 57 skipped, 12 new tests). NIGHT-08 all 4 checks PASS on CI (`1ca812f4400c9e8d881532e0baf9277513fa4109`, run 33951772995: 3121 total / 3063 passed / 58 skipped; actual Apply false branch + retainedStop retry + ack hotreload executed on Ubuntu, successful Windows restart test not selected by Windows filter; receipt https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550206811). NIGHT-05 full integration all 4 checks PASS on CI (`8ee89105635bc750932084c6dad09467cbe1d8b9`, run 33954240530: 3163 total / 3105 passed / 58 skipped on Ubuntu, 42 new tests; receipt https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550488707). NIGHT-07 all 4 checks PASS on CI (`5ecbe4d2ad275766d3c52f9589ad94b0be4b53b3`, run 33955634699: Ubuntu 3184 total / 3126 passed / 58 skipped, 21 new tests; receipt https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550638820). NIGHT-06 all 4 checks PASS on CI (`da3c21e740c7b5905158cbf6d4b568d922ff8bf7`, run 33956946168: Ubuntu 3194 total / 3136 passed / 58 skipped, 10 new tests; receipt https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550780691). NIGHT-09/10 all 4 checks PASS on CI (`530ca9e7c8c8074267b67398d80d67f2022ff4df`, run 33957757306: Ubuntu 3221 total / 3163 passed / 58 skipped, 27 new tests; receipt https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550872851). Actual loopback UDP reply/afterdatagramcancel/silent2s proved; bounded8 and tailbest. NIGHT-12 minimal secret wiring pending CI; NIGHT-11 pending.
**Files changed**:
- `plans/OPEN-DEFECTS.md` (active defect ledger; all still open pending closure evidence for whole task; no fake full gate; NIGHT-FOLLOWUP-01 registered; no closure claim for all 12)
- `plans/overnight-audit-morning-report-2026-09-04.md` (audit report)
- `plans/phase-fix-night-audit-gemini-2026-09-04.md` (brief updated with NIGHT-06 CI pass, NIGHT-09/10 CI pass, NIGHT-12 minimal secret wiring pending CI, and NIGHT-11 status)
- `VPNRouter.Core/Interfaces/IFirewallManager.cs` (internal optional `ICommittedFirewallConfig` capability interface; committed `b7406456`, `8ee89105`)
- `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint parsing committed `ef82aadc`; NIGHT-05 committed-config atomic refresh, `_gate` synchronization, and disarm before DNS committed `b7406456`, `8ee89105`)
- `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint parsing committed `ef82aadc`; NIGHT-05 committed-config atomic refresh, `_gate` synchronization, and disarm before DNS committed `b7406456`, `ecb682ef`, `8ee89105`)
- `VPNRouter.Core/Services/StartupPipeline.cs` (NIGHT-05 Phase 6 skips legacy capability; Phase 8 passes committed config after confirmed start before monitors; committed `b7406456`, `8ee89105`)
- `VPNRouter.Core/Services/VpnEngine.cs` (NIGHT-08 `ApplyAsync` baseline restoration on reload/restart failure committed `143d2adf`; NIGHT-05 firewall committed config update on successful bool ack committed `b7406456`, `8ee89105`; NIGHT-06 failover settings context and generation reset on public Start and committed Apply, stale generation/identity guards before teardown committed `da3c21e7`; NIGHT-12 ClashLogStream constructor passes ClashApiSecret)
- `VPNRouter.Core/Services/SingBoxManager.cs` (NIGHT-08 `ReloadConfigJsonWithResult` and `TryReloadConfigJson` lease guards committed `143d2adf`)
- `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs` (NIGHT-08 `RestartCore` bool outcome and exact-stop failure handling committed `143d2adf`)
- `VPNRouter.Core/Services/ServerHealthProbe.cs` (NIGHT-09: bounded concurrency with worker limit 8, caller best-server tail selection preserved, early-exit removed; committed `530ca9e7`)
- `VPNRouter.Core/Services/TcpTlsProbe.cs` (NIGHT-10: UDP probe receiveTask await, OperationCanceledException propagation, cancelled probes never reporting alive or slow; committed `530ca9e7`)
- `VPNRouter.Core/Services/ClashLogStream.cs` (NIGHT-12: minimal secret wiring, WebSocket Bearer secret auth)
- `VPNRouter.App/ViewModels/Internals/TwoPhaseStartCoordinator.cs` (NIGHT-07: Phase B awaits typed `Connected`, clean startTask completion never readiness, same A/B timer retained, late typedConnected succeeds, faults and cancellations observed immediately; committed `5ecbe4d2`)
- `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs` (NIGHT-07: legacy `Connected` status strings do not promote `IsConnected` from false; two-phase `outcome == Connected` refreshes status; generic catch preserves green only if already typed-ready; committed `5ecbe4d2`)
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs` (NIGHT-07: `SyncConnectedWithVpnRuntime` requires `IsConnected` before restoring status for owned engine; owned poll presence never promotes to readiness; external service behavior retained; committed `5ecbe4d2`)
- `VPNRouter.Tests/CommittedFirewallConfigTests.cs` (NIGHT-05 internal capability contract and isolation tests; committed `b7406456`, `8ee89105`)
- `VPNRouter.Tests/LinuxFirewallManagerTests.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint tests committed `ef82aadc`; NIGHT-05 committed config stale/no file, active refresh, failed refresh, malformed JSON, disarm tests; committed `b7406456`, `8ee89105`)
- `VPNRouter.Tests/MacFirewallManagerTests.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint tests committed `ef82aadc`; NIGHT-05 committed config stale/no file, active anchor/legacy refresh, failed refresh, malformed JSON, disarm tests; committed `b7406456`, `ecb682ef`, `8ee89105`)
- `VPNRouter.Tests/SingBoxManagerRestartTunLockTests.cs` (NIGHT-08 restart result and guard regression tests committed `1ca812f4`)
- `VPNRouter.Tests/VpnEngineApplyEscalationTests.cs` (NIGHT-08 source-pin without comment bypass committed `143d2adf`)
- `VPNRouter.Tests/VpnEngineApplyStructuralChangeTests.cs` (NIGHT-08 Apply failure baseline preservation tests committed `143d2adf`, `1ca812f4`; NIGHT-05 failed Apply zero capability calls, hot-reload success exact JSON / one PUT, StartupPipeline cold ordering source guard committed `b7406456`, `8ee89105`; NIGHT-06 failed/successful actual Apply failover context retention and lazy null reset, no-op start on already running engine committed `da3c21e7`)
- `VPNRouter.Tests/MvmTwoPhaseStartTimerTests.cs` (NIGHT-07 behavioral coordinator tests: clean startTask never readiness, same A/B timer retained, late typedConnected succeeds, faults/cancel immediate; committed `5ecbe4d2`)
- `VPNRouter.Tests/NightTypedReadinessTests.cs` (NIGHT-07 UI source-guard tests: legacy status no promotion, owned poll presence no readiness, generic catch guard, external service retained; committed `5ecbe4d2`)
- `VPNRouter.Tests/NightFailoverIntentTests.cs` (NIGHT-06 failover lifecycle, settings context, same object reuse, stale generation/identity guards before teardown, and source order tests; committed `da3c21e7`)
- `VPNRouter.Tests/NightProbeConcurrencyCancellationTests.cs` (NIGHT-09/10 tests: peak concurrency bounded to 8, caller best-server tail preserved, actual loopback UDP reply, afterdatagramcancel, silent2s timeout; committed `530ca9e7`)

**Gate results**:
- [-] Gate 1: BATCH 1, BATCH 2 (NIGHT-04, NIGHT-05 FULL INTEGRATION `8ee89105635bc750932084c6dad09467cbe1d8b9`, run 33954240530), BATCH 3 (NIGHT-08 `1ca812f4`, run 33951772995; NIGHT-07 `5ecbe4d2`, run 33955634699; NIGHT-06 `da3c21e740c7b5905158cbf6d4b568d922ff8bf7`, run 33956946168), and BATCH 4 (NIGHT-09/10 `530ca9e7c8c8074267b67398d80d67f2022ff4df`, run 33957757306) PASS ON CI. Limits full solution/live/macOS unchanged; red witnesses/full solution/native gaps remain; no live VPN/infra. NIGHT-12 minimal secret wiring pending CI. NIGHT-11 pending.
- [-] Gate 2: BATCH 1, BATCH 2 (NIGHT-04, NIGHT-05 FULL INTEGRATION `8ee89105635bc750932084c6dad09467cbe1d8b9`, run 33954240530: 3163 total / 3105 passed / 58 skipped), BATCH 3 (NIGHT-08 `1ca812f4`, run 33951772995; NIGHT-07 `5ecbe4d2`, run 33955634699: 3184 total / 3126 passed / 58 skipped; NIGHT-06 `da3c21e740c7b5905158cbf6d4b568d922ff8bf7`, run 33956946168: 3194 total / 3136 passed / 58 skipped), and BATCH 4 (NIGHT-09/10 `530ca9e7c8c8074267b67398d80d67f2022ff4df`, run 33957757306: 3221 total / 3163 passed / 58 skipped) PASS ON CI. NIGHT-12 minimal secret wiring pending CI. NIGHT-11 pending.
- [x] Gate 3: PASS (brief updated; ledger all still open pending closure evidence; no fake full gate; NIGHT-FOLLOWUP-01 registered; no closure claim for all 12).
- [-] Gate 4: PARTIAL / PENDING (Batch 1, Batch 2 [NIGHT-04, NIGHT-05 full integration], Batch 3 [NIGHT-08, NIGHT-07, NIGHT-06], and Batch 4 [NIGHT-09/10] green on CI; NIGHT-12 minimal secret wiring pending CI; NIGHT-11 pending; red witnesses/full solution/native gaps remain; no live VPN/infra; full review gate remains open; ledger all still open pending closure evidence; no fake full gate; no closure claim for all 12).
- [-] Gate 5: N/A / PENDING (no visual UI redesign; ViewModel status/readiness verified via headless/characterization tests on CI; NIGHT-07 UI source guard only, coordinator behavioral fake events tests; no live run/fullsolution/native mac).
- [-] Gate 6: PENDING (characterization diff across batches).

**Follow-ups**: Verify NIGHT-12 minimal secret wiring on CI; implement and verify NIGHT-11 (ConnStats stale indicator / generation binding); final review; maintain `plans/OPEN-DEFECTS.md` (all defects still open pending closure evidence; no fake full gate; no closure claim for all 12; red witnesses/full solution/native gaps remain; no live VPN/infra); address preexisting P1 survivor NIGHT-FOLLOWUP-01 (outside approved 12 repair scope, deferred owner follow-up); track NIGHT-DOC in PR #235 review; profile owner monitor (NIGHT-MEASURE).

### Batch 1 Outcome

- **Status**: PASS on CI (all 4 checks green).
- **Commit & CI**: All 4 checks PASS on commit `4d029a54`, GitHub Actions run `33947035219`. Receipt: https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5549652101.
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` remains open pending evidence for the whole task.
- **Batch 1 Scope**:
  - **NIGHT-01**: Process ownership verified; removed port-only kill; failed exact stop retains own handle; foreign listeners no longer cause false "Running" state.
  - **NIGHT-02**: Endpoint-aware final DNS; unified destination classification across outbounds and endpoints so WireGuard endpoint destinations are not treated as local and synthesized `vpnrouter-vpn-dns` detours are not rewritten to `dns-direct`.
  - **NIGHT-03**: Effective strict precedence; `StrictDns == true` overrides `DnsMode == "smart"` on generated and custom exclude rules, ensuring all matched DNS queries route through `vpn-dns`.
- **Review & Corrections**: Lead caught unsafe regression tests (never executed), shared-path fixture, platform compile issue, and failure-state issues; workers corrected; CI verified green.
- **CI Pipeline Scope & Gate 1 Limitation**:
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner excludes `HeadlessGuiTests`, `PageScreenshotTests`, and `VisualDiffTests`.
  - Windows runner tests `Characterization`, `PostShipVerifierContractTests`, and `BratVerifierContractTests`, and publishes CLI.
  - It DOES NOT prove full solution, macOS, or live behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged.
- **Test Selection & Execution Status**:
  - `TgProxyOwnershipCharacterizationTests` and `NightDnsPrivacyRegressionTests` executed and passed on CI.

### Batch 2 (NIGHT-04 Sub-batch) Outcome

- **Status**: PASS on CI (all 4 checks green).
- **Commit & CI**: All 4 checks PASS on commit `4ce2fc309c0bf96ff8734d3cd783639ec83d97a7`, GitHub Actions run `33948404811`. Receipt: https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5549823631.
- **Test Metrics**: 3102 total / 3045 passed / 57 skipped on Ubuntu runner. Unix classes (`LinuxFirewallManagerTests`, `MacFirewallManagerTests`) explicitly ran on Ubuntu; Windows filter did not select Unix tests.
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` remains open pending evidence for the whole task.
- **Batch 2 Scope (NIGHT-04)**:
  - **LinuxFirewallManager**: Linux marker cleared only upon confirmed cleanup. When `nft` table delete fails, verifies absence through successful structured `nft -j list tables` (not stderr guess), preserving recovery trigger for subsequent runs.
  - **MacFirewallManager**: macOS marker cleared only upon confirmed cleanup. Retained pf token retry and unknown marker conservative no broad restore.
  - **Test Isolation**: Unit tests fully isolate temp config/rules with no side effects on host firewall state.
- **Lead Corrections**: Multiple lead corrections applied during source review prior to commit:
  1. Unguarded Mac cold DeleteAll marker.
  2. Internal-enum public-test compile hazard.
  3. Malformed nft inventory.
  4. Unsafe raw marker content logging.
- **CI Pipeline Scope & Gate 1 Limitation**:
  - All 4 CI checks green on run 33948404811.
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner executes Core unit tests including Unix firewall tests; Windows runner tests `Characterization`, `PostShipVerifierContractTests`, and `BratVerifierContractTests`, and publishes CLI.
  - Does NOT prove full solution, macOS runner, or live system behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged.

### Batch 2 (NIGHT-05 Endpoint-Only Substep) Outcome

- **Status**: PASS on CI (all 4 checks green); NIGHT-05 NOT resolved (freshness remains).
- **Commit & CI**: All 4 checks PASS on commit `ef82aadc4389de08f3a67ef356e2f203d866c1bb`, GitHub Actions run `33949288379`.
- **Test Metrics**: Ubuntu runner 3114 total / 3057 passed / 57 skipped (12 new tests: 3114 vs prior 3102; Unix firewall endpoint test suites executed on Ubuntu).
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` remains open pending evidence for the whole task.
- **Batch 2 Scope (NIGHT-05 Endpoint-Only Substep)**:
  - **Endpoint Extraction**: `ReadServerIps` in `LinuxFirewallManager` and `MacFirewallManager` parses both `outbounds` server addresses and WireGuard peer addresses from `endpoints` (accepted endpoint type ONLY wireguard including AmneziaWG obfuscation fields (not type amneziawireguard), with `peers[].address`), supporting both IPv4 and IPv6 (AAAA) addresses.
  - **Canonical Literal Validation**: Uses `IPAddress.TryParse` to validate and normalize IP literals into canonical form, filtering duplicates case-insensitively and ignoring non-IP domain strings.
  - **Malformed Sibling Handling**: Robust against malformed JSON siblings or invalid elements; valid endpoints are still extracted even if sibling nodes are missing, malformed, or invalid.
  - **No Local Tunnel / Allowed_IPs**: Explicitly extracts only server and remote peer addresses; does not extract local tunnel interfaces or client `allowed_ips`/addresses (which are not bypass candidates).
  - **Test Isolation**: Comprehensive unit tests added in `LinuxFirewallManagerTests` and `MacFirewallManagerTests` covering IPv4, IPv6, Amnezia WireGuard, malformed siblings, and duplicate filtering with isolated temp paths.
- **Lead Corrections & Review**: Endpoint extraction implementation verified against source constraints; no invalid allowed_ips or local tunnel endpoints included.
- **Pending Integration & Unresolved Status**:
  - Freshness/commit integration still pending and NIGHT-05 NOT resolved (freshness remains).
  - Full resolution requires runtime freshness, bypass refresh on successful Apply, and `StartupPipeline` integration.
  - Depends on truthful restart commit NIGHT-08 (`SingBoxManager.ReloadConfigJsonWithResult` consumed by `VpnEngine.ApplyAsync`) to prevent bypass updates on declined or failed restarts.
- **CI Pipeline Scope & Gate 1 Limitation**:
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner executes Core unit tests including Unix firewall endpoint tests.
  - Does NOT prove full solution, macOS runner, or live system behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged.

### Batch 3 (NIGHT-08) Outcome

- **Status**: PASS on CI (all 4 checks green).
- **Commit & CI**: All 4 checks PASS on commit `1ca812f4400c9e8d881532e0baf9277513fa4109` (preceded by `143d2adf`), GitHub Actions run `33951772995`. Receipt: https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550206811.
- **Test Metrics**: 3121 total / 3063 passed / 58 skipped on Ubuntu runner.
- **Test Selection & Execution Details**:
  - Actual Apply false branch + retainedStop retry + ack hotreload executed on Ubuntu runner.
  - Successful Windows restart test not selected by Windows filter (Windows runner filter explicitly selects `Category=Characterization`, `Category=PostShipVerifierContractTests`, and `Category=BratVerifierContractTests`).
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` all still open pending closure evidence; no fake full gate.
- **Batch 3 Scope (NIGHT-08)**:
  - **Boolean Outcome Guards Actual Apply Baseline**: `SingBoxManager.ReloadConfigJsonWithResult` returns an explicit boolean indicating whether hot-reload or restart succeeded. In `VpnEngine.ApplyAsync`, this boolean outcome guards the actual Apply baseline (`ActiveConfigMode`, `ActiveRoutingMode`, `TunFingerprint`, `ActiveAppRoutingFingerprint`). If reload/restart returns `false`, `RestoreActiveBaseline()` is invoked, an error status is emitted, and `ApplyAsync` returns `false` without committing candidate metadata or claiming success.
  - **Exact-Stop Retry Retained**: When `_exactStopUnconfirmed` is true, `SingBoxManager` retains the retry path to attempt `StopInternal(releaseLock: false)` before deciding whether restart can safely proceed, returning `false` if the unconfirmed stop cannot be settled.
  - **Public Void Signatures Preserved**: Public `void ReloadConfigJson(string configJson, bool forceRestart = false)` forwards to `ReloadConfigJsonWithResult` without altering public method signatures, preserving backwards compatibility across callers. Public `Restart()` and `Stop()` signatures are likewise preserved.
  - **TUN Ownership & Lease Guards**: `ReloadConfigJsonWithResult` and `TryReloadConfigJson` explicitly check `_ownsTunLock` and `_disposed`, refusing reload or restart without a valid TUN lease.
- **Lead Review & Corrections**:
  - **Source-Pin Pass Rejected & Corrected**: Lead rejected a naive comment-based source-pin pass in `VpnEngineApplyEscalationTests` where source comments could satisfy pin assertions. Corrected via Gemini by stripping comment lines before validating that `ReloadConfigJsonWithResult` receives `forceRestart` and aggregates structural changes before consumption.
  - **Baseline Seeding via Private Property Reflection**: Applied in commit `1ca812f4` so Apply baseline properties are safely seeded through reflection without fragile state side effects.
  - **Fixture Safety Scrutinized**: Test classes (`SingBoxManagerRestartTunLockTests`, `VpnEngineApplyStructuralChangeTests`) serialized under `[Collection(SafeModeStateCollection.Name)]`, temp data directories isolated with `AppPaths.OverrideDataDir`, mock HTTP and process runner dependencies cleaned up in `finally` blocks, and lease states safely restored before test execution.
- **Preexisting Survivor Identified (NIGHT-FOLLOWUP-01)**:
  - During review of `SingBoxManager.Lifecycle.cs`, a separate preexisting P1 defect was identified: `StartWithJsonCore` catch (~line 88) and `RestartCore` catch (~line 590) unconditionally call `ReleaseTunOwnership()` even after `LaunchProcess` can throw from the `Started` callback with a retained live handle (`startedHandle`/`Started` invoke near line 905).
  - Evidence: `_runner.Start(request)` succeeds, sets `_handle`, and invokes `Started?.Invoke(startedHandle.Pid)`; an exception thrown from the `Started` event handler bubbles to the catch block which calls `ReleaseTunOwnership()`, orphaning the running sing-box process while releasing the TUN lock.
  - Preexisting condition, not introduced by NIGHT-08. Outside the approved 12-repair scope; registered in `plans/OPEN-DEFECTS.md` as `NIGHT-FOLLOWUP-01` and deferred for owner follow-up, not fixed here.
- **CI Pipeline Scope & Gate 1 Limitation**:
  - All 4 CI checks green on run 33951772995.
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner executes Core unit tests; Windows runner tests `Characterization`, `PostShipVerifierContractTests`, and `BratVerifierContractTests`, and publishes CLI.
  - Does NOT prove full solution, macOS runner, or live system behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged.

### Batch 2 (NIGHT-05 Full Integration) Outcome

- **Status**: PASS on CI (all 4 checks green).
- **Commit & CI**: All 4 checks PASS on commit `8ee89105635bc750932084c6dad09467cbe1d8b9` (preceded by `b7406456`, `ecb682ef`), GitHub Actions run `33954240530`. Receipt: https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550488707.
- **Test Metrics**: 3163 total / 3105 passed / 58 skipped on Ubuntu runner (42 new tests: 3163 vs prior 3121; all Unix firewall tests and integration test suites passed).
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` all still open pending closure evidence for whole task; no fake full gate.
- **Batch 2 Scope & Implementation (NIGHT-05 Full Integration)**:
  - **Internal Optional Capability Interface**:
    - Introduced `internal interface ICommittedFirewallConfig` (`void UpdateCommittedConfig(string configJson, bool enabledForFullTunnel)`) in `VPNRouter.Core/Interfaces/IFirewallManager.cs`.
    - Narrow internal capability; implemented explicitly by `LinuxFirewallManager` and `MacFirewallManager`. Windows `FirewallManager` and `NullFirewallManager` remain unchanged and do not implement this capability.
  - **StartupPipeline Lifecycle Ordering**:
    - Phase 6 (`CreateFirewallPhase`): skips legacy `CreateBlockRules` when firewall implements `ICommittedFirewallConfig` (`profile.BlockOnVpnFail && firewall is not ICommittedFirewallConfig`), eliminating the startup read of stale or missing on-disk `current.json`.
    - Phase 8 (`StartMonitorsPhase`): passes committed configuration directly to `committedFirewall.UpdateCommittedConfig(configJson, profile.BlockOnVpnFail && isFullTunnel)` only AFTER confirmed sing-box startup (`await StartSingBoxPhaseAsync`) and BEFORE background monitors start (`StartMonitorsPhase`).
  - **Truthful Commit on Apply (Bool Ack Guard)**:
    - In `VpnEngine.ApplyAsync`, `UpdateFirewallCommittedConfig` is invoked ONLY after confirmed boolean acknowledgment of successful hot-reload or restart (`ReloadConfigJsonWithResult` returning `true`).
    - If hot-reload or restart returns `false`, `ApplyAsync` invokes `RestoreActiveBaseline()`, emits an error status, returns `false`, and executes ZERO calls to `ICommittedFirewallConfig.UpdateCommittedConfig`.
  - **Unix Loaded Atomic Refresh (No Unblock / No -E)**:
    - `LinuxFirewallManager`: when ruleset is already loaded (`_loaded == true`), atomic refresh writes the updated ruleset to disk and executes `nft -f` in-place without invoking `DeleteTable()` or disabling/unblocking first, eliminating the egress leak window during config updates.
    - `MacFirewallManager`: when ruleset is already loaded (`_loaded == true`), atomic refresh writes the updated rules to disk and loads them into anchor (`pfctl -a Anchor -f`) or main ruleset (`pfctl -f` in legacy mode) in-place without re-invoking `EnsureCarrier()` (`pfctl -sr`), without acquiring an additional ref-counted `-E` token, and without flushing rules or unblocking first.
  - **Disarm Before DNS / Parse**:
    - In `UpdateCommittedConfig`, if `!enabledForFullTunnel` (disabled branch), the manager sets `_armed = false` and calls `DisableBlockRules()` immediately BEFORE any hostname DNS resolution or JSON parsing occurs. This avoids unnecessary DNS resolution queries or exceptions when the kill-switch is disarmed.
  - **Invalid JSON Preserves Prior Cache**:
    - In `ParseServerIps`, root elements that are not JSON objects (`[]`, `null`, strings, numbers) throw `JsonException`.
    - In `UpdateCommittedConfig`, any parse failure logs a warning and retains the prior `_serverIps` allowlist cache instead of wiping it or turning it into an empty cache.
    - An empty JSON object `{}` is treated as valid committed config with an empty server list.
  - **Windows Unchanged**:
    - Windows `FirewallManager` does not implement `ICommittedFirewallConfig`; Windows netsh firewall rules and behavior remain completely unchanged (netsh, not WFP).
  - **Thread Safety**:
    - Both `LinuxFirewallManager` and `MacFirewallManager` serialize internal state access (`_serverIps`, `_armed`, `_loaded`, `_anchorMode`) behind private `_gate` lock.
- **Test Suite & Verification**:
  - `CommittedFirewallConfigTests.cs`:
    - Tests narrow internal contract and explicit interface implementation by Unix managers.
    - Asserts Windows `FirewallManager` and `NullFirewallManager` do not implement the capability.
    - Asserts `UpdateCommittedConfig` never accesses `_currentConfigPath` on disk (stale/no current file).
    - Asserts `ParseServerIps` extracts outbounds and WireGuard peers for both IPv4 and IPv6 (AAAA).
  - Fake/temp tests in `LinuxFirewallManagerTests.cs` and `MacFirewallManagerTests.cs`:
    - Stale on-disk file ignored in favor of committed string; missing config file succeeds without error.
    - Active refresh failure retains prior allowlist, loaded flag, and marker without unblocking.
    - Active refresh succeeds atomically without delete table / carrier / -E / flush unblock.
    - Malformed JSON retains prior cache.
    - Disabled mode disarms and lifts rules without invoking DNS resolver.
  - `VpnEngineApplyStructuralChangeTests.cs`:
    - Failed Apply on exact branch results in zero firewall capability calls and restores active baseline.
    - Successful hot-reload triggers exactly one capability call with exact generated JSON and intent, exactly one HTTP PUT, and rejects zero calls.
    - StartupPipeline cold ordering verified via stripped-comment source inspection (Phase 6 skips legacy capability; StartSingBoxPhaseAsync -> UpdateCommittedConfig -> StartMonitorsPhase).
    - Cold ordering is source-only, not live run (real sing-box process and OS monitors require network stack / privileges).
- **CI Pipeline Scope & Gate 1 Limitation**:
  - All 4 CI checks green on run 33954240530.
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner executes Core unit tests including Unix firewall tests. Windows runner tests `Characterization`, `PostShipVerifierContractTests`, and `BratVerifierContractTests`, and publishes CLI.
  - Does NOT prove full solution, macOS runner, or live system behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged.

### Batch 3 (NIGHT-07) Outcome

- **Status**: PASS on CI (all 4 checks green).
- **Commit & CI**: All 4 checks PASS on commit `5ecbe4d2ad275766d3c52f9589ad94b0be4b53b3`, GitHub Actions run `33955634699`. Receipt: https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550638820.
- **Test Metrics**: 3184 total / 3126 passed / 58 skipped on Ubuntu runner (21 new tests: 3184 vs prior 3163; coordinator behavioral tests and UI source guards executed on Ubuntu).
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` remains open with all defects open pending evidence for the whole task; do not close ledger or any gates prematurely.
- **Batch 3 Scope & Implementation (NIGHT-07 Readiness Truthfulness)**:
  - **Clean StartTask Never Readiness**:
    - In `TwoPhaseStartCoordinator.RunAsync`, Phase B ignores clean completion of `startTask`. Successful completion of the startup task indicates only that the pipeline completed, not that the data plane or tunnel is ready.
    - Phase B waits for the typed `Connected` event (`connectedTcs.Task`) against the deadline (`phaseBDelay`). Clean `startTask` completion never promotes or signals readiness.
  - **Same A/B Timer Retained**:
    - When `startTask` completes cleanly in Phase A (before `SingBoxStarted`) or Phase B, the coordinator continues awaiting events without resetting or extending the timer. The original Phase A / Phase B deadline is strictly preserved (`same A/B timer retained`).
  - **Late typedConnected Succeeds**:
    - If `startTask` completes cleanly and `Connected` fires later within the active Phase B window, the coordinator successfully returns `TwoPhaseStartOutcome.Connected`.
  - **Faults and Cancellations Observed Immediately**:
    - If `startTask` faults (throws an exception) or is cancelled, Phase B (and Phase A) does not wait out the remaining timer: the failure or cancellation is observed immediately, returning `StartTaskCompleted` or throwing / returning `Cancelled`.
  - **Legacy Status No Promotion**:
    - In `MainWindowViewModel.Connection.cs` (`OnEngineStatus`), legacy status strings starting with `"Connected"` or `"VPN Router is running"` check `if (!IsConnected) return;` and call `RestoreConnectedStatus();` only when `IsConnected` is already `true`.
    - They NEVER set `IsConnected = true` from `false`. Legacy engine log/status strings can refresh display text but cannot promote connection state or paint UI green before typed readiness.
    - Two-phase start outcome `TwoPhaseStartOutcome.Connected` explicitly calls `RestoreConnectedStatus()` to ensure UI transitions out of "Connecting..." to connected status.
    - In `ToggleConnectionAsync`'s generic `catch (Exception ex)`, green UI state is preserved ONLY if `IsConnected && _engine.IsRunning` (already typed-ready); if not, `_engine.Stop()` is called and `IsConnected = false`.
  - **Owned Poll Presence No Readiness**:
    - In `MainWindowViewModel.RuntimeStatus.cs` (`SyncConnectedWithVpnRuntime`), when `vpnRunning` is detected and an owned engine manager is active (`_engine.SingBoxPid != null || _engine.IsRunning`), `if (!IsConnected) return;` prevents polling presence or process detection from promoting `IsConnected` from `false` to `true`.
    - When `IsConnected` is already `true`, it calls `RestoreConnectedStatus()` to maintain display text without relabeling via service.
    - No `WindowsServiceHelper.IsRunning` or blocking service queries on every poll.
  - **External Service Behavior Retained**:
    - When `vpnRunning` is detected but no owned engine manager exists (unowned external service mode), existing adoption path (`IsConnected = true`, `ConnectButtonText = Strings.StopVPN`, `Connected via service`, `MarkTrueSplitServiceManagedIfNeeded()`) is retained.
- **Test Suite & Verification**:
  - **UI Source Guard Only** (`VPNRouter.Tests/NightTypedReadinessTests.cs`):
    - `OnEngineStatus_LegacyConnectedStrings_CannotSetIsConnectedFromFalse_SourceGuard`: verifies `if (!IsConnected) return;` and absence of `IsConnected = true` in legacy string branch.
    - `TwoPhaseOutcomeConnected_CallsRestoreConnectedStatus_SourceGuard`: verifies outcome connected calls `RestoreConnectedStatus()`.
    - `GenericCatch_PreservesGreenOnlyIfAlreadyTypedReady_SourceGuard`: verifies catch checks `IsConnected && _engine.IsRunning`.
    - `TwoPhaseStartCoordinator_PhaseBCtsCanceledOnlyAfterFinalOutcome_SourceGuard`: verifies cancellation token timing in coordinator.
    - `TwoPhaseStartCoordinator_PhaseA_CleanNoStartedWaitsUntilDeadline_SourceGuard`: verifies Phase A clean startTask wait.
    - `SyncConnectedWithVpnRuntime_OwnedEngineReadinessGuardAndServicePath_SourceGuard`: verifies owned engine presence guard and retained external service path.
  - **Coordinator Behavioral Fake Events Tests** (`VPNRouter.Tests/MvmTwoPhaseStartTimerTests.cs`):
    - 781 lines, 18 facts testing the full combinatorial matrix with synthetic delegate events:
    - `Started_ThenCleanStartCompletion_LaterConnectedSucceeds`
    - `CleanStartCompletion_ThenStarted_LaterConnectedSucceeds`
    - `CleanStartCompletion_NoStarted_TimesOutPhaseA`
    - `PhaseB_CleanStartCompletion_NoConnected_TimesOutPhaseB`
    - `PhaseB_StartTaskFaults_FailsImmediately`
    - `PhaseB_StartTaskCancelled_CancelsImmediately`
    - `PhaseA_StartTaskFaults_FailsImmediately`
    - `PhaseA_StartTaskCancelled_CancelsImmediately`
    - `PhaseB_CancellationTokenFires_CancelsImmediately`
    - `PhaseA_PreCancelled_CancelsImmediately`
    - `PhaseB_LateConnectedAfterCleanStart_Success`
    - Preserves same A/B timer across clean startTask completion.
- **CI Pipeline Scope & Gate Limits**:
  - All 4 CI checks green on run 33955634699.
  - Coordinator behavioral fake events tests (`MvmTwoPhaseStartTimerTests`) and UI source guards (`NightTypedReadinessTests`) executed on Ubuntu runner.
  - Does NOT prove full solution, macOS runner, or live system behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged.

### Batch 3 (NIGHT-06) Outcome

- **Status**: PASS on CI (all 4 checks green).
- **Commit & CI**: All 4 checks PASS on commit `da3c21e740c7b5905158cbf6d4b568d922ff8bf7`, GitHub Actions run `33956946168`. Receipt: https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550780691.
- **Test Metrics**: 3194 total / 3136 passed / 58 skipped on Ubuntu runner (10 new tests: 3194 vs prior 3184; failover lifecycle, settings context, same object reuse, and stale generation guard suites executed on Ubuntu).
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` remains open with all defects open pending evidence for the whole task; do not close ledger or any gates prematurely; no closure claim for all 12. Red witnesses, full solution, and native gaps remain; no live VPN/infra.
- **Batch 3 Scope & Implementation (NIGHT-06 Failover Settings Context & Lifecycle Synchronization)**:
  - **Private Settings + Generation Reset on Public New Start / Committed Apply Only (Lazy Null)**:
    - `VpnEngine` adds private fields `_failoverSettingsContext`, `_failoverGeneration`, and internal helper `ResetFailoverContext(AppSettings settings)`.
    - `ResetFailoverContext` resets `_failover = null` (lazy null, avoiding unconditional instantiation), increments `_failoverGeneration++`, and updates `_failoverSettingsContext = settings`.
    - Reset occurs ONLY on:
      1. Public new `StartAsync` after the already-running guard (`HasLiveOrStartingSingBox()`), ensuring no-op starts on an already-running engine do not reset context or cycle state.
      2. Successful committed `ApplyAsync` (both hot-reload success and restart success).
    - Failed `ApplyAsync` (e.g. reload or restart unconfirmed) preserves active baseline settings context and prior `_failover` instance (no reset).
  - **Engine Context Wires Settings + Generation in Same Closure**:
    - `WireFailoverCore` reads `var settings = _engine._failoverSettingsContext ?? CapturedSettings();` and `var generation = _engine._failoverGeneration;`.
    - Instantiates `_engine._failover ??= new AutoFailoverEngine(settings, sanityCheck, restart: (innerCt) => _engine.ExecuteFailoverRestartAsync(settings, innerCt, generation), logger: _engine._logger);`.
    - Both constructor argument (`settings` pool) and restart delegate closure share the exact same `settings` object and captured `generation`.
    - Routine wire calls (`WireFailover`, `WireFailoverWithStop`) preserve the lazy instance and tried cycle state (`_tried` set).
  - **Stale Guards on Generation + Identity After Gate Before Teardown**:
    - In `ExecuteProbeFailoverRestartAsync` (post-start): acquires `_lifecycleGate` first, then immediately checks:
      `if ((expectedGeneration.HasValue && expectedGeneration.Value != _failoverGeneration) || (_failoverSettingsContext is not null && !ReferenceEquals(_failoverSettingsContext, captured))) { ... return false; }`
      BEFORE calling `TeardownInternal()`.
      If generation or settings identity does not match active failover context, restart aborts truthfully without tearing down running tunnel, launching processes, or persisting state.
    - In `ExecuteFailoverRestartAsync` (pre-start): checks generation and settings identity before calling `StartAsyncInternal`.
  - **Test Suite & Verification**:
    - `VPNRouter.Tests/NightFailoverIntentTests.cs`:
      - `WireFailover_SameInternalWire_PreservesInstanceAndTriedCycle`: routine wire calls preserve same instance and tried cycle state across calls.
      - `ResetFailoverContext_CommittedSettings_LazyNullUntilNewWireWithPoolBAndCapturedB`: verifies immediate lazy null on commit, pool B identity and closure settings/generation matching.
      - `StaleAutoFailoverDelegate_InvocationAfterApplyOrReset_ReturnsFalseBeforeTeardown_RetainsManager_NoRunner_NoStore`: post-start stale failover delegate invocation returns false before teardown, retains manager, makes no runner calls, and does not save to settings store.
      - `StaleAutoFailoverDelegate_PreStartRestart_ReturnsFalseBeforeStartAsyncInternal`: pre-start stale delegate aborts without starting pipeline.
      - `WireFailover_ResetSameObjectNewIntent_OldRestartReturnsFalse_NoRunnerNoTeardown`: same object reuse with mutated intent aborts old restart closure via generation guard before teardown.
      - `WireFailover_PreStart_ResetSameObjectNewIntent_OldRestartReturnsFalse_NoRunner`: same object reuse in pre-start phase aborts old restart closure via generation guard.
      - `PublicStartBoundary_SourceOrder_ResetsFailoverContextAfterAlreadyRunningGuardAndBeforeStartAsyncInternal`: source order verification that `ResetFailoverContext` runs after already-running guard and before `StartAsyncInternal`.
    - `VPNRouter.Tests/VpnEngineApplyStructuralChangeTests.cs`:
      - `ApplyAsync_StructuralRestartFails_RestoresActiveBaselineAndDoesNotCommitCandidateMetadata`: failed actual Apply retains baseline settings context and failover instance.
      - `ApplyAsync_HotReloadSucceeds_CallsFirewallCapabilityOnceWithExactGeneratedAndIntent`: successful actual Apply updates settings context and resets failover to lazy null.
      - `StartAsync_SingBoxAlreadyRunning_NoOpStartDoesNotResetFailoverContext`: sing-box already running no-op start does not reset failover context.
- **CI Pipeline Scope & Gate 1 Limitation**:
  - All 4 CI checks green on run 33956946168.
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner executes Core unit tests including failover lifecycle and generation guard tests. Windows runner tests `Characterization`, `PostShipVerifierContractTests`, and `BratVerifierContractTests`, and publishes CLI.
  - Does NOT prove full solution, macOS runner, or live system behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged; red witnesses/full solution/native gaps remain; no live VPN/infra.

### Batch 4 (NIGHT-09 & NIGHT-10) Outcome

- **Status**: PASS on CI (all 4 checks green).
- **Commit & CI**: All 4 checks PASS on commit `530ca9e7c8c8074267b67398d80d67f2022ff4df`, GitHub Actions run `33957757306`. Receipt: https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5550872851.
- **Test Metrics**: 3221 total / 3163 passed / 58 skipped on Ubuntu runner (27 new tests: 3221 vs prior 3194; concurrency bounds, best-server preservation, UDP probe cancellation, and loopback verification suites executed on Ubuntu).
- **Defect Ledger**: `plans/OPEN-DEFECTS.md` remains open with all defects open pending evidence for the whole task; do not close ledger or any gates prematurely; no closure claim for all 12. Red witnesses, full solution, and native gaps remain; no live VPN/infra.
- **Batch 4 Scope & Implementation (NIGHT-09 & NIGHT-10)**:
  - **NIGHT-09 — Bounded Concurrency & Tail Best Preservation (`ServerHealthProbe`)**:
    - Bounded concurrency in `ServerHealthProbe.ProbeAllAsync` using constant worker limit of 8 (`bounded8`, `SemaphoreSlim(8)`).
    - Preserves caller best-server selection semantics (`tailbest`): probes all eligible servers up to concurrency limit without unrequested early-exit first-server, ensuring caller retains full visibility of all probe results and selects optimal server based on latency/metrics.
    - No unrequested config knob, benchmark claims, or caller semantic drift.
  - **NIGHT-10 — UDP Probe Receive Await & Cancellation Propagation (`TcpTlsProbe`)**:
    - Fixed unhandled cancelled UDP probe receive in `TcpTlsProbe.ProbeUdpAsync`.
    - Explicitly awaits `receiveTask` on UDP probes, handling cancellation tokens truthfully.
    - Catches and propagates `OperationCanceledException` so cancelled UDP probes throw or settle as cancelled and never falsely report alive, zero latency, or slow.
    - Actual loopback UDP reply, `afterdatagramcancel`, and `silent2s` behavior proved with rigorous characterization tests.
- **Test Suite & Verification (`VPNRouter.Tests/NightProbeConcurrencyCancellationTests.cs`)**:
  - `ProbeAllAsync_ConcurrencyBoundedTo8`: asserts peak concurrent probes never exceed 8 across large server batches.
  - `ProbeAllAsync_TailBestPreserved_ProbesAllWithoutEarlyExit`: verifies all servers probed and best server correctly identified by caller without premature termination.
  - `ProbeUdpAsync_ActualLoopbackReply_ReturnsSuccess`: verifies successful UDP probe round-trip when loopback listener replies.
  - `ProbeUdpAsync_AfterDatagramCancel_PropagatesCancellation`: verifies cancellation triggered after initial datagram sends propagates `OperationCanceledException` and does not report alive.
  - `ProbeUdpAsync_Silent2s_TimesOutOrCancelsTruthfully`: verifies silent endpoint with 2-second timeout settles without false alive reporting.
- **CI Pipeline Scope & Gate 1 Limitation**:
  - All 4 CI checks green on run 33957757306.
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner executes Core unit tests including probe concurrency and UDP cancellation tests. Windows runner tests `Characterization`, `PostShipVerifierContractTests`, and `BratVerifierContractTests`, and publishes CLI.
  - Does NOT prove full solution, macOS runner, or live system behavior; Gate 1 remains a full solution limitation; limits full solution/live/macOS unchanged; red witnesses/full solution/native gaps remain; no live VPN/infra.

### Batch 4 (NIGHT-12) Implementation Outcome (Pending CI) & NIGHT-11 Status

- **Status**: NIGHT-12 minimal secret wiring implemented in working tree pending CI; NIGHT-11 pending.
- **NIGHT-12 Minimal Secret Wiring**:
  - `ClashLogStream`: constructor updated to accept `secret` parameter and authenticate WebSocket connection via Bearer authorization header or query token.
  - `VpnEngine`: supplies `settings.SingBox.ClashApiSecret` to `ClashLogStream` constructor to authenticate WebSocket connection without leaking secrets in logs.
  - Pending CI verification.
- **NIGHT-11 Pending**:
  - `MainWindowViewModel.ConnStats.cs` & `ClashSingBoxApi.cs`: distinguish API error from zero metrics, show stale indicator or clear stats on failure, bind updates to active session generation.
- **Pending Batches & Scope Limits**:
  - Batch 4 (NIGHT-09 and NIGHT-10 verified on CI; NIGHT-12 minimal secret wiring implemented pending CI; NIGHT-11 pending).
  - Defect ledger `plans/OPEN-DEFECTS.md` remains open with all defects open pending evidence for the whole task; do not close ledger or any gates prematurely.
  - No closure claim for all 12 defects; red witnesses, full solution, and native macOS/Windows gaps remain; no live VPN/infra.
