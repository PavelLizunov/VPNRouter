# True OS-level split tunnel — research + design doc

Date: 2026-07-03. Author: research agent (Fable 5), grounded in code + primary sources.
Status: RESEARCH / DESIGN — no code changes yet.

---

## 1. Problem statement

VPNRouter's "split tunnel" is not an OS-level split. Verified in code:

- `VPNRouter.Core/Models/TunSettings.cs:29` — `AutoRoute = true` by default;
  `ConfigGenerator.cs:1127` passes it straight into the sing-box TUN inbound.
  sing-box installs a default route into the wintun adapter, so the TUN captures
  ALL traffic at the OS level.
- Include/exclude app split is implemented as sing-box `route.rules` matching
  `process_name` INSIDE the tunnel (post-capture). Excluded/direct apps' packets
  still transit TUN -> sing-box -> `direct` outbound -> physical NIC.
- `RouteExcludeAddress` (`TunSettings.cs:47`) is address-based only (WG/AWG
  coexistence, e.g. 10.9.1.0/24) — not app-based.
- There is no per-app OS-level driver in the product. The only WinDivert usage is
  the bundled zapret DPI-bypass tool (`tools/zapret/WinDivert64.sys`,
  `ZapretManager.cs`); `ResilientStarter.cs:12` merely mentions its load race.

**Consequence:** when sing-box dies or hangs, excluded apps go down with it,
because their packets were riding the TUN. In a true split, excluded apps' flows
never touch the TUN and are indifferent to VPN health.

### 1.1 What actually happens on failure today (crash-behavior matrix)

Failure mode analysis, grounded in `FirewallManager.cs`, `HealthMonitor.cs`,
wintun semantics:

