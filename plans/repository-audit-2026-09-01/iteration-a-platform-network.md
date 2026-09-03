# Iteration A — Platform/network raw candidate index

Base: `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Category coverage in this file: `PN-1` through `PN-4`
Status: unverified swarm output except PN-2-4, which received immediate lead source verification because it was reported as P0.

## Coverage receipts

| Leaf | Reviews | Lenses | Raw findings | Synthesized candidates |
|---|---:|---|---:|---:|
| PN-1 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 4 | 3 |
| PN-2 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 7 | 6 |
| PN-3 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 10 | 6 |
| PN-4 | 3/3 | correctness; security/fail-closed/lifetime; tests/platform/upstream | 8 | 7 |

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
| PN-3-1 | P1 | Startup process handler appends `.exe` on macOS and may miss routed processes | `StartupPipeline.cs:1370`; `MacProcessMonitor.cs:126` | pending |
| PN-3-2 | P1 | macOS DNS lockdown may inspect the post-TUN default route and fail to find a physical service | `VpnEngine.cs:333,411`; `MacDnsHardening.cs:44-56` | pending |
| PN-3-3 | P1 | macOS firewall may cache server IPs before the active config is generated | `StartupPipeline.cs:1095,1130`; `MacFirewallManager.cs:117,137` | pending |
| PN-3-4 | P1 | macOS firewall hostname resolution may discard IPv6 addresses | `MacFirewallManager.cs:336,343,402` | pending |
| PN-3-5 | P1 | Mac process scanner may retain profile casing instead of filesystem process casing | `MacProcessScanner.cs:33,44,58` | pending |
| PN-3-6 | P1 | macOS sing-box sudo spawn may omit non-interactive `-n` | `SingBoxManager.Lifecycle.cs:716`; `MacFirewallManager.cs:152` | pending |
| PN-4-1 | P1 | Android sideload source may not match the canonical `android-arm64.apk` asset | `SideloadSource.cs:243`; `build-android.yml:347` | pending |
| PN-4-2 | P1 | Android batch-test cancellation may release an unacquired semaphore | `AndroidApp.ServerList.cs:1363`; `AndroidApp.SubscribePage.cs:746` | pending |
| PN-4-3 | P1 | Android active-server resolution may ignore manual ConfigMode | `AndroidStorage.cs:592-671`; `MainActivity.cs:1033-1083` | pending |
| PN-4-4 | P1 | Android system-resolver discovery may select the active VPN network | `VpnRouterService.java:996-1024,1477-1482` | pending |
| PN-4-5 | P2 | Android interface callback flag may be accessed across threads without synchronization | `VpnRouterService.java:1616,1932,1970,1992` | pending |
| PN-4-6 | P1 | Failed Android tunnel startup may leak partial Slipstream/PFD resources | `VpnRouterService.java:655-688` | pending |
| PN-4-7 | P1 | Include-mode per-app filter may capture VPNRouter's own package and loop egress | `VpnRouterService.java:1496-1504` | pending |

## Immediate lead verification for PN-2-4

- The installed policy sets `<allow_active>yes</allow_active>`, which polkit defines as implicit authorization for an active local session; no administrator authentication is required.
- `org.freedesktop.policykit.exec.path` limits authorization to the helper binary but does not constrain its arguments.
- The helper accepts caller-supplied `SRC`, checks only that it is a directory containing a file named `VPNRouter.App` or `VPNRouter.App.dll`, then runs `cp -rfT "$SRC" "$DST"` as root.
- `DST` is restricted to VPNRouter installation roots, but the caller still controls every copied payload and can supply a `sing-box` file that receives `cap_net_admin,cap_net_bind_service`.
- Current application-side checksum verification does not protect direct invocation of the installed passwordless helper.
- Primary source: https://polkit.pages.freedesktop.org/polkit/polkit.8.html (`allow_active=yes` means authorized).

PN-2-4 was implemented in dedicated PR #204; exact head `b665a66b3b5ba18ad3c3b301cb74842a8f39cccd` passed `test`, `test-update`, `grep`, Windows Go, and characterization checks. Merge remains an owner decision. All other candidates remain unverified pending Iteration B and lead tracing.
