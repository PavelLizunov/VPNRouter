> **TL;DR:** The lane reports contain real defects, but their **18 P0 labels are not defensible as a set**: several depend on disproven assumptions, omit existing mitigations, or describe deliberate fail-closed behavior. Sanction the agent-map structure below, but **do not declare the map complete or Omarchy runtime-ready**: inventory coverage is inconsistent, production runtime authorization remains intentionally unavailable, and confirmed lifecycle, persistence, logging, and consumer-recovery defects require correction.

# Phase 3 — Architectural Review & Reconciliation

## 1. Scope, evidence, and verdict

This was a **read-only source review**. No implementation, configuration changes, service execution, delegation, or goal operations were performed.

Reviewed source baselines:
- VPNRouter HEAD: `5be8952a181f6bc66b41fd26f60dd93e6cc74951`
- Omarchy plugin HEAD: `541b11246dcedcb3e55ea389b91c35cc9f26ce4b`
- VPNRouter’s ledger and Omarchy phase document have working-tree modifications; agent-map artifacts are untracked.
- All six lane reports were read. All 18 packet-listed P0 candidates were reconciled against relevant source and callers.
- Additional high-impact Headless findings were checked where necessary to establish continuation requirements.

**Verification performed:** source inspection, Git identity/status, and read-only inventory/checkpoint counting. **Not performed:** compilation, test execution, native firewall changes, packet capture, Android runtime checks, Windows ACL reproduction, or live Omarchy activation.

### Architectural verdict

**CHANGES REQUIRED; bounded source-only continuation is appropriate.**

This report:
1. Recommends which findings the coordinator should merge or reconcile in `plans/OPEN-DEFECTS.md`.
2. Approves a documentation structure—not a claim of exhaustive coverage.
3. Separates ordinary source work from unapproved privilege architecture and live deployment.

It does **not** authorize installation, privilege provisioning, activation, release, or a P0/P1 waiver. Under the existing ledger contract, accepted **P1** findings still block a stable cut.

---

## 2. Reconciliation of all critical candidates

### P01 — Lifecycle and concurrency

| Candidate | Reconciled disposition | Source-grounded reasoning |
|---|---|---|
| **P01-01: Linux pkexec makes `IsHealthy()` permanently false** | **Reject the asserted critical defect. Historical premise already withdrawn.** | `SingBoxManager.Health.cs:33–51` does use the process handle on Linux. However, `SingBoxManager.Lifecycle.cs:924–970` launches foreground `pkexec <exe> run -c …`, not a detached command. The existing investigation, `plans/phase-unix-launch-provenance-design-2026-09-10.md:23–47`, explicitly withdrew the exited-wrapper premise after checking foreground execution. The stale comment in Health is not proof of detachment. An abnormal orphan/API-only scenario remains a separate question. **Do not replace owned-process checks with an API-only check as the proposed fix.** |
| **P01-02: stale warmup work survives failover** | **Confirmed P1; merge.** | `ExecuteProbeFailoverRestartAsync` retains the session CTS while restarting (`VpnEngine.cs:640–665`); teardown cancels `_probeCts` (`:1027`). Warmup uses the session token (`StartupPipeline.cs:1240–1352`). Typed `OnConnected` rejects stale generation/manager/handle identity (`VpnEngine.cs:1654–1667`), but the status emission and DNS-lockdown call lie outside that guard (`StartupPipeline.cs:1293,1322`). Therefore a stale probe can still publish status and enqueue stale DNS work. |

### P02 — Leak protection

| Candidate | Reconciled disposition | Source-grounded reasoning |
|---|---|---|
| **P02-01: fire-and-forget DNS restoration** | **Confirmed P1; merge.** | `WindowsDnsHardening.Restore():238–286` schedules rule deletion and returns without joining it. `VpnEngine.TeardownInternal():1062` does not await completion. Shutdown can leave rules behind. This establishes a cleanup-completion defect, not guaranteed complete DNS failure on every host. |
| **P02-02: overlapping DNS lockdown transitions** | **Confirmed P1; merge, grouped with P02-01/P02-04.** | `WindowsDnsHardening.cs:181–221` changes `_lockdownEffective` before scheduling unordered enable/disable work. It tracks intent, not successful installation. Even perfectly atomic Boolean updates would not serialize the external commands. |
| **P02-03: missing CGNAT causes Tailscale lockout** | **Confirmed compatibility/deployment hazard; P1 before remote firewall enablement.** | `LinuxFirewallManager.BuildRuleset():407–421` deliberately drops all output except loopback, listed IPv4 private/link-local ranges, and server endpoints. Tailnet destinations and public Tailscale transport/control traffic can be blocked. |
| **P02-04: DNS cancellation does not reach netsh** | **Confirmed P1, corrected mechanism.** | `FirewallManager.cs:724–824,873–891` passes the token only to `Task.Run`; the delegate never observes it. Ineffective five-second aggregate deadline: commands can continue, each using a separate three-second timeout. |
| **P02-05: failed Linux firewall disposal retains table/state** | **Reject as a new P0; deliberate recovery behavior.** | `LinuxFirewallManager.cs:203–231` retains loaded state and marker when removal cannot be confirmed. `LinuxFirewallManagerTests.cs:416–443` explicitly requires a later `Dispose()` retry to succeed. |

