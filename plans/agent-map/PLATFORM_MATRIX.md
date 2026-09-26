# Platform Capability & Implementation Matrix

Authoritative matrix comparing implemented features vs. dynamically verified runtime support across all five operational targets.

| Subsystem / Feature | Windows Desktop (`VPNRouter.App`) | Linux Desktop / CLI (`App` / `CLI`) | macOS Desktop (`App`) | Android (`VPNRouter.Android`) | Linux Headless (`Omarchy`) |
|---|---|---|---|---|---|
| **Core VPN Engine** | Complete (`sing-box` child proc) | Complete (`sing-box` child proc) | Complete (`sing-box` child proc) | Complete (`Libbox.aar` in-proc JNI) | Complete (`RouterSession` wrapper) |
| **TUN Device Management** | Wintun / WinTUN adapter (`netsh` / driver) | Linux TUN (`/dev/net/tun`, `ip tuntap`) | macOS utun (`scutil` / `networksetup`) | Android `VpnService.Builder` | Linux TUN via sing-box (requires cap/root) |
| **Cross-Process Mutual Exclusion** | Global Named Semaphore `Global\VPNRouter-SingBox-Owner` | Advisory kernel lock `flock(2)` on `/run/user/<uid>/vpnrouter-tun.lock` | POSIX advisory lock | OS Service singleton (`VpnService`) | Advisory kernel lock `flock(2)` |
| **KillSwitch & Firewall** | Windows Filtering Platform (WFP) + `netsh advfirewall` | `nftables` via `LinuxFirewallManager` (`sudo -n nft -f`) | `pf` via `pfctl` anchor rules | `VpnService.Builder.setBlocking` / OS KillSwitch | **Unavailable** (DefaultProduction fail-closed, no NOPASSWD) |
| **DNS Leak Lockdown** | `WindowsDnsHardening` (`netsh` port 53 WFP block) | `systemd-resolved` + `resolvconf` drop | `scutil` DNS override | Android private DNS bypass (`protect(fd)`) | **Unavailable** (Honest capability refusal) |
| **Split Tunneling (App Routing)** | WFP Kernel Driver (`SplitTunnelDriverInterop`) + ETW | cgroups v2 / net_cls (Partial/Roadmap) | Process-based routing (Partial) | Android per-app filter (`addAllowed/DisallowedApplication`) | Headless AppRouting feature (Config generator rules) |
| **Config Generation & Outbounds** | `ConfigPipeline` (VLESS, SS, Trojan, Hysteria2, AWG) | Same (`ConfigPipeline`) | Same (`ConfigPipeline`) | Source-linked Core `ConfigGenerator` | Same (`ConfigPipeline`) |
| **Free Configs Pool & Deep Verify** | Complete (`FreeConfigFetcher`, TCP/TLS Probes) | Complete (`FreeConfigFetcher`) | Complete (`FreeConfigFetcher`) | Complete (`AndroidFreeConfigDeepVerifier`) | Complete (`FreeConfigFeature`, TCP probes) |
| **Subscription Management** | Complete (Base64, SIP002, Clash, V2Ray) | Complete | Complete | Complete | Complete (`SubscriptionFeature`) |
| **Supporting Services (Zapret)** | Complete (`winws.exe` / `service`) | Complete (`nfqws`) | N/A | Android port (`zapret2-android`) | Headless config generation only |
| **Supporting Services (TgProxy)** | Complete (`mtproto-proxy.exe`) | Complete (Linux binary) | Complete (macOS binary) | N/A | Headless config generation only |
| **Supporting Services (Slipstream)** | Complete (`slipstream-client.exe`) | Complete (Linux binary) | Complete (macOS binary) | Complete (`SlipstreamNative.java`) | Headless config generation only |
| **UI Shell** | Avalonia UI XAML (FluentTheme) | Avalonia UI XAML (FluentTheme) | Avalonia UI XAML (FluentTheme) | Avalonia UI Mobile XAML + Android Views | Native Omarchy Quattro Quickshell / QML |
| **IPC & Management Interface** | Named Pipes / SCM (`VPNRouter.Service`) | CLI Stdio / Unix Domain Socket | CLI Stdio | Android Intent Broadcast Receiver | Standard I/O NDJSON Protocol v1 (`ProtocolServer`) |
