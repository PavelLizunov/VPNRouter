# Iteration A — Platform/network raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `PN-1`, `PN-2`
Status: unverified swarm output except PN-2-4, which received immediate lead source verification because it was reported as P0.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| PN-1 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 4 | 3 |
| PN-2 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 7 | 6 |
| PN-3 | 0/3 | pending | 0 | 0 |
| PN-4 | 0/3 | pending | 0 | 0 |

## Candidates

| ID | Proposed severity | Candidate | Primary cited evidence | Lead status |
|---|---|---|---|---|
| PN-1-1 | P1 | ETW readiness event is not reset across monitor restarts | `EtwProcessMonitor.cs:38,51-94,126,169` | pending |
| PN-1-2 | P1 | Process-start handler may append a second `.exe` suffix | `StartupPipeline.cs:1370`; `EtwProcessMonitor.cs:135,193` | pending |
| PN-1-3 | P2 | Child-process resolution may compare an unnormalized rule name | `ProcessScanner.cs:54,106-109` | pending |
| PN-2-1 | P2 | Linux firewall hostname resolution may discard IPv6 server addresses | `LinuxFirewallManager.cs:206-212,264-269` | pending |
| PN-2-2 | P1 | Predictable privileged nftables ruleset path may permit symlink/TOCTOU attacks | `LinuxFirewallManager.cs:126-135` | pending |
| PN-2-3 | P2 | Root `pkill -f sing-box` may kill unrelated sing-box processes | `packaging/linux/vpnrouter-update-helper:45`; `SingBoxManager.LinuxStop.cs:65,86` | pending |
| PN-2-4 | P0 | Passwordless polkit helper accepts an arbitrary user-controlled source directory and copies it into a root-owned installation, then grants network capabilities to its `sing-box` | `com.vpnrouter.update.policy:27-33`; `vpnrouter-update-helper:16-17,24-49,62-63`; `UpdateChecker.cs:997-1048` | **confirmed; stop-line fix task required** |
| PN-2-5 | P1 | DNS hardening may apply TUN DNS settings to a physical interface | `LinuxDnsHardening.cs:80-102,226-235` | pending |
| PN-2-6 | P2 | Linux relaunch helper uses a predictable `/tmp` path | `UpdateChecker.cs:1089-1127` | pending |

## Immediate lead verification for PN-2-4

- The installed policy sets `<allow_active>yes</allow_active>`, which polkit defines as implicit authorization for an active local session; no administrator authentication is required.
- `org.freedesktop.policykit.exec.path` limits authorization to the helper binary but does not constrain its arguments.
- The helper accepts caller-supplied `SRC`, checks only that it is a directory containing a file named `VPNRouter.App` or `VPNRouter.App.dll`, then runs `cp -rfT "$SRC" "$DST"` as root.
- `DST` is restricted to VPNRouter installation roots, but the caller still controls every copied payload and can supply a `sing-box` file that receives `cap_net_admin,cap_net_bind_service`.
- Current application-side checksum verification does not protect direct invocation of the installed passwordless helper.
- Primary source: https://polkit.pages.freedesktop.org/polkit/polkit.8.html (`allow_active=yes` means authorized).

The exact severity and remediation design will be pinned in a dedicated security brief before code changes. PN-3 and PN-4 remain pending in Iteration A.