### P03 — Privilege and security

| Candidate | Reconciled disposition | Source-grounded reasoning |
|---|---|---|
| **P03-01: silent Windows ACL failure enables LPE** | **Confirmed trust-enforcement gap; P1, with exploitation conditional.** | `AppPaths.cs:254–287` swallows ACL failures and returns no trusted/untrusted result. Startup continues through `EnsureDirectories` and deployment; launch does not establish the executable's safe effective ACL. |
| **P03-02: no-scope capability verifier bypasses trust chain** | **P2 defense-in-depth gap; current production path mitigated.** | The fallback exists at `PlatformCapabilityVerifier.cs:77–88`. Its only production Headless caller found is `RouterSession.CanConnect`, which enters the retained policy (`RouterSession.cs:155–163`). |
| **P03-03: marker permissions are a P0 security defect** | **P2 hardening inconsistency; reject cross-user exploit as established.** | `LinuxFirewallManager.cs:566–573` uses `File.WriteAllText`, but normal startup enforces the parent data directory as `0700`. Typical `0644` marker is not world-writable. |

### P04 — Headless protocol

| Candidate | Reconciled disposition | Source-grounded reasoning |
|---|---|---|
| **P04-01: dead `_inFlightRequests` makes cancellation ineffective** | **Confirmed dead API path, P2; wire cancellation is working architecturally.** | The dictionary is declared, queried, and cleared but never populated. However, `ProtocolDispatcher.cs:70–76,134–177` intercepts wire `cancel` and cancels the operation CTS passed to the handler. |
| **P04-02: `RouterSession.DisposeAsync` races gate holders** | **Confirmed P1; Omarchy correction blocker.** | Disposal does not acquire/join the operation gate, but disposes it at `RouterSession.cs:584`; connect/apply/disconnect release it at `:364,431,512`. This is reachable during server teardown. |
| **P04-03: raw exceptions passed to Serilog** | **Confirmed P1 contract violation.** | Four sites pass exception objects: `RouterBackend.cs:321,326` and `Storage/ConfigStorage.cs:359,375`. This violates Headless's explicit no-raw-exception invariant. |
| **P04-06: custom configuration transactions** | **Confirmed P1; data loss / inconsistent state.** | `CustomConfigFeature.Remove` deletes the JSON file before committing YAML; `Import` writes JSON before committing YAML; compensation restores settings only, not custom-file bytes. |

### P05 — UI consumer synchronization

| Candidate | Reconciled disposition | Source-grounded reasoning |
|---|---|---|
| **P05-01: clean backend exit permanently stops recovery** | **Confirmed P1 availability/status defect.** | `Service.qml:307–335` sets `state = "unavailable"` and retries only for nonzero exit. Backend EOF/cancellation can legitimately return zero. |
| **P05-02: conflict error triggers cascade mutation storm** | **Confirmed P1 recovery-ordering gap.** | `Service.qml:404–405` queues a snapshot, and `sendRequest():510` itself immediately pumps pending mutations before refresh. |

### P06 — Android/cross-platform

| Candidate | Reconciled disposition | Source-grounded reasoning |
|---|---|---|
| **P06-01: self-inclusion inevitably loops all proxy traffic** | **P2 hardening gap; reject unconditional P0.** | Include paths do not filter the app package (`VpnRouterService.java:1548–1557`), but live platform interface enables socket protection and throws if `protect(fd)` fails (`:1695–1706`). |
| **P06-02: subscription refresh failure escapes `async void`** | **Reject the stated failure mechanism; residual handler hardening is P2.** | `OnConnectClicked` is `async void`, but the awaited refresh is caught inside `ApplyScannedSubscriptionUrlAsync` (`AndroidApp.QrScanApply.cs:234–245`). |
| **P06-03: two initialization flags use conflicting Libbox directories** | **Reject conflicting-path claim; P2 concurrency question remains unproven.** | Both setup paths use `filesDir`, `filesDir/data`, `cacheDir`, and `fixAndroidStack=false`. Independent flags permit repeated/concurrent calls, but do not establish corrupting behavior. |