| Failure | TUN adapter / routes | Routed apps | Excluded apps |
|---|---|---|---|
| **sing-box process crashes/killed** | wintun adapter is destroyed with the owning process (`WintunCloseAdapter` "removes adapter" — [wintun README](https://github.com/WireGuard/wintun); empirically acknowledged in `FirewallManager.cs:23` "when sing-box dies and TUN is gone"); its routes vanish with it | blocked by per-program netsh kill-switch rules (`CreateBlockRules` creates `program=<path>` rules — correct, fail-closed) | recover via physical NIC after the OS removes the adapter (~1-2 s), **except** DNS if Wave 39 DNS-lockdown rules are active (they block UDP/TCP 53 on every adapter except loopback+TUN, `FirewallManager.cs:46-70`) — then excluded apps have no DNS until sing-box returns |
| **sing-box process hangs** (alive, not forwarding) | adapter + default route stay up, pointing at a dead engine | black-hole (semi-intended, fail-closed) | **black-hole — the bug.** Until `HealthMonitor` probe threshold (2 failed probes, `HealthProbeRestartFailThreshold`) triggers a kill+restart, everything routed through TUN dies, including excluded apps and all system DNS |
| **VPN server dead, sing-box alive** | up | stall (fail-closed at outbound; intended) | actually fine — TUN -> `direct` outbound -> NIC still forwards |
| **restart loop** (`AttemptRestart` backoff 5/10/20/40/80 s) | adapter torn down / recreated repeatedly | blocked during gaps | repeated multi-second stalls each cycle |

So the standing black-hole for excluded apps is (a) the hang case, (b) the DNS
lockdown during down-windows, (c) flapping. The crash case mostly self-heals at
the routing layer because wintun adapters are process-lifetime-bound — the OS
removes routes with the adapter.

---

## 2. Windows options, head-to-head

### 2.1 The industry answer: WFP callout driver with bind/connect redirection

Every major open-source Windows VPN converged on the same design — a kernel WFP
callout driver that redirects excluded apps' socket binds to the physical NIC's
IP at the ALE layers, before any packet exists:

| Vendor | Driver | License | Notes |
|---|---|---|---|
| Mullvad | [mullvad/win-split-tunnel](https://github.com/mullvad/win-split-tunnel) | **GPL-3.0-or-later OR MPL-2.0 (dual, your option)** — verified from README License section | Non-PnP KMDF. Bind redirection + in-kernel process tree + child-process exclusion propagation + leak blocking. Full IPv6. |
| Windscribe | [Desktop-App/backend/windows/WindscribeSplitTunnel](https://github.com/Windscribe/Desktop-App/blob/master/backend/windows/WindscribeSplitTunnel/CalloutFunctions.c) | GPL-2.0 (repo) | ALE connect-redirect to a chosen interface index; inclusive + exclusive modes ([DeepWiki writeup](https://deepwiki.com/Windscribe/Desktop-App/3.6-split-tunneling)) |
| PIA | [pia-foss/desktop-windows-wfp-callout](https://github.com/pia-foss/desktop-windows-wfp-callout) | **No license file** (GitHub license API returns 404) — legally not reusable | Bind/connect redirect; README requires EV cert to build installable |
| Proton | [ProtonVPN/win-app](https://github.com/ProtonVPN/win-app) `ProtonVPN.CalloutDriver` | GPL-3.0 (repo) | Bind redirection for split tunnel + DNS SERVFAIL injection for leak protection |
| Cloudflare WARP | n/a | closed | **No app-based split at all on desktop** — split tunnels are IP/domain only; docs say "Until Split Tunnels mode supports App Types, you will need to manually add all domains used by a particular app" ([Cloudflare One docs](https://developers.cloudflare.com/cloudflare-one/team-and-resources/devices/cloudflare-one-client/configure/route-traffic/split-tunnels/)) |

Why bind-redirect survives a VPN crash by construction: the excluded app's
socket is bound to the physical NIC's address. Windows' strong-host model then
routes those packets via the physical interface's own default route (the DHCP
gateway route never disappears — sing-box's `auto_route` adds a better route on
the TUN, it does not remove the NIC's). The excluded flow never references the
TUN, so TUN death is invisible to it. This is research question 2 answered: the
required invariant is "excluded flow's route table entries live on the physical
interface", and bind redirection at ALE_BIND time is exactly what pins that.

#### The decisive finding: Mullvad ships the signed binary publicly

[mullvad/mullvadvpn-app-binaries](https://github.com/mullvad/mullvadvpn-app-binaries)
`x86_64-pc-windows-msvc/split-tunnel/` contains prebuilt
`mullvad-split-tunnel.sys` + `.inf` + `.cat` (+ `.pdb`). Verified locally
(2026-07-03, `Get-AuthenticodeSignature`):

- `mullvad-split-tunnel.sys`: Valid, signer `CN=Mullvad VPN AB ... SE` via
  DigiCert Trusted G4 Code Signing, cert valid to 2027-02-07.
- `mullvad-split-tunnel.cat`: Valid, signer
  **`CN=Microsoft Windows Hardware Compatibility Publisher`** — i.e. Microsoft
  attestation-signed. Loads on production Win10/11 x64 with Secure Boot, no
  test-signing, no cert purchase, no Partner Center account.

License is dual GPL-3.0-or-later / MPL-2.0. VPNRouter is GPL-3.0 (repo
`LICENSE`), so either branch is clean; MPL-2.0 removes even theoretical
contamination arguments for the packaging. Redistribution of the signed binary
is the intended use — Mullvad's own OSS build instructions bundle exactly these
files, and the Authenticode/attestation signatures stay valid on unmodified
redistribution (same model as everyone bundling wintun.dll or WinDivert).

Driver contract (from the [README](https://github.com/mullvad/win-split-tunnel/blob/master/README.md)):
user-mode agent talks to the device via IOCTLs through a fixed state machine:
`ST_DRIVER_STATE_STARTED` -> `IOCTL_ST_INITIALIZE` -> `IOCTL_ST_REGISTER_PROCESSES`
(initial process tree snapshot) -> `IOCTL_ST_SET_CONFIGURATION` (excluded image
paths) + `IOCTL_ST_REGISTER_IP_ADDRESSES` (tunnel IPv4/IPv6 + internet NIC
IPv4/IPv6) -> `ST_DRIVER_STATE_ENGAGED`. After that the driver tracks
process arrivals/departures in-kernel (including child-process propagation) and
sends events back via inverted call. The agent's ongoing duties: watch interface
IP changes and re-register IPs; convert DOS paths to device paths; volume
arrival handling (see `talpid-core/src/split_tunnel/windows/{driver,path_monitor,volume_monitor}.rs`
in [mullvadvpn-app](https://github.com/mullvad/mullvadvpn-app) for the reference agent).

Documented driver limitations (README "Limitations" — these become our UX
caveats):

1. **DNS**: most apps resolve via the `dnscache` service (svchost), so the
   driver sees svchost, not the excluded app — excluded apps' DNS still goes
   inside the tunnel while the tunnel is up. (Mullvad's user docs confirm:
   "When you exclude an app ... it will still use the DNS server on the Mullvad
   server" — [help page](https://mullvad.net/en/help/split-tunneling-with-the-mullvad-app).)
   Note this also means our CURRENT exclude-mode per-process DNS rule in
   `ConfigGenerator.BuildDns` (ProcessName -> local-dns) only matches apps doing
   their own UDP/53 (e.g. Chrome's async resolver), not getaddrinfo()-style
   lookups which arrive from svchost. Same root cause, pre-existing.
2. **Localhost UDP**: excluded apps' unbound UDP client sockets get redirected
   away from `inaddr_any`, breaking UDP to 127.0.0.1 unless the app explicitly
   binds loopback. Known to bite launcher/IPC patterns.
3. **Multicast reception** for excluded apps can break (bind redirected, group
   join on the wrong interface).
4. Windows Store/UWP apps can't be excluded (per Mullvad user docs).

### 2.2 WinDivert reuse (already bundled for zapret)

Two shapes were assessed:

**(a) "Divert excluded apps' packets around the TUN" — NOT feasible cleanly.**
The [WinDivert docs](https://reqrypt.org/windivert-doc.html) are explicit:
process ID is only available at the FLOW/SOCKET layers, which cannot inject
(FLOW: "not blocked nor injected"; SOCKET: block-only). At the NETWORK layer,
for outbound injection "the IfIdx and SubIfIdx fields are currently ignored" —
the TCP/IP stack re-routes re-injected packets by destination, which lands them
right back on the TUN default route. Forcing egress via the NIC would require
rewriting source IPs plus a full userspace NAT plus answering on the same path
— building a second VPN engine, not a fix.

**(b) Inverted architecture ("no auto_route at all")** — leave the physical
default route untouched, and have a WinDivert-based interceptor capture ONLY
routed apps' flows (SOCKET-layer events give PID+5-tuple before the SYN;
NETWORK-layer NAT redirects the flow into sing-box's local `mixed` inbound —
the classic redirect-to-local-proxy pattern from WinDivert's streamdump
sample). This is the only WinDivert shape that yields a true split, and it is
how WinDivert-based VPNs (e.g. VpnHood) avoid route manipulation entirely.
It even fixes include-mode (see 2.5). But it is a data-path rearchitecture:
per-packet kernel<->user copies for ALL proxied traffic, a PID->flow race at
connect time, own NAT table, UDP flows, IPv6, loopback edge cases.

**License**: WinDivert is dual LGPLv3 / GPLv2 — fine for a GPL-3.0 app.
**The real problem is signing provenance.** Verified locally: the
`tools/zapret/WinDivert64.sys` we already ship is signed by
`成都密思听科技有限公司` (a Chengdu company; not basil00), cert **expired
2023-05-26**, loading via timestamped legacy cross-signing. Upstream's last
release is v2.2.2 (2022-09-21, [releases](https://github.com/basil00/WinDivert/releases));
the original signing cert saga is why third-party re-signs circulate. Microsoft
has announced its intent to end trust for the cross-signed driver program
([Windows IT Pro blog](https://techcommunity.microsoft.com/blog/windows-itpro-blog/advancing-windows-driver-security-removing-trust-for-the-cross-signed-driver-pro/4504818);
body not independently verified in this research — treat the timeline as TBD).
If that lands, these binaries stop loading on updated systems. Acceptable risk
for an optional DPI tool; unacceptable as the foundation of the VPN data path.

### 2.3 sing-box / wintun native

Nothing OS-level per-app exists on Windows. From the
[TUN inbound docs](https://sing-box.sagernet.org/configuration/inbound/tun/):

- `auto_redirect`: "Only supported on Linux with auto_route enabled" (nftables).
  Not a Windows mechanism.
- `strict_route` on Windows: makes unsupported networks unreachable + adds a WFP
  filter against Windows' multihomed DNS behavior. Leak hardening, not app
  exclusion. (We deliberately keep it `false` — `ConfigGenerator.cs:1128`.)
- Per-app selectors exist only as: `include_uid`/`exclude_uid` (Linux),
  `include_package`/`exclude_package` (Android). Windows/macOS have only
  post-capture `process_name` route rules — the thing we already use.
- `route_exclude_address` — address-based only, as we use for WG/AWG.

Conclusion: sing-box will not do this for us on Windows, now or (per docs
direction) plausibly ever — upstream's Windows answer is route rules.

### 2.4 Pure routing / binding tricks (no driver)

- **Per-app SO_BINDTODEVICE-equivalent enforceable from outside: does not exist
  on Windows.** `IP_UNICAST_IF`/bind-to-source are opt-in from inside the app's
  own code. Forcing them externally means DLL injection (ForceBindIP style) —
  fragile, per-arch, anticheat-hostile, not product-grade.
- **Route metrics / policy routing**: Windows route selection is
  destination-based; no process dimension. No partial win exists here beyond
  what `RouteExcludeAddress` already does per-subnet.
- **User-mode WFP (no driver)**: can add per-app PERMIT/BLOCK filters
  (`FWPM_CONDITION_ALE_APP_ID`) from a service without any driver, but the
  redirect layers (`ALE_BIND_REDIRECT` / `ALE_CONNECT_REDIRECT`) explicitly
  require a kernel callout ([MS docs: Using Bind or Connect Redirection](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/using-bind-or-connect-redirection)).
  So user-mode WFP cannot re-route an app — but it CAN make the kill-switch
  per-app-correct and replace brittle netsh semantics. Useful as a companion,
  not a split mechanism.

### 2.5 Include mode caveat (honest scoping)

Mullvad-style drivers implement EXCLUSION only (bind excluded apps to the NIC).
VPNRouter's include-split ("route only listed apps") is the inverse: at OS level
the unlisted majority would need excluding — unbounded set, wrong shape for the
driver. Nobody in the survey solves include-mode with a bind-redirect driver;
the only architecture that solves include-mode OS-level is 2.2(b) (capture only
included apps, no default-route change). Scope decision: the driver fixes
exclude-mode and full-tunnel-with-exceptions; include-mode keeps post-capture
rules + the fast-teardown mitigation (its unlisted apps then recover in seconds
after a crash, which is most of the win).

### 2.6 Writing our own driver / forking

Requires an EV code-signing cert (~$280-580/yr, hardware token) + Microsoft
Partner Center attestation for every build
([signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs),
[attestation](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation)),
plus WDK maintenance across Windows releases and HVCI compatibility testing.
For a solo dev this is the worst option; it is listed only to price the
alternative. Same math kills "fork Mullvad's driver and change it" — any byte
change invalidates the Microsoft signature. ProxiFyre/WinpkFilter (NDIS-level,
[ntkernel](https://www.ntkernel.com/windows-packet-filter/)) was assessed and
rejected: driver redistribution is license-gated commercial
("If your license allows driver redistribution..." — their docs), an unknown
cost + vendor dependency.

---

## 3. Options table

| Option | Mechanism | Effort | Risk | License / signing | Maintenance |
|---|---|---|---|---|---|
| **W1. Bundle Mullvad win-split-tunnel (recommended)** | WFP callout, ALE bind redirect, in-kernel process tree | M (2-4 wk: C# IOCTL agent port, service install, IP/volume monitoring, mode gating) | M — driver is mature + fuzzed by a funded team; our risk is agent correctness + interop with netsh rules | GPLv3-or-later OR MPL-2.0; **Microsoft-attestation-signed binary published by vendor**; zero signing cost | Track their repo for updates; driver ABI stable for years; worst case: pin version |
| W2. Same, but Windscribe/Proton driver | same idea | M | M, plus extraction from installers (no standalone binary channel) | GPL-2 / GPL-3; signed binaries not separately published | worse channel than W1, no gain |
| W3. WinDivert inverted architecture | SOCKET-layer PID map + NETWORK-layer NAT to local sing-box inbound; no auto_route | XL (data-path rewrite) | H — perf (all proxied traffic through user mode twice), flow races, own NAT; **signing provenance of shipped .sys is a re-sign with expired CN-company cert; cross-sign trust being retired by MS** | LGPLv3 OK | single upstream maintainer, last release 2022 |
| W4. sing-box native | none exists on Windows (`auto_redirect` = Linux-only) | — | — | — | — |
| W5. No-driver tricks | user-mode WFP permit/block per app (no redirect); injection hacks | S | injection = not product-grade | — | — |
| W6. Own/forked callout driver | WFP callout | XL + $ | H (WDK, HVCI, cert, attestation per build) | EV cert $280-580/yr + Partner Center | permanent tax; worst for solo dev |
| **W0. Fast-teardown mitigation (do first, keep forever)** | on crash/hang: hard-kill sing-box -> wintun adapter+routes die with process; scope DNS-lockdown; verify adapter gone | S (days) | L — races bounded by detection latency | none | trivial |

---

## 4. macOS (secondary)

- **Mullvad's approach**: shipped 2024/2025 for macOS 13+ ([blog](https://mullvad.net/en/blog/2025/1/10/split-tunneling-on-macos)).
  Not NetworkExtension: `talpid-core/src/split_tunnel/macos/{tun.rs,bpf.rs,process.rs,default.rs}`
  shows a custom second utun + BPF packet shuffling + process monitoring in the
  daemon. Their own docs admit "significant overhead, for both excluded apps
  and VPN traffic", WebKit child processes can't be excluded, connections die
  when toggling. A funded team's result being this caveated is the strongest
  signal that a solo dev should not walk this road.
- **Apple-sanctioned path**: `NETransparentProxyProvider` system extension
  (per-flow app attribution via signing identifier;
  [Apple docs](https://developer.apple.com/documentation/networkextension/netransparentproxyprovider)).
  Requires Developer ID + the NetworkExtension entitlement + notarized system
  extension + a Swift/ObjC component and a new build pipeline on the Mac host.
  Realistic long-horizon item, not near-term.
- **pf**: cannot match by process; per-user/group only. No per-app pf split.
- **Near-term plan**: W0 mitigation only (kill fast, utun disappears with the
  process, pf kill-switch already scoped by `MacFirewallManager`); document that
  macOS has no true per-app split yet. Mullvad's own macOS caveat list makes
  this defensible.

## 5. Linux (cleanest platform)

Mechanism (same family Mullvad uses — verified in
`talpid-core/src/split_tunnel/linux/mod.rs`: "Linux split-tunneling
implementation using cgroups", `MARK = 0xf41`, with both cgroup v1 `net_cls`
and cgroup v2 + nftables backends):

1. Create a dedicated cgroup, e.g. `/sys/fs/cgroup/vpnrouter.excluded/`.
2. Move excluded PIDs in (`cgroup.procs`); our process watcher already resolves
   names -> PIDs (`ProcessScanner`); new children inherit the cgroup.
3. nftables: `meta l4proto ... socket cgroupv2 level 1 "vpnrouter.excluded" meta mark set 0xf41 ct mark set meta mark`
   (cgroup v2 socket match — see [fraggod writeup](https://blog.fraggod.net/2021/08/31/easy-control-over-applications-network-access-using-nftables-and-systemd-cgroup-v2-tree.html),
   [Oracle blog on cgroup v2 + nftables](https://blogs.oracle.com/linux/cgroup-v2-meets-nftables)).
   Gotcha from those sources: cgroup-path rules resolve to inodes at rule-add
   time — (re)create the cgroup BEFORE loading rules, never delete it while
   rules live.
4. `ip rule add fwmark 0xf41 lookup main pref 8999` (one slot before sing-box's
   auto_route rules at index 9000, table 2022 — [sing-box TUN docs](https://sing-box.sagernet.org/configuration/inbound/tun/)),
   so marked traffic resolves via the physical default route and never enters
   the TUN. Plus `rp_filter` loosening on the NIC if strict.
5. Kill-switch interaction: `LinuxFirewallManager`'s nftables egress-drop gets
   one `socket cgroupv2 ... accept` rule above the drop — excluded apps stay up
   during down-windows, routed apps stay fail-closed. (Also mitigates the known
   P1-6 "global egress-drop bricks the host" hazard for excluded tooling.)
6. DNS: same shared-resolver caveat (systemd-resolved is its own process); v1
   answer: dnat excluded-cgroup port 53 to the NIC's resolver, or document.

Effort S-M (shell-out to `nft`/`ip` like the existing nftables kill-switch; no
new deps). This is the one platform where true split is basically free, and it
survives VPN crash by construction (marked flows use `main` table).

## 6. Interaction with the existing kill-switch / auto_route (summary)

- Windows netsh kill-switch is ALREADY per-program (block rules for routed apps
  only) — compatible with W1 unchanged. Do NOT add "block all": Windows
  Firewall block-beats-allow means a global block cannot be punched per-app.
- Wave 39 DNS lockdown conflicts with excluded apps during down-windows (blocks
  UDP/TCP 53 on the NIC). With W1/W0: disable or relax it in exclude-split mode,
  or migrate those four rules to user-mode WFP filters with
  `FWPM_CONDITION_ALE_APP_ID` conditions in our own sublayer.
- Mullvad's driver issues its own permits "to override Mullvad VPN app WFP
  filters" (README). Whether its permits also override MPSSVC (netsh) blocks is
  UNVERIFIED — test matrix item #1. If they do, the driver would also punch
  through our per-program blocks for excluded apps (desired) — but our rules
  only target routed apps anyway, so no overlap is expected.
- sing-box `auto_route` stays as-is with W1. Excluded apps' flows simply never
  arrive at the TUN; the existing `process_name` direct rules remain as
  defense-in-depth for path-vs-name mismatches. `strict_route` stays false.
- AWG coexistence: `RouteExcludeAddress` (10.9.1.0/24 etc.) is orthogonal and
  unaffected; the driver additionally needs the CORRECT "internet interface"
  choice on multi-NIC boxes (agent responsibility; reuse
  `NetworkInterfaceDetector`).

## 7. Pragmatic partial mitigation (W0) — build this first

Goal: excluded apps recover in ~=seconds instead of "until user intervenes",
without any driver. Changes are small and all in existing files:

1. **Hang case** (the real black-hole): `HealthMonitor` already detects via
   probes and kills — tighten: on 2 failed probes, `Kill(entireProcessTree:true)`
   FIRST (adapter dies with the process, OS restores NIC routes + DNS), then
   backoff/restart. Add a post-kill assert: adapter with `Tun.InterfaceName`
   gone from `GetIfTable2`; if a zombie adapter persists, delete its routes
   explicitly (`DeleteIpForwardEntry2` P/Invoke or `netsh interface ipv4 delete
   route`). Detection latency = probe interval; consider a cheap 2-3 s TCP
   canary through the TUN to cut it.
2. **Crash case**: already self-heals at the route layer (process-lifetime
   adapter); the remaining breakage is DNS lockdown — scope Wave 39: in
   exclude-split profiles, either skip those rules or add a crash-window
   temporary disable (they are already toggled by HealthMonitor's
   `EnableBlockRules`/`DisableBlockRules` lifecycle hooks, so the hook points
   exist).
3. **Flap dampening**: while in the restart-backoff window, do not recreate the
   TUN until the health probe of the PREVIOUS attempt has a chance to pass
   (MaxRestartAttempts cap already exists); excluded apps then see one gap, not
   five.
4. **The race, honestly**: between engine death and detection (probe interval,
   worst case ~2 intervals) excluded apps still black-hole in the hang case; in
   the crash case the gap is OS adapter-teardown time (~1-2 s). W0 cannot fix
   the hang-window — only a true split (W1) makes excluded apps indifferent to
   it. W0 is still worth shipping first: it shrinks every failure mode today
   and remains necessary DNS/teardown hygiene even after W1 (the driver does
   not fix DNS-through-dead-TUN either, per its own DNS limitation).

## 8. Ranked recommendation

1. **W0 now** (days): fast teardown + Wave-39 scoping + flap dampening.
2. **W1 next on Windows** (the real feature, 2-4 wk): bundle Mullvad's signed
   driver, port the agent to C#. Exclude-mode + full-tunnel-with-exceptions
   get true OS-level split; include-mode documented as post-capture.
3. **Linux cgroup v2 split** (S-M, independent): highest correctness-per-effort
   in the whole plan; also de-risks the kill-switch P1-6 hazard.
4. **macOS**: W0 only; NETransparentProxyProvider as a someday item.
5. Explicitly rejected: own/forked driver (signing economics), WinDivert as VPN
   data path (signing provenance + rearchitecture), ProxiFyre/WinpkFilter
   (commercial redistribution), waiting for sing-box (Windows per-app is
   architecturally out of scope upstream).

## 9. Incremental implementation sketch — W1 (Windows)

Phase 0 (spike, 2-3 days, throwaway console app):
- `sc create mullvad-split-tunnel type= kernel binPath= <sys>` (or
  CreateService P/Invoke), open `\\.\MULLVADSPLITTUNNEL`, drive the IOCTL
  state machine with hardcoded values (one excluded notepad.exe, current NIC +
  TUN IPs), verify: notepad's traffic egresses NIC while sing-box runs; kill
  sing-box; verify notepad unaffected. Structs/IOCTL codes: port from
  `talpid-core/src/split_tunnel/windows/driver.rs` (dual-licensed same as
  driver). Test on windows-brat VM (192.168.0.106) via testvm-control.
- Gate: PASS -> continue; FAIL (e.g. netsh-block interplay) -> stop, W0 only.

Phase 1: `SplitTunnelDriverManager.cs` in Core — service install/start/stop,
device handle, state machine, `RegisterProcesses` snapshot (Toolhelp32 +
image paths), `SetConfiguration` (excluded names -> full paths via existing
`ProcessScanner`/`where` logic -> NT device paths via `QueryDosDevice`),
`RegisterIpAddresses` from `NetworkInterfaceDetector`, inverted-call event
pump on a background thread. Fail-open design: any driver failure logs +
falls back to current post-capture behavior (never brick networking).

Phase 2: lifecycle wiring — `VpnEngine.StartAsync`/`Apply`/`Stop` engage/
disengage around sing-box start; IP-change events re-register; uninstall on
app uninstall (installer script). Wave-39 scoping from W0 applies.

Phase 3: UX + docs — "true split" badge in exclude mode; caveats surfaced
(driver DNS/localhost-UDP/multicast limitations from section 2.1); MCP verify
checklist: exclude Discord, kill sing-box.exe, Discord voice must survive.

Packaging: +~110 KB (3 files), Windows x64 (aarch64 build also published by
Mullvad if ever needed). Keep driver version pinned + checksummed in build.ps1
(same pattern as sing-box-lx bundling).

## 10. Open questions

1. Does the driver's permit override netsh (MPSSVC) block rules and the Wave-39
   DNS lockdown? (Phase-0 test matrix.)
2. Driver vs. zapret/WinDivert on the same flows — winws touches 80/443 of ALL
   processes; excluded apps' redirected flows will also pass WinDivert. Expected
   benign (zapret is payload-mangling, not routing), needs one test.
3. IOCTL ABI stability across mullvad releases — pin exact commit of
   `driver.rs` structs to the bundled .sys version; re-verify on driver bumps.
4. Service-name collision if the user also installs Mullvad VPN proper —
   likely must detect `mullvad-split-tunnel` service existence and either reuse
   or refuse with a clear message (both apps driving one driver instance is
   undefined).
5. Include-mode long game: is WinDivert-inverted (2.2b) ever worth XL effort,
   or does exclude-mode + full-tunnel cover real usage? Collect telemetry-free
   signal via user reports first.
6. Mullvad driver on Windows on ARM — binaries exist upstream
   (aarch64-pc-windows-msvc), untested here; out of scope until we ship ARM64.

## Sources

- Code: `VPNRouter.Core/Services/ConfigGenerator.cs` (BuildInbounds ~1103-1148,
  BuildDns ~1040-1099), `Models/TunSettings.cs`, `Services/FirewallManager.cs`
  (lifecycle comment 10-25, Wave-39 46-70, CreateBlockRules 217+),
  `Services/HealthMonitor.cs` (probe threshold, EnableBlockRules call ~690),
  `tools/zapret/` (WinDivert64.sys — signature inspected locally).
- https://github.com/mullvad/win-split-tunnel (README: architecture, states,
  limitations, dual license GPL-3.0+/MPL-2.0)
- https://github.com/mullvad/mullvadvpn-app-binaries (prebuilt signed driver;
  signatures verified locally 2026-07-03: sys = Mullvad VPN AB / DigiCert
  (to 2027-02-07), cat = Microsoft Windows Hardware Compatibility Publisher)
- https://github.com/mullvad/mullvadvpn-app — talpid-core/src/split_tunnel/
  {windows,macos,linux} (reference agent; macOS = utun+BPF+process monitor;
  linux = cgroups, MARK 0xf41)
- https://mullvad.net/en/blog/2025/1/10/split-tunneling-on-macos ;
  https://mullvad.net/en/help/split-tunneling-with-the-mullvad-app
- https://github.com/Windscribe/Desktop-App (GPL-2.0;
  backend/windows/WindscribeSplitTunnel/CalloutFunctions.c) ;
  https://deepwiki.com/Windscribe/Desktop-App/3.6-split-tunneling
- https://github.com/pia-foss/desktop-windows-wfp-callout (no license file;
  EV-cert build requirement)
- https://github.com/ProtonVPN/win-app (GPL-3.0; CalloutDriver = split tunnel
  bind redirect + DNS SERVFAIL)
- https://reqrypt.org/windivert-doc.html (layers, PID availability, outbound
  IfIdx ignored, dual LGPLv3/GPLv2) ;
  https://github.com/basil00/WinDivert/releases (v2.2.2, 2022-09-21)
- https://techcommunity.microsoft.com/blog/windows-itpro-blog/advancing-windows-driver-security-removing-trust-for-the-cross-signed-driver-pro/4504818
- https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs ;
  https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation ;
  https://learn.microsoft.com/en-us/windows-hardware/drivers/network/using-bind-or-connect-redirection
- https://sing-box.sagernet.org/configuration/inbound/tun/ (auto_redirect
  Linux-only; strict_route semantics; uid/package selectors per platform)
- https://developers.cloudflare.com/cloudflare-one/team-and-resources/devices/cloudflare-one-client/configure/route-traffic/split-tunnels/
- https://www.ntkernel.com/windows-packet-filter/ ;
  https://github.com/wiresock/proxifyre
- https://blog.fraggod.net/2021/08/31/easy-control-over-applications-network-access-using-nftables-and-systemd-cgroup-v2-tree.html ;
  https://blogs.oracle.com/linux/cgroup-v2-meets-nftables
- https://developer.apple.com/documentation/networkextension/netransparentproxyprovider
- https://github.com/WireGuard/wintun (WintunCloseAdapter semantics)
