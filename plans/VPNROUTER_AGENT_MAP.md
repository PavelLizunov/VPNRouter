# VPNRouter Master Agent-Readable Codebase Map

> **TL;DR:** Authoritative, snapshot-bound, multi-platform architectural map of the `VPNRouter` ecosystem (~208k LOC) and companion `omarchy-vpnrouter` plugin. Synthesized from mechanical Gemini swarm inventories, deep adversarial Claude Opus audits, and architectural arbitration by GPT Astra.

---

## 1. Snapshot Identity & Evidence Status

- **Primary Repository:** `/var/lib/dsh/Project/VPNRouter`
  - **Git Branch:** `dsh/omarchy-plugin-2026-09-17`
  - **Commit Baseline:** `5be8952a` (`feat(packaging): add verified Omarchy component license evidence and catalog`)
  - **Working Tree State:** Dirty (tracked changes in `plans/OPEN-DEFECTS.md`, `plans/phase-omarchy-plugin-2026-09-17.md`; untracked agent-map database in `plans/agent-map/`).
- **Companion Repository:** `/var/lib/dsh/Project/omarchy-vpnrouter`
  - **Git Branch:** `dsh/omarchy-plugin-2026-09-17`
  - **Commit Baseline:** `541b1124`
- **Audit Campaign Receipts:**
  - **Phase 1 (Gemini Swarm Mechanical Inventory):** 32 core modules inventoried line-by-line across 72 accepted batch records in `plans/agent-map/modules/`.
  - **Phase 2 (Opus Swarm Adversarial Analysis):** 6 specialized analytical lanes in `plans/agent-map/reviews/opus/` discovering 96 raw candidate findings.
  - **Phase 3 (Astra Review & Reconciliation):** Authoritative arbitration in `plans/agent-map/reviews/astra/reconciliation_report.md` filtering disproven P0 hypotheses and confirming 11 true P1 architectural defects merged into `plans/OPEN-DEFECTS.md`.

---

## 2. Contract Precedence & Canonical Authorities

1. **`docs/agent-contract.md`**: Canonical project contract for repository safety, Git hygiene, WinRM BRAT verification, and release gates. Takes absolute precedence over local instructions.
2. **`plans/omarchy-protocol-v1.md`**: Authoritative specification for Headless NDJSON framing, RPC schemas, and lifecycle guarantees.
3. **`plans/OPEN-DEFECTS.md`**: Canonical release-gating defect ledger. Any open P0 or P1 blocks stable promotion.

---

## 3. High-Level System Architecture

```
                    ┌─────────────────────────────────────────────────────┐
                    │                   VPNRouter Core                    │
                    │  - VpnEngine & StartupPipeline                      │
                    │  - SingBoxManager (Lifecycle, CrashDetect, Health)  │
                    │  - ConfigPipeline & ConfigGenerator (Dns, Routes)   │
                    │  - FreeConfigFetcher & DeepVerifier                 │
                    │  - TunOwnershipLock / LinuxTunOwnership (flock)     │
                    │  - FirewallManager & LeakProtection                 │
                    └───────────┬──────────────┬──────────────┬───────────┘
                                │              │              │
         ┌──────────────────────┘              │              └──────────────────────┐
         ▼                                     ▼                                     ▼
┌──────────────────┐                 ┌──────────────────┐                 ┌──────────────────┐
│  VPNRouter.App   │                 │VPNRouter.Headless│                 │VPNRouter.Android │
│  (Avalonia UI)   │                 │ (NDJSON Daemon)  │                 │ (VpnService/JNI) │
│ - Windows / Mac  │                 │ - ProtocolServer │                 │ - Libbox JNI     │
│ - Linux Desktop  │                 │ - RouterBackend  │                 │ - AndroidApp     │
└──────────────────┘                 └────────┬─────────┘                 └──────────────────┘
                                              │ (NDJSON stdio)
                                              ▼
                                     ┌──────────────────┐
                                     │ omarchy-vpnrouter│
                                     │ (Quickshell/QML) │
                                     │ - Service.qml    │
                                     │ - Panel/Bar views│
                                     └──────────────────┘
```

---

## 4. Multi-Platform Capability Matrix

See [`plans/agent-map/PLATFORM_MATRIX.md`](agent-map/PLATFORM_MATRIX.md) for full breakdown.

- **Windows**: Wintun adapter, WFP driver (`SplitTunnelDriverManager`), `Global\VPNRouter-SingBox-Owner` semaphore, `netsh advfirewall` DNS lockdown.
- **Linux Desktop / CLI**: Linux TUN (`/dev/net/tun`), `flock` on `/run/user/<uid>/vpnrouter-tun.lock`, `nftables` killswitch (`sudo -n nft`), systemd-resolved integration.
- **macOS**: `utun` interface, `pfctl` anchor rules, `scutil` DNS management.
- **Android**: Android `VpnService`, socket protection (`protect(fd)`), in-process `Libbox.aar` via JNI, per-app package filtering.
- **Linux Headless (Omarchy)**: Non-root user daemon, NDJSON RPC framing over stdio, `flock` process isolation, fail-closed runtime policy (`SingBoxRuntimePolicy.DefaultProduction`).

---

## 5. Agent Task Routing Guide

See [`plans/agent-map/TASK_ROUTING.md`](agent-map/TASK_ROUTING.md) for step-by-step navigational routes.

