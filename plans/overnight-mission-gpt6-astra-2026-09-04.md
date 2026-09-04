# Autonomous Overnight Mission — Deep Audit & Verification

**Target Model**: Frontier Reasoning Model (e.g., GPT-6 Astra)
**Project**: VPNRouter (Virtual Penguin Network)
**Context**: .NET 10 cross-platform split-tunnel VPN router (Windows, macOS, Linux, Android) with Avalonia UI and sing-box core.
**Date**: 2026-09-04
**Reference Contract**: `docs/agent-contract.md`

---

## Mission Directive

You are tasked with an autonomous, comprehensive overnight deep audit of the VPNRouter codebase. 

Your objective is not a shallow linter run, but an autonomous exploration:
1. Ingest and internalize all architectural documentation and subsystem invariants.
2. Autonomously analyze the threat model, concurrency surface, and user experience.
3. Formulate targeted hypotheses on where the most subtle bugs, performance bottlenecks, and UX inconsistencies reside.
4. Dive deep into the source code to prove or disprove your hypotheses.
5. Produce a prioritized, actionable morning report containing concrete defects (P0/P1/P2) with reproduction paths and proposed fixes.

---

## Phase 1: Mandatory Reading & Invariant Ingestion

Before analyzing source code, thoroughly read the following documentation files in order:

1. **Foundations & Canonical Rules**:
   - `docs/agent-contract.md`: The supreme project contract. Note Rule 6 (zero emoji), Rule 7 (case-sensitive process names), and safety/Git boundaries.
   - `plans/OPEN-DEFECTS.md`: Critical defect ledger. Study the resolved P0/P1 issues to understand past failure modes (PID spoofing, SCM quoting, Linux polkit LPE, Wintun GUID mismatch, Avalonia TabControl carousel bug, DNS leaks).
2. **Subsystem Architecture & Invariant Maps**:
   - `VPNRouter.Core/AGENTS.md` and `VPNRouter.Core/Services/AGENTS.md`: 123 services mapped into 12 functional subsystems.
   - `VPNRouter.Core/Platform/AGENTS.md`: Cross-platform adapter rules, DNS hardening, and the Cross-Platform Firewall Kill-Switch Matrix.
   - `VPNRouter.App/AGENTS.md` and `VPNRouter.App/ViewModels/AGENTS.md`: 14 partials of `MainWindowViewModel` and the Connection State Machine.
   - `VPNRouter.CLI/AGENTS.md` and `VPNRouter.Service/AGENTS.md`: CLI and Windows Service contracts.
   - `docs/AGENTS.md`: Index of documentation and test topology.

---

## Phase 2: Autonomous Triage & Multi-Vector Exploration

Once familiar with the system invariants, formulate your investigation plan across three core vectors. Prioritize areas with high concurrency, privilege boundaries, or complex cross-platform state transitions:

### Vector A: Correctness, Security & Race Conditions (Hardcore System Bugs)
- **State Machine Concurrency**: Inspect `MainWindowViewModel.Connection.cs`, `VpnEngine.cs`, `SingBoxManager.Lifecycle.cs`, and `ResilientStarter.cs`. 
  - Look for race conditions during rapid user clicks, sleep/wake power transitions (`PowerEventListener.cs`), or network interface flapping (`NetworkInterfaceDetector.cs`).
  - Verify that `CancellationTokenSource` instances are properly linked, cancelled, and disposed without leaving zombie background tasks or orphaned sing-box instances.
- **Privilege Boundaries & IPC**: Inspect `VPNRouter.Service`, `ProcessOwnership.cs`, `UnixOwnedProcessSignal.cs`, and installer scripts (`packaging/`).
  - Can an unprivileged local process hijack named pipes, signal the service, or abuse SCM/polkit/launchd to execute unauthorized commands or escalate privileges?
- **Network Invariants & Leak Proofing**: Inspect `ConfigGenerator.Dns.cs`, `ConfigGenerator.Route.cs`, `CustomConfigInjector.cs`, and `LeakProtection.cs`.
  - Are there edge cases where IPv6 leaks outside the tunnel on dual-stack systems?
  - Can browser-initiated DoH / DoT bypass sing-box detour routing?
  - Are proxy server endpoints guaranteed to bypass the TUN interface to prevent recursive routing loops?

### Vector B: Performance, Memory & Resource Lifecycles
- **Process & Socket Descriptors**: Inspect process queries (`ProcessQuery.cs`, `ProcessScanner.cs`) and HTTP clients (`PolicyHttpClient.cs`).
  - Ensure all native `Process` handles and `SafeHandle` instances are strictly closed under all exception paths.
  - Check for socket exhaustion or connection pooling issues during continuous health probes (`ServerHealthProbe.cs`, `HealthMonitor.cs`).
- **Memory & Allocation Hotspots**:
  - Check string allocations, JSON serialization paths (`System.Text.Json` source generator usage in `AppJsonContext.cs`), and collection rebuilding in ViewModels.
  - Verify that long-running 24/7 background timers and event subscriptions (`ClashLogStream.cs`, `EtwProcessMonitor.cs`) do not retain dead object references.

### Vector C: UI/UX Consistency, Layout & State Synchronization
- **State Synchronization (Silent Failures)**:
  - Verify that the UI status card never presents a misleading "Protected" / "Connected" state when the underlying data plane is degraded or dead (see AutoFailover alert logic).
- **Responsive Layout & Avalonia Gotchas**: Inspect `VPNRouter.App/Views/` and `Views/Pages/`.
  - Check for bare `CheckBox.Content` strings (must use wrapped `TextBlock`).
  - Check for `<TabControl>` inside unconstrained scroll views.
  - Check for layout clipping or overflow when the window is resized down to 360px width.
- **Bilingual UI & Formatting**:
  - Verify that no raw hardcoded strings exist in XAML or ViewModels; all labels must route through `Strings.*`.
  - Check `NumericUpDown` bindings for decimal vs int cast issues.

---

## Phase 3: Deliverable — The Morning Brief

Synthesize your findings into a structured report with the following format:

### 1. Executive Summary & Chosen Focus
- Brief assessment of the system health.
- Which vectors you prioritized and why.

### 2. Prioritized Defect Ledger
Categorize each finding using the project standard:
- **P0**: Critical security vulnerability, system crash/brick, or complete network cutoff.
- **P1**: Silent leak, deadlock, state desync, or broken core feature.
- **P2**: Performance bottleneck, resource leak, or narrow UI breakage.
- **P3**: Code elegance, redundant allocations, or minor UX polish.

For every finding, provide:
- **Identifier & Title**: e.g., `DEFECT-01: Race condition between Sleep listener and TunOwnershipLock on Wake`.
- **Severity**: P0 / P1 / P2 / P3.
- **Location**: Exact file path and line numbers / method name.
- **Root Cause Analysis**: Logical or mathematical proof of how the failure occurs.
- **Reproduction Scenario**: Concrete sequence of events that triggers the defect.
- **Recommended Solution**: Specific code patch or architectural adjustment.

### 3. Immediate Action Plan
- Top 3 recommendations for the engineering team to review first thing in the morning.
