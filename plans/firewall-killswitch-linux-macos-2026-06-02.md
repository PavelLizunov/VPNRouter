# Fail-closed firewall backstop for Linux + macOS — DESIGN BRIEF

**Status:** design only (2026-06-02 overnight). NO code shipped — this is a
HIGH-risk P0 (firewall changes can break a host's connectivity, need root, and
can't be meaningfully tested on the Windows dev VM). Implementation should land
under supervision, tested on real Linux + macOS hosts. This brief is for the
user to review and green-light before any code.

Implements product-gap-audit **#131**.

## Problem

`block_on_vpn_fail` (the kill-switch) is **Windows-only**. On Linux and macOS it
is a silent **no-op**:

- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs` — every method is empty;
  `EnableBlockRules()` only logs *"VPN crashed but block_on_vpn_fail is not
  available on macOS — traffic may leak"*.
- `PlatformServices.CreateFirewallFactory` returns `NullFirewallManager` on any
  non-Windows build.

So a Linux/macOS user who enables the kill-switch gets **no protection**: if
sing-box crashes, routed traffic leaks direct until the health monitor restarts
it. The DNS-leak lockdown (Wave 39) is likewise Windows-only.

## What already exists (good news — the seam is clean)

- **Interface**: `VPNRouter.Core/Interfaces/IFirewallManager.cs`
  ```csharp
  public interface IFirewallManager : IDisposable {
      void CreateBlockRules(IEnumerable<string> processNames);
      void EnableBlockRules();
      void DisableBlockRules();
      void DeleteAllRules();
  }
  ```
  Platform-agnostic; ready for per-platform impls.
- **Factory**: `PlatformServices.CreateFirewallFactory` (`#if PLATFORM_WINDOWS`
  → `FirewallManager`, else `NullFirewallManager`). One switch point.
- **Orchestration is already platform-neutral** — no app-level changes needed:
  - `StartupPipeline.cs:975` — `CreateBlockRules(processNames)` at VPN start
    (creates rules DISABLED) when `profile.BlockOnVpnFail`.
  - `HealthMonitor.cs:373` — `EnableBlockRules()` on sing-box crash (fail-closed).
  - `HealthMonitor.cs:472` — `DisableBlockRules()` on successful restart.
  - `VpnEngine.Stop():390-396` — `DisableBlockRules()` + `DeleteAllRules()` +
    `Dispose()` on shutdown.
- **IProcessRunner** abstraction is already wired into the Windows
  `FirewallManager` for shelling out + test fakes — the Linux/macOS impls reuse it
  to shell `nft` / `pfctl` exactly like the Windows impl shells `netsh`.

So the work is: write two `IFirewallManager` implementations + flip the factory.
No orchestration or settings changes.

## The hard semantic difference: per-app vs global

Windows `netsh advfirewall` can block **by program path** (`program="C:\...\Discord.exe"`),
so the kill-switch blocks *only the routed apps'* egress while everything else
stays online. **Linux nftables and macOS pf cannot match by executable path** —
they match by uid / cgroup / socket / address, not process image. Consequences:

1. **Per-app fail-closed is not directly portable.** Options:
   - **(A) Global egress block (recommended for v1):** on crash, block ALL
     outbound except loopback + the LAN + the VPN server endpoint(s), until the
     tunnel is back. Simpler, robust, genuinely fail-closed. Semantically
     *stricter* than Windows (it also pauses non-routed apps for the brief
     restart window) — acceptable for a kill-switch, and arguably more correct
     (a kill-switch that only half-blocks is a foot-gun).
   - **(B) cgroup/uid-scoped block (v2, complex):** launch routed apps in a
     net_cls cgroup / mark, block that mark. Mirrors Windows per-app semantics
     but requires launching apps under VPNRouter's control — a big architectural
     change. Defer.
   - Recommendation: ship **(A)** with a clear log line + doc that the
     non-Windows kill-switch is whole-host during the failure window.
2. **`CreateBlockRules(processNames)` ignores `processNames` on Linux/macOS**
   (v1 global block). Keep the signature for interface compatibility; document
   that the arg is Windows-only.

## Proposed implementation

### Linux — `LinuxFirewallManager` (nftables)

`VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs`, `[SupportedOSPlatform("linux")]`.

- Own table so we never touch the user's rules: `inet vpnrouter_killswitch`.
- `CreateBlockRules`: create the table + an output chain with `policy accept`
  (disabled state — nothing blocked yet), plus a named set `vpn_allow` holding
  the VPN server IP(s) + loopback + RFC1918 LAN. Idempotent (delete table first).
  ```
  nft add table inet vpnrouter_killswitch
  nft add chain inet vpnrouter_killswitch out { type filter hook output priority 0 \; policy accept \; }
  nft add set inet vpnrouter_killswitch vpn_allow { type ipv4_addr\; flags interval\; }
  nft add element inet vpnrouter_killswitch vpn_allow { 127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, <server-ips> }
  ```
- `EnableBlockRules` (crash → fail closed): add a drop rule for everything not in
  `vpn_allow` and not the TUN device:
  ```
  nft add rule inet vpnrouter_killswitch out oifname != "<tun>" ip daddr != @vpn_allow drop
  ```
  (also an ip6 drop, or a blanket ip6 drop if we don't do v6 — match the existing
  `force_ipv4_only` posture).
