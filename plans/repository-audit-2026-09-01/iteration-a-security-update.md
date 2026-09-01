# Iteration A — Security/update raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `SU-1` through `SU-3`
Status: unverified swarm output; no item below is accepted until lead source verification. `PN-2-4` is a known duplicate already fixed in green PR #204, with merge still pending.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| SU-1 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 4 | 4 |
| SU-2 | 3/3 | correctness; security/fail-closed/lifetime; tests/schema/upstream | 4 | 3 |
| SU-3 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 11 | 7 |

## Unverified candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Status |
|---|---|---|---|---|
| SU-1-1 | P1 | Failed snapshot restore may leave the application directory absent after moving it to a temporary stage | `VPNRouter.Core/Services/UpdateBackup.cs:198-219` | pending |
| SU-1-2 | P1 | Windows installer self-elevation may interpolate caller-controlled `Version` into an elevated PowerShell command | `packaging/windows/install.ps1:98-132` | pending |
| SU-1-3 | P2 | Legacy `update-helper.cmd` expects a staging layout that no longer includes the per-attempt GUID directory | `update-helper.cmd:10-23`; `UpdateChecker.cs` staging flow | pending |
| SU-1-4 | P2 | Same-version reinstall receipt may be reported as an update that did not take effect | `VPNRouter.Core/Services/UpdateChecker.cs:188-190,1177-1187` | pending |
| SU-2-1 | P1 | Crash scrubber may preserve HTTP basic-auth credentials when a URL has no multi-character path | `VPNRouter.Core/Services/CrashReporter.cs:203-208` | pending |
| SU-2-2 | P1 | `FreeConfigFetcher` buffers an untrusted HTTP response without a byte ceiling | `VPNRouter.Core/Services/FreeConfigs/FreeConfigFetcher.cs:54-63` | pending |
| SU-2-3 | P1 | Deep-verifier failure logs may persist raw sing-box stderr without secret scrubbing | `VPNRouter.Core/Services/VlessDeepVerifier.cs:321-325`; `FreeConfigDeepVerifier.cs:164-167` | pending |
| SU-3-1 | P1 | CLI stop fallback reopens a PID after a wait and may kill it without re-validating ownership | `VPNRouter.CLI/Commands/StopCommand.cs:25-36,45-64` | pending |
| SU-3-2 | P1 | Windows service startup sweep may kill every process named `sing-box` without ownership validation | `VPNRouter.Service/Program.cs:48-65` | pending |
| SU-3-3 | P1 | Linux stop escalation uses privileged `pkill -f sing-box`, matching unrelated command lines | `VPNRouter.Core/Services/SingBoxManager.LinuxStop.cs:48-86,156` | pending |
| SU-3-4 | P1 | Detached Linux relaunch helper uses a predictable path under `/tmp` | `VPNRouter.Core/Services/UpdateChecker.cs:1089-1114` | pending |
| SU-3-5 | P1 | Fail-closed firewall may apply a ruleset without a hostname server IP after DNS resolution failure | `LinuxFirewallManager.cs:254-276`; `MacFirewallManager.cs:391-412` | pending |
| SU-3-6 | P1 | Windows service registration may persist an unquoted executable path | `VPNRouter.Service/ServiceInstaller.cs:47-53` | pending |
| SU-3-7 | P1 | CLI/service logging may use literal `%ProgramData%` paths outside Windows | `VPNRouter.CLI/Program.cs:7`; `VPNRouter.Service/Program.cs:81` | pending; duplicate candidate of CL-3-1/2 |

## Lead status

Pending Iteration B and source verification. Security-looking labels are reviewer proposals, not verdicts; exact command construction, guards, platform behavior, and tests must be reopened before any ledger or implementation action.
