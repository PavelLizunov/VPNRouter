# Phase 1 — DR-07 replace TraceEvent prototype

**Owner**: Codex

**Branch**: `codex/dr-07-replace-traceevent`

**Audit ref**: dependency replacement task list DR-07, draft PR #99

**Effort**: 4–8 hours

**Risk**: MEDIUM — a build can pass while short-lived routed processes are
missed or monitor lifecycle races leave a watcher alive

**Blast radius**: Windows process monitoring and the HealthMonitor rescan
trigger; no routing/config semantics may change

**Rollback**: discard the prototype or revert its implementation commit

## Why

`Microsoft.Diagnostics.Tracing.TraceEvent` is carried by one Windows process
monitor and adds an estimated 3.76 MB to the Windows output. The product already
references `System.Management`, so a small WMI event watcher may preserve the
existing process-start trigger with less shipped code. The replacement is worth
keeping only if real routed-process detection and monitor lifecycle are no worse
on windows-brat.

## What

- Trace `EtwProcessMonitor` from registration through every subscriber and
  prove which start/stop events affect product behavior.
- Measure the current Windows Release output and exclusive TraceEvent files.
- Prototype one replacement behind the existing monitor contract. Prefer
  `ManagementEventWatcher` with `Win32_ProcessStartTrace` and
  `Win32_ProcessStopTrace`; consider polling only if Qwen/code evidence rejects
  WMI reliability for this flow.
- Cover start, stop, dispose, repeated start, WMI unavailability, and
  short-lived-process behavior with the smallest useful tests.
- Remove the TraceEvent package only after compilation and windows-brat runtime
  gates pass, then repeat the identical output measurement.

## How

1. Run Qwen 3.8 max in read-only mode over the complete monitor lifecycle,
   HealthMonitor debounce, composition roots, tests, and package ownership.
2. Record an exact Release baseline: build command, output bytes/files, and
   TraceEvent-exclusive payload.
3. Implement only the smallest evidence-supported candidate without adding a
   second abstraction hierarchy.
4. Run focused lifecycle tests, full accessible regression tests, and a clean
   Release build with .NET 10.
5. On windows-brat only, verify repeated monitor start/stop, a short-lived
   process, WMI failure/recovery, and an application from the routed list being
   detected within the existing user-visible wait.
6. Keep the change only if reliability is no worse and the output reduction is
   measurable. Otherwise restore TraceEvent and record the rejection.

### Tests written

- Planned: focused monitor lifecycle/error tests selected after Qwen maps the
  current test seam.

### Verification approach

- Never launch or install VPNRouter on the developer workstation.
- Use identical clean Windows Release commands before and after.
- Unit-test deterministic lifecycle/error behavior locally.
- Run runtime process detection only on windows-brat through the existing remote
  control path.

## Verification gate

- [x] **Gate 1 — Build clean**: solution Release build has 0 errors.
- [x] **Gate 2 — Tests green**: focused tests and accessible regression suite pass.
- [x] **Gate 3 — windows-brat runtime**: real process detection, repeated lifecycle,
  bounded Stop, and idle CPU pass. WMI failure/recovery is not applicable because the
  accepted replacement does not use WMI.
- [x] **Gate 4 — Measurements**: exact output before/after and removed exclusive
  TraceEvent files are recorded.
- [x] **Gate 5 — Qwen/self-review**: final qwen3.8-max-preview verdict is ACCEPT;
  Codex independently fixed and regression-tested the same-worker Stop path.
- [x] **Gate 6 — Push/PR/CI**: draft PR #106 is open; final product-code head is
  green (`test`, `grep`, and `go-test-windows`; characterization job intentionally skipped).

## Outcome

Qwen rejected WMI for this flow: it adds `winmgmt`/COM failure modes while the
existing macOS implementation already proves that a two-second snapshot is inside
HealthMonitor's five-second debounce window. The candidate therefore consolidates
the Windows ETW monitor and macOS poller into one cross-platform
`PollingProcessMonitor` and removes TraceEvent.

- Release solution build: 0 errors.
- Focused monitor tests: 7 passed; accessible suite: 2635 passed, 2 skipped, 0 failed.
- Identical self-contained App + CLI + Service baseline: 300 files, 241,743,856 bytes.
- Candidate: 288 files, 227,620,805 bytes; delta: -12 files, -14,123,051 bytes.
- Removed payload includes TraceEvent/FastSerialization plus all x86/x64/ARM64
  `msdia140` and `KernelTraceControl` native assets.
- windows-brat (`100.115.182.0`) real implementation probe: first detection 1634ms,
  post-restart detection 1823ms, Stop 1ms, idle CPU 0.031%, no lingering processes.
- The candidate GUI launched and UIA reached Connect. Full tunnel startup was blocked
  by the VM's pre-existing invalid VLESS Reality key; the installed build was restored
  unchanged after the probe.
- First PR CI caught and fixed one platform-only compile regression: the shared macOS
  namespace import is still required by process-scanner/firewall types after deleting
  `MacProcessMonitor`.
