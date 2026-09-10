# VPNRouter.Core.Platform Sub-Zone Instructions

Applies to `VPNRouter.Core/Platform/` and descendants. Follow the
[Core instructions](../AGENTS.md) and [canonical contract](../../docs/agent-contract.md).

## Platform factories

[PlatformServices.cs](PlatformServices.cs) selects process scanners, monitors,
firewall factories and DNS adapters, and wires `CreateVpnEngine`. Read its
compile-time and runtime OS branches before changing platform behavior.

## Firewall boundaries

[IFirewallManager](../Interfaces/IFirewallManager.cs) separates rule creation,
enabling, disabling and deletion. In
[StartupPipeline.cs](../Services/StartupPipeline.cs), firewall setup calls
`CreateBlockRules` only when the profile enables `BlockOnVpnFail`, passing
`isFullTunnel` from `settings.App.RoutingMode == "full"` (case-insensitive).
Creation/arming is not evidence that blocking was successfully enabled.

| Owner | Mode and implementation notes |
|---|---|
| [Windows FirewallManager](../Services/FirewallManager.cs) | Per-executable `netsh` rules; ignores `isFullTunnel`. Unresolved paths or command failures can prevent rule creation. This is not a global full-host block guarantee. |
| [LinuxFirewallManager](Linux/LinuxFirewallManager.cs) | Global nftables block, armed only when explicit `isFullTunnel` is true. Uses table `inet vpnrouter_ks`; loading requires the noninteractive `sudo -n nft` path. |
| [MacFirewallManager](macOS/MacFirewallManager.cs) | Global pf block, armed only when explicit `isFullTunnel` is true. Uses `sudo -n pfctl`; prefers an anchor carrier but includes a legacy broad-ruleset fallback. |
| [NullFirewallManager](macOS/NullFirewallManager.cs) | No-op adapter selected by the factory for other targets; do not infer Android OS VPN policy from this adapter. |

Linux/macOS do not infer full-tunnel mode from an empty process list. With
`isFullTunnel == false`, both remain disarmed even when a scan returns no names.
Their enable paths log write/load failures and may leave traffic unblocked.

Inspect `ReadServerIps` and `BuildRuleset` (Linux) / `BuildRules` (macOS) for
loopback, LAN and server exceptions. Inspect cleanup branches separately:
Linux attempts table deletion; macOS flushes its anchor in anchor mode but
restores the default ruleset after a legacy broad load. Do not promise
anchor-only mutation or successful cleanup regardless of command failures.

## Other adapters

- `Linux/LinuxDnsHardening.cs` and `macOS/MacDnsHardening.cs`: Unix DNS
  configuration and restoration. `PlatformServices.CreateUnixDnsHardening`
  selects them; `VpnEngine` applies the adapter under `DnsLeakLockdown` with
  nonfatal error handling. See `Services/WindowsDnsHardening.cs` for Windows.
- `macOS/MacProcessScanner.cs`, `macOS/MacProcessMonitor.cs` and
  `Unix/PsProcessLineParser.cs`: Unix process discovery and parsing. Preserve
  process-name casing as required by the canonical contract.
- `AutostartHelper.cs`: platform autostart setup.
- `Android/AndroidSingBoxRuntime.cs`: Android runtime adapter; use the Android
  zone instructions for the app's `VpnService` and OS VPN policy.
