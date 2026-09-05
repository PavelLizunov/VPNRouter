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
- [ ] **Gate 3 — Docs**: **PASS** (Brief revised; `plans/OPEN-DEFECTS.md` restored and active; report imported).
- [ ] **Gate 4 — Independent review**: **PENDING** (Adversarial review / bug-hunt per batch).
- [ ] **Gate 5 — UI verify**: **N/A / PENDING** (No visual redesign; ViewModel status/readiness verified via headless/characterization tests on CI).
- [ ] **Gate 6 — Characterization diff**: **PENDING** (Coordinator state transitions and error propagation baselined).

---

## Outcome

**Status**: PLANNING COMPLETE / READY FOR BATCH 1 EXECUTION
**Commits**: None (planning phase; no commits created on task branch yet)
**Branch**: `dsh/fix-night-audit-gemini-2026-09-04` (based on accepted `main` `b7ce0e4f`, will track task origin branch, not `origin/main`)
**Test deltas**: 0 executed locally (control plane lacks .NET SDK; CI inspection pending)
**Files changed**:
- `plans/OPEN-DEFECTS.md` (restored from audit commit `7678e6ef`, 12 findings open)
- `plans/overnight-audit-morning-report-2026-09-04.md` (restored from audit commit `7678e6ef`)
- `plans/phase-fix-night-audit-gemini-2026-09-04.md` (repair brief revised per lead review)

**Gate results**:
- [-] Gate 1: EVIDENCE PENDING (CI workflow command inspection required)
- [-] Gate 2: BLOCKED on control plane (CI execution required)
- [x] Gate 3: PASS (brief updated, open defect ledger restored, report imported)
- [-] Gate 4: PENDING (to be executed per batch)
- [-] Gate 5: N/A for planning phase (no visual changes)
- [-] Gate 6: PENDING (characterization tests planned for Batches 1–4)

**Follow-ups**: Inspect CI workflows for exact commands; coordinate with Lead to execute Batch 1; maintain `plans/OPEN-DEFECTS.md` until green CI evidence gathered; track NIGHT-DOC in PR #235 review; profile owner monitor (NIGHT-MEASURE).

### Batch 1 Interim Outcome

- **Status**: Gemini implementation awaiting CI; final review pending.
- **Commit & CI**: Brief commit `c7bd9c48` has all 4 checks green on PR #240. Defect ledger (`plans/OPEN-DEFECTS.md`) remains open.
- **Batch 1 Scope**:
  - **NIGHT-01**: Process ownership verified; removed port-only kill; failed exact stop retains own handle; foreign listeners no longer cause false "Running" state.
  - **NIGHT-02**: Endpoint-aware final DNS; unified destination classification across outbounds and endpoints so WireGuard endpoint destinations are not treated as local and synthesized `vpnrouter-vpn-dns` detours are not rewritten to `dns-direct`.
  - **NIGHT-03**: Effective strict precedence; `StrictDns == true` overrides `DnsMode == "smart"` on generated and custom exclude rules, ensuring all matched DNS queries route through `vpn-dns`.
- **Review & Corrections**: Lead caught unsafe regression tests (never executed), shared-path fixture, platform compile issue, and failure-state issues; workers corrected, final review pending.
- **CI Pipeline Scope & Gate 1 Limitation**:
  - Existing CI (`test.yml`) builds `VPNRouter.Tests` project (Core and App dependencies).
  - Ubuntu runner excludes `HeadlessGuiTests`, `PageScreenshotTests`, and `VisualDiffTests`.
  - Windows runner tests `Characterization`, `PostShipVerifierContractTests`, and `BratVerifierContractTests`, and publishes CLI.
  - It DOES NOT prove full solution, macOS, or live behavior; Gate 1 remains a full solution limitation.
- **Test Selection & Execution Status**:
  - New `TgProxyOwnershipCharacterizationTests` selected on Windows and Ubuntu.
  - New `NightDnsPrivacyRegressionTests` selected on Ubuntu.
  - No dotnet tests executed yet; zero false test counts reported; defect ledger remains open.
  - Test coverage needs actual CI next.