| Developer / Agent Objective | Primary Code Anchors | Test Verification Fixture |
|---|---|---|
| **Modify DNS routing or failover** | `ConfigGenerator.Dns.cs`, `AppSettings.cs`, `LinuxDnsHardening.cs`, `WindowsDnsHardening.cs` | `ConfigGeneratorStrictDnsOverrideTests.cs`, `DnsLockdownPolicyTests.cs` |
| **Fix process crash / reconnect races** | `VpnEngine.cs`, `SingBoxManager.Lifecycle.cs`, `SingBoxManager.CrashDetect.cs` | `VpnEngineLifecycleTests.cs`, `SingBoxManagerLifecycleStressTests.cs` |
| **Update Headless RPC Protocol v1** | `ProtocolServer.cs`, `ProtocolDispatcher.cs`, `RouterBackend.cs`, `Features/*` | `VPNRouter.Headless.Tests/ProtocolTests.cs` |
| **Debug Omarchy Quickshell UI** | `Service.qml`, `ui/SettingsView.qml`, `ui/ServersView.qml`, `setup` | `omarchy-vpnrouter/tests/test-ui-service.js` |
| **Audit Split Tunneling / ETW** | `SplitTunnelDriverManager.cs`, `SplitTunnelDriverInterop.cs`, `EtwProcessMonitor.cs` | `SplitTunnelManagerTests.cs` |

---

## 6. Critical Invariants & Security Boundaries

1. **Fail-Closed Runtime Policy:** Linux Headless runs strictly under `SingBoxRuntimePolicy.DefaultProduction`. Implicit elevation (pkexec/sudo fallback) or untrusted binaries in writable paths are strictly blocked.
2. **Private File Permissions (`0700` / `0600`):** `AppPaths.cs` enforces `chmod 0700` on data directories and `0600` on private configuration files on POSIX systems.
3. **Zero-Secret-Leak Logging:** Error responses emitted on the wire use `RouterException.GetSafeMessage()`. Raw exceptions and credential URIs are never output to stdout.
4. **Mutual Exclusion via `flock`:** Cross-process mutual exclusion on Linux is enforced via kernel file locks (`flock`) on an owner-private path (`/run/user/<uid>/vpnrouter-tun.lock`).

---

## 7. Confirmed Open Defect Cross-Reference

Full details tracked in [`plans/OPEN-DEFECTS.md`](OPEN-DEFECTS.md):
- **`FAILOVER-WARMUP-RACE` (P1)**: Stale warmup probe outlives failover restart and applies stale DNS lockdown.
- **`WIN-DNS-RESTORE-ORPHAN` (P1)**: `WindowsDnsHardening.Restore()` uses fire-and-forget netsh tasks that orphan on exit.
- **`WIN-DNS-LOCKDOWN-TOCTOU` (P1)**: Unordered netsh enable/disable tasks race with `_lockdownEffective`.
- **`LINUX-NFT-TAILSCALE-LOCKOUT` (P1)**: nftables killswitch omits `100.64.0.0/10` (Tailscale lockout hazard).
- **`WIN-DNS-NETSH-TIMEOUT-DEADLINE` (P1)**: Netsh worker ignores cancellation token, exceeding aggregate deadline.
- **`WIN-BINDIR-ACL-FAIL-OPEN` (P1)**: Swallowed ACL failure in `RestrictWindowsBinDirAcl`.
- **`HEADLESS-GATE-DISPOSE-RACE` (P1)**: `RouterSession.DisposeAsync` disposes `_gate` without acquiring it first.
- **`HEADLESS-SERILOG-RAW-EXCEPTION` (P1)**: Raw exception logging in `ConfigStorage` and `RouterBackend`.
- **`HEADLESS-CUSTOM-CONFIG-TRANSACTION` (P1)**: Custom config JSON file deleted before YAML CAS commit.
- **`OMARCHY-QML-CLEAN-EXIT-STALL` (P1)**: `Service.qml` permanently stops plugin on `exitCode === 0`.
- **`OMARCHY-QML-CONFLICT-CASCADE` (P1)**: Mutation revision conflict triggers cascade failures in QML queue.

---

## 8. Omarchy Plugin Continuation Roadmap

With the codebase map fully established and all blind spots eliminated, the remaining implementation tasks for `VPNRouter.Headless` + `omarchy-vpnrouter` follow a strict 4-stage pipeline:

1. **Stage 1 (Headless Protocol Repairs):**
   - Fix `RouterSession.DisposeAsync` gate disposal race (`HEADLESS-GATE-DISPOSE-RACE`).
   - Fix Serilog raw exception logging in `ConfigStorage.cs` and `RouterBackend.cs` (`HEADLESS-SERILOG-RAW-EXCEPTION`).
   - Implement transactional compensation for custom config import/delete (`HEADLESS-CUSTOM-CONFIG-TRANSACTION`).
2. **Stage 2 (Omarchy Service.qml & QML Queue Repairs):**
   - Fix clean-exit restart stall in `Service.qml` (`OMARCHY-QML-CLEAN-EXIT-STALL`).
   - Fix conflict cascade storm: halt mutation pumping on conflict until snapshot refresh completes (`OMARCHY-QML-CONFLICT-CASCADE`).
   - Fix QML UI binding severance on user input in `SettingsView.qml`.
3. **Stage 3 (Feature Integration & Free Configs):**
   - Wire Free Configs deep verification and server pool selection into `Service.qml` and `ui/FreePoolView.qml`.
   - Wire subscription refresh and error status reporting.
4. **Stage 4 (Packaging, Regression & Worker Verification):**
   - Run isolated unit/integration tests on `omarchy-test` worker.
   - Run `test-packaging.py` and QML test runner (`tests/qml-test-runner.sh`).
