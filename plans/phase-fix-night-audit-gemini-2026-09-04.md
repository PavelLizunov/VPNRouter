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

- [ ] **Gate 1 — Build clean**: **EVIDENCE PENDING** (Control plane lacks .NET SDK; exact workflow commands must be inspected before asserting full solution build).
- [ ] **Gate 2 — Tests green**: **BLOCKED** on control plane (CI execution required; non-headless test suite).
- [ ] **Gate 3 — Docs**: **PASS** (Brief revised; `plans/OPEN-DEFECTS.md` restored and active; report imported; NIGHT-FOLLOWUP-01 registered).
- [ ] **Gate 4 — Independent review**: **PENDING** (Adversarial review / bug-hunt per batch; Batch 1, NIGHT-04, and NIGHT-05 endpoint substep green on CI; NIGHT-08 source reviewed pending CI; full review gate remains open).
- [ ] **Gate 5 — UI verify**: **N/A / PENDING** (No visual redesign; ViewModel status/readiness verified via headless/characterization tests on CI).
- [ ] **Gate 6 — Characterization diff**: **PENDING** (Coordinator state transitions and error propagation baselined).

---

## Outcome

**Status**: BATCH 1, NIGHT-04 & NIGHT-05 ENDPOINT SUBSTEP VERIFIED ON CI / NIGHT-08 CURRENT SOURCE REVIEWED PENDING CI / NIGHT-05 FRESHNESS REMAINS UNRESOLVED
**Commits**: Batch 1 on `4d029a54` (prior commits: `c7bd9c48`, `ff4bf3d4`, `389963cc`). Batch 2 (NIGHT-04) on `4ce2fc309c0bf96ff8734d3cd783639ec83d97a7`. Batch 2 (NIGHT-05 endpoint substep) on `ef82aadc4389de08f3a67ef356e2f203d866c1bb`. NIGHT-08 current source reviewed working tree changes pending CI.
**Branch**: `dsh/fix-night-audit-gemini-2026-09-04` (PR #240)
**Test deltas**: Batch 1 all 4 checks PASS on CI (`4d029a54`, run 33947035219). NIGHT-04 all 4 CI checks PASS (`4ce2fc309c0bf96ff8734d3cd783639ec83d97a7`, run 33948404811: 3102 total / 3045 passed / 57 skipped on Ubuntu; Unix classes explicitly ran on Ubuntu; Windows filter does not select Unix tests). NIGHT-05 endpoint substep all 4 checks PASS on CI (`ef82aadc4389de08f3a67ef356e2f203d866c1bb`, run 33949288379: Ubuntu 3114 total / 3057 passed / 57 skipped, 12 new tests). NIGHT-08 unit tests source-reviewed pending CI.
**Files changed**:
- `plans/OPEN-DEFECTS.md` (active defect ledger; stays open pending evidence for whole task; NIGHT-FOLLOWUP-01 registered)
- `plans/overnight-audit-morning-report-2026-09-04.md` (audit report)
- `plans/phase-fix-night-audit-gemini-2026-09-04.md` (brief updated with NIGHT-05 endpoint substep CI pass and NIGHT-08 source review outcome)
- `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint parsing committed `ef82aadc`)
- `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint parsing committed `ef82aadc`)
- `VPNRouter.Tests/LinuxFirewallManagerTests.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint unit tests committed `ef82aadc`)
- `VPNRouter.Tests/MacFirewallManagerTests.cs` (NIGHT-04 committed `4ce2fc30`; NIGHT-05 endpoint unit tests committed `ef82aadc`)
- `VPNRouter.Core/Services/SingBoxManager.cs` (NIGHT-08 `ReloadConfigJsonWithResult` and `TryReloadConfigJson` lease guards source-reviewed)
- `VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs` (NIGHT-08 `RestartCore` bool outcome and exact-stop failure handling source-reviewed)
- `VPNRouter.Core/Services/VpnEngine.cs` (NIGHT-08 `ApplyAsync` baseline restoration on reload/restart failure source-reviewed)
- `VPNRouter.Tests/SingBoxManagerRestartTunLockTests.cs` (NIGHT-08 restart result and guard regression tests source-reviewed)
- `VPNRouter.Tests/VpnEngineApplyEscalationTests.cs` (NIGHT-08 source-pin without comment bypass source-reviewed)
- `VPNRouter.Tests/VpnEngineApplyStructuralChangeTests.cs` (NIGHT-08 Apply failure baseline preservation tests source-reviewed)

**Gate results**:
- [-] Gate 1: BATCH 1, NIGHT-04, and NIGHT-05 ENDPOINT SUBSTEP PASS ON CI (`ef82aadc4389de08f3a67ef356e2f203d866c1bb`, run 33949288379). Limits full solution/live/macOS unchanged. NIGHT-05 freshness and NIGHT-08 pending CI.
- [-] Gate 2: BATCH 1, NIGHT-04, and NIGHT-05 ENDPOINT SUBSTEP PASS ON CI (`ef82aadc4389de08f3a67ef356e2f203d866c1bb`, run 33949288379). NIGHT-05 freshness and NIGHT-08 pending CI.
- [x] Gate 3: PASS (brief updated; ledger stays open pending evidence for whole task; NIGHT-FOLLOWUP-01 registered).
- [-] Gate 4: PARTIAL / PENDING (Batch 1, NIGHT-04, and NIGHT-05 endpoint substep reviewed and green on CI; NIGHT-08 current source reviewed pending CI; full review gate remains open pending CI and whole-task completion).
- [-] Gate 5: N/A (no visual UI redesign).
- [-] Gate 6: PENDING (characterization diff across batches).

**Follow-ups**: Commit and push NIGHT-08 changes to PR #240 for CI verification; implement NIGHT-05 freshness/commit integration (freshness/commit integration still pending and NIGHT-05 NOT resolved; depends on truthful restart commit NIGHT-08); address preexisting P1 survivor NIGHT-FOLLOWUP-01 (outside approved 12 repair scope, deferred owner follow-up); maintain `plans/OPEN-DEFECTS.md` until green CI evidence gathered for whole task; track NIGHT-DOC in PR #235 review; profile owner monitor (NIGHT-MEASURE).

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

### Batch 3 (NIGHT-08) Source Review Outcome

- **Status**: Current source reviewed, pending CI.
- **Batch 3 Scope & Implementation (NIGHT-08)**:
  - **Boolean Outcome Guards Actual Apply Baseline**: `SingBoxManager.ReloadConfigJsonWithResult` returns an explicit boolean indicating whether hot-reload or restart succeeded. In `VpnEngine.ApplyAsync`, this boolean outcome guards the actual Apply baseline (`ActiveConfigMode`, `ActiveRoutingMode`, `TunFingerprint`, `ActiveAppRoutingFingerprint`). If reload/restart returns `false`, `RestoreActiveBaseline()` is invoked, an error status is emitted, and `ApplyAsync` returns `false` without committing candidate metadata or claiming success.
  - **Exact-Stop Retry Retained**: When `_exactStopUnconfirmed` is true, `SingBoxManager` retains the retry path to attempt `StopInternal(releaseLock: false)` before deciding whether restart can safely proceed, returning `false` if the unconfirmed stop cannot be settled.
  - **Public Void Signatures Preserved**: Public `void ReloadConfigJson(string configJson, bool forceRestart = false)` forwards to `ReloadConfigJsonWithResult` without altering public method signatures, preserving backwards compatibility across callers. Public `Restart()` and `Stop()` signatures are likewise preserved.
  - **TUN Ownership & Lease Guards**: `ReloadConfigJsonWithResult` and `TryReloadConfigJson` explicitly check `_ownsTunLock` and `_disposed`, refusing reload or restart without a valid TUN lease.
- **Lead Review & Corrections**:
  - **Source-Pin Pass Rejected & Corrected**: Lead rejected a naive comment-based source-pin pass in `VpnEngineApplyEscalationTests` where source comments could satisfy pin assertions. Corrected via Gemini by stripping comment lines before validating that `ReloadConfigJsonWithResult` receives `forceRestart` and aggregates structural changes before consumption.
  - **Fixture Safety Scrutinized**: Test classes (`SingBoxManagerRestartTunLockTests`, `VpnEngineApplyStructuralChangeTests`) serialized under `[Collection(SafeModeStateCollection.Name)]`, temp data directories isolated with `AppPaths.OverrideDataDir`, mock HTTP and process runner dependencies cleaned up in `finally` blocks, and lease states safely restored before test execution.
- **Preexisting Survivor Identified (NIGHT-FOLLOWUP-01)**:
  - During review of `SingBoxManager.Lifecycle.cs`, a separate preexisting P1 defect was identified: `StartWithJsonCore` catch (~line 88) and `RestartCore` catch (~line 590) unconditionally call `ReleaseTunOwnership()` even after `LaunchProcess` can throw from the `Started` callback with a retained live handle (`startedHandle`/`Started` invoke near line 905).
  - Evidence: `_runner.Start(request)` succeeds, sets `_handle`, and invokes `Started?.Invoke(startedHandle.Pid)`; an exception thrown from the `Started` event handler bubbles to the catch block which calls `ReleaseTunOwnership()`, orphaning the running sing-box process while releasing the TUN lock.
  - Preexisting condition, not introduced by NIGHT-08. Outside the approved 12-repair scope; registered in `plans/OPEN-DEFECTS.md` as `NIGHT-FOLLOWUP-01` and deferred for owner follow-up, not fixed here.
- **Pending CI**:
  - Working tree changes reviewed and ready for commit and push to PR #240 for CI verification.
  - Defect ledger (`plans/OPEN-DEFECTS.md`) remains open pending CI evidence and whole-task completion.
- **Ledger & Constraints**:
  - Defect ledger (`plans/OPEN-DEFECTS.md`) stays open pending evidence for whole task.
  - No fake complete or full gate claims; limits on full solution / live / macOS unchanged.