- `DisableBlockRules` (restart ok): `nft flush chain inet vpnrouter_killswitch out`
  (rules gone, table stays).
- `DeleteAllRules` (shutdown): `nft delete table inet vpnrouter_killswitch`.
- Needs root. Linux already runs sing-box TUN with root/CAP_NET_ADMIN (the .deb
  postinst sets `setcap`), so the privilege is available; shell via `IProcessRunner`
  → `nft` (fall back to `iptables` only if `nft` absent — most modern distros
  have nft; keep v1 nft-only + log a clear error if missing).

### macOS — `MacFirewallManager` (pf / pfctl)

`VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`, `[SupportedOSPlatform("macos")]`,
replacing the `NullFirewallManager` selection (keep Null as the ultimate fallback).

- Use a dedicated **pf anchor** `vpnrouter.killswitch` so we never rewrite the
  user's `/etc/pf.conf`. Load our anchor ruleset via `pfctl -a vpnrouter.killswitch -f -`.
- `CreateBlockRules`: ensure pf is enabled (`pfctl -E`, refcounted) + load an
  EMPTY anchor (disabled state). Record a table `<vpn_allow>` with loopback/LAN/
  server IPs.
- `EnableBlockRules`: load the anchor with
  `block drop out all` + `pass out on <tun>` + `pass out to <vpn_allow>` +
  `pass out proto udp to any port 443` won't be needed (server IP is enough).
- `DisableBlockRules`: load the empty anchor again (flush).
- `DeleteAllRules`: flush anchor + `pfctl -X <token>` to release our `-E` ref.
- Needs root (sudo) — macOS already elevates for utun; reuse that path.

### Factory wiring

```csharp
public static Func<IFirewallManager> CreateFirewallFactory(ILogger? logger = null)
{
#if PLATFORM_WINDOWS
    return () => new FirewallManager(logger);
#else
    if (OperatingSystem.IsLinux())  return () => new LinuxFirewallManager(logger);
    if (OperatingSystem.IsMacOS())  return () => new MacFirewallManager(logger);
    return () => new NullFirewallManager(logger);
#endif
}
```

## DNS-leak lockdown parity (related, separate sub-task)

Wave 39 DNS lockdown (`FirewallManager.EnableDnsLockdownAsync` + `WindowsDnsHardening`)
is also Windows-only. Linux/macOS equivalent: block outbound 53/853 except on the
TUN/loopback via the same nft table / pf anchor. Recommend a follow-up
`IDnsLockdown` interface mirroring `IWindowsDnsHardening`, done AFTER the
kill-switch lands (don't bundle — two risky firewall changes at once is worse).

## Verification plan (cannot be done on the Windows VM)

- **Unit (here, safe):** `FakeProcessRunner` asserting the exact `nft`/`pfctl`
  command sequences for Create/Enable/Disable/Delete — mirrors
  `FirewallManagerLocalizedNetshTests`. This is the only part testable on Windows.
- **Integration (real hosts, supervised):** on a Linux box and a Mac —
  enable kill-switch, start VPN, `kill -9` sing-box, confirm egress is blocked
  (curl a public IP → must fail), confirm restart restores it, confirm clean
  shutdown removes the table/anchor (no leftover rules after the app exits or
  crashes). The "no leftover rules" case is the dangerous one — a stuck drop
  rule after a crash would brick the user's network. **Must** verify the
  shutdown/cleanup-on-crash path (ProcessExit handler should call
  `DeleteAllRules`, like Windows).

## Risk

**HIGH.** A firewall bug here can: (a) leak (under-block — the thing we're fixing),
or (b) **brick connectivity** (over-block + fail to clean up → user's whole
network dead until they manually flush nft/pf). (b) is worse than the status quo.
Mitigations: dedicated table/anchor (never touch user rules), idempotent
create-with-delete-first, a ProcessExit cleanup hook, an emergency
`vpnrouter --firewall-reset` CLI verb, and supervised real-host testing before
any release. Default stays OFF (`BlockOnVpnFail = false`), so only opt-in users
are exposed during rollout.

## Recommendation / sequencing

1. **User review + green-light** (this brief).
2. Implement `LinuxFirewallManager` (nft) first — most users; + unit tests + the
   `--firewall-reset` safety verb + ProcessExit cleanup. Ship behind the existing
   opt-in, test on a real Linux host before release.
3. `MacFirewallManager` (pf) second, same pattern.
4. DNS-lockdown parity third (separate `IDnsLockdown`).
5. Each step: real-host integration test of the **cleanup-on-crash** path is the
   gate — that's the brick risk.

## Cross-references

- `VPNRouter.Core/Services/FirewallManager.cs` (Windows reference impl)
- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs` (current stub)
- `VPNRouter.Core/Interfaces/IFirewallManager.cs`, `Platform/PlatformServices.cs`
- `plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md` (Wave 39 DNS lockdown)
- `HealthMonitor.cs:373/472`, `StartupPipeline.cs:975`, `VpnEngine.cs:390` (orchestration)
