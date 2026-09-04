# VPNRouter.Core.Platform Sub-Zone Instructions

This document governs the `VPNRouter.Core/Platform/` directory, which houses OS-specific abstractions, platform factories, DNS hardening, process monitors, and firewall kill-switch implementations.

## Directory Structure

- `PlatformServices.cs`: Central factory for platform implementations. Contains conditional compilation boundaries (`#if PLATFORM_WINDOWS`) and the sole blessed factory method `PlatformServices.CreateVpnEngine`.
- `AutostartHelper.cs`: Unified autostart manager across Windows (Registry Run key), macOS (`~/Library/LaunchAgents/com.vpnrouter.app.plist`), and Linux (`~/.config/autostart/vpnrouter.desktop`).
- `Linux/`:
  - `LinuxFirewallManager.cs`: Linux `nftables` kill-switch (`inet vpnrouter_ks` table).
  - `LinuxDnsHardening.cs`: Linux DNS hardening via `systemd-resolved` (`resolvectl`).
- `macOS/`:
  - `MacFirewallManager.cs`: macOS `pf` packet filter kill-switch (`com.vpnrouter/killswitch` anchor).
  - `MacDnsHardening.cs`: macOS DNS pinning via `scutil` / `networksetup`.
  - `MacProcessScanner.cs`: Unix process discovery via `ps` command output.
  - `MacProcessMonitor.cs`: Periodic process monitor for non-Windows platforms.
  - `NullFirewallManager.cs`: No-op fallback firewall manager.
- `Unix/`:
  - `MacDnsParsers.cs`: Output parser for macOS `scutil` DNS configurations.
  - `PsProcessLineParser.cs`: Standardized Unix `ps -eo pid,ppid,comm,args` text line parser.
- `Android/`:
  - `AndroidSingBoxRuntime.cs`: Android-specific runtime helpers and filesystem paths.

---

## Cross-Platform Firewall & Kill-Switch Matrix

The kill-switch (`block_on_vpn_fail`) behavior differs fundamentally across platforms due to OS packet filtering architectures:

| Platform | Underlying Mechanism | Supported Tunnel Modes | Elevation Prerequisite | Fail-Safe Behavior |
|---|---|---|---|---|
| **Windows** | WFP / `netsh advfirewall` + `SplitTunnelDriver` | Full-tunnel AND Split-tunnel (per-process rules) | Administrator / `LocalSystem` | Blocks routed apps; fails closed. |
| **Linux** | `nftables` (`inet vpnrouter_ks`) | Full-tunnel ONLY | `sudo -n nft` (NOPASSWD sudoers grant) | Missing grant logs warning and stays unblocked (no brick); split-tunnel is an explicit no-op. |
| **macOS** | `pf` (`com.vpnrouter/killswitch` anchor) | Full-tunnel ONLY | `sudo -n pfctl` (NOPASSWD sudoers grant) | Flushes anchor on disable/dispose; split-tunnel is an explicit no-op. |
| **Android** | `VpnService` native bypass/block | Controlled by OS VPN settings (`Always-on VPN` / `Block connections without VPN`) | User VPN consent dialog | Managed entirely by Android OS; app configures allowed/disallowed apps. |

### Critical Kill-Switch Invariants

1. **Non-Windows Split-Tunnel Is Disarmed**: Both `LinuxFirewallManager` and `MacFirewallManager` filter network packets by IP, port, and interface, not by process executable image. Therefore, they arm global blocking **only when the process list is empty** (which indicates full-tunnel mode). If a split-tunnel process list is passed, the kill-switch remains disarmed. Never describe an unarmed Unix split-tunnel firewall as fail-closed.
2. **Server IP Pass-Through (Anti-Brick Guard)**: In full-tunnel kill-switch mode on Linux and macOS, the ruleset MUST explicitly allow loopback, local LAN (RFC1918/link-local), and the active VPN server IP(s) read from `current.json`. Without the server pass-through rule, `sing-box` cannot reach the server to reconnect during an outage, and the host would remain permanently disconnected.
3. **Dedicated Anchor and Table Isolation**:
   - On macOS, rules reside inside the anchor `com.vpnrouter/killswitch` rather than replacing the system ruleset. Disabling flushes only that anchor.
   - On Linux, rules reside in a standalone table `inet vpnrouter_ks`. Disabling deletes that table completely, leaving zero residual state in the user's main `nftables` tables.

---

## DNS Hardening & Leak Protection Mechanisms

| Platform | Service Implementation | Actions Taken | Failure Strategy |
|---|---|---|---|
| **Windows** | `WindowsDnsHardening.cs` | Binds NRPT rules to TUN gateway, adjusts interface metric to 1, suppresses multi-homed resolution. | Best-effort with logging; rolls back metrics on disconnect. |
| **Linux** | `LinuxDnsHardening.cs` | Calls `resolvectl dns <tun> <dns_ip>` and `resolvectl domain <tun> ~.` to make TUN the default routing domain. | Best-effort; fails open if `systemd-resolved` or `resolvectl` is not present. |
| **macOS** | `MacDnsHardening.cs` | Modifies dynamic store via `scutil` to assign TUN DNS to primary network service keys. | Best-effort; restores previous DNS servers from state snapshot on teardown. |

---

## Process Discovery & Monitoring

- **Windows**: Uses Windows ETW (Event Tracing for Windows) through `EtwProcessMonitor` for zero-polling, real-time process creation and exit notifications. Fallback scans use `ProcessScanner` with `QueryFullProcessImageName`.
- **macOS and Linux**: Polling via `MacProcessScanner` and `MacProcessMonitor` executing `ps -eo pid,ppid,comm,args` and parsed through `PsProcessLineParser`.
- **Preserve Case Invariant**: All process scanners must preserve filesystem casing (`Discord.exe`) so sing-box Go string maps resolve accurately.
