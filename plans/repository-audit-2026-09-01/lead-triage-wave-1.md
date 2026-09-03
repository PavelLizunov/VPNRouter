# Lead triage wave 1 — high-risk boundaries

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Date: 2026-09-01
Method: lead reopened each cited source range after both swarm iterations; swarm agreement alone was not accepted.

## Confirmed

| Cluster | Severity | Classification evidence | Next action |
|---|---|---|---|
| PN-2-4 Linux polkit helper | P0 | Base policy used `allow_active=yes`; helper accepted arbitrary `SRC`, recursively copied it as root and applied capabilities. | Fixed in exact-head-green PR #204; merge pending. |
| SU-1-2 / BR-2-6 Windows installer trust boundary | P1 security | `install.ps1:97-132` joins unvalidated `Version` into executable PowerShell source at line 124. `install.ps1:193-208` verifies only when a sidecar exists and otherwise continues. | Immediate dedicated task/PR; add injection and missing-sidecar contracts. |
| SU-1-1 snapshot restore compensation | P1 recovery | `UpdateBackup.cs:207-214` moves `app` aside, then moves `bak` to `app`; `catch` at 227-230 returns without moving `stage` back. | Dedicated recovery task with forced-second-move failure characterization. |
| SU-2-2 unbounded free-config response | P1 availability/security | `FreeConfigFetcher.cs:54-64` uses `ResponseContentRead` and `ReadAsStringAsync` with timeout but no byte cap; existing `PolicyHttpClient` demonstrates the repository primitive. | Dedicated boundary task; reuse bounded primitive. |
| SU-2-3 raw verifier stderr | P1 privacy/security | `VlessDeepVerifier.cs:312-325` and `FreeConfigDeepVerifier.cs:145-168` append process stderr and emit it directly through logs/results; `TrimSnippet` only flattens/truncates. | Same boundary task; scrub once before log/result use. |
| SU-3-1 CLI PID reuse | P1 process isolation | `StopCommand.cs:29-35` validates an initial process handle, waits up to five seconds at 94-95, then obtains a fresh handle at 61 and kills without revalidation. | Windows CLI task with deterministic post-wait ownership contract. |
| SU-3-2 service orphan sweep | P1 process isolation | `Service/Program.cs:42-65` protects active VPNRouter TUN owners but otherwise kills all processes named `sing-box`; third-party instances have no VPNRouter lock. | Windows service task; require VPNRouter ownership proof before kill. |
| SU-3-3 Unix pattern kill | P1 process isolation | `LinuxStop.cs:48,65,86` invokes `pkill -f sing-box` at user, pkexec and sudo privilege levels; post-attempt liveness checks do not constrain which processes receive the signal. | Unix process task; target the owned process PID only. |
| SU-3-6 Windows service command line | P1 privilege boundary | `ServiceInstaller.cs:47-53` gives `sc.exe` a quoted argument delimiter around `exePath --service`, not persisted inner quotes around the executable path. | Windows service task; pin exact SCM `ImagePath`/argument construction. |

## Refuted

| Candidate | Reason |
|---|---|
| SU-2-1 basic-auth URL leak | `CrashReporter` places userinfo in a non-capturing group and emits only scheme + host; `CrashReporterScrubberTests:70-78` pins no-path basic auth. |
| PN-4-2 semaphore over-release | In both cited Android loops, `WaitAsync(ct)` occurs before entry into the `try/finally`; cancellation while waiting does not run `Release`. |
| CL-1-7 DataContext subscription leak | The cited code assigns `DataContext` once; no `DataContextChanged` subscription exists. |
| CL-1-9 decimal-to-int binding cast | The ViewModel property and `NumericUpDown` binding use the existing nullable integer-compatible contract. |
| CL-2-13 narrow CheckBox overflow | Cited controls already wrap content in `TextBlock TextWrapping="Wrap"`. |
| CL-2-14 TabControl height bug | Cited locations do not contain the claimed `TabControl`. |
| CL-2-17 parent DataContext cast | Actual XAML uses an ancestor binding rather than the claimed direct cast. |
| CL-2-18 direct color brush violation | Cited brushes resolve semantic token resources. |
| CL-2-20 unhandled URL launch | `AboutWindow.axaml.cs:45-57` wraps `Process.Start` in `try/catch`. |
| QA-2 generic god-file splits | Iteration B found no behavior-preserving minimal seam for most rows; size alone does not satisfy the split gate. |

## Measurement-gated / lower priority

| Candidate | Classification |
|---|---|
| SU-3-4 predictable Linux relaunch path | P2 confirmed race surface: path is predictable and mode is applied after creation, but the helper is used for a user-writable install without privilege escalation. Fix with native secure temp creation when the updater cluster reaches it. |
| SU-3-5 empty hostname allowlist in full-tunnel firewall | Measurement/design-gated: the current kill switch intentionally fails closed; determine whether reconnect must retain the previous resolved server set before changing policy. |
| CL-2-11 synchronous AboutWindow streams | Source shape can deadlock for high-volume dual streams, but the command is `sing-box version`; characterize actual bounded output before changing. |
| CL-2-15 runtime XAML load exception | Framework/packaging measurement-gated; no current missing-resource reproduction. |
| CL-2-19 localized clipping | Requires canonical 360px rendered measurement. |

## Durable ledger

All confirmed P0/P1 clusters above were recorded in `plans/OPEN-DEFECTS.md` before implementation. Remaining categories continue through later lead waves; no majority vote is treated as a verdict.
