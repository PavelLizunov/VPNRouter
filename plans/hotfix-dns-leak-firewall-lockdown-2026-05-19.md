# Hotfix — Wave 39: DNS leak via OS resolver despite SMHNR/ParallelAAAA disabled

**Date**: 2026-05-19
**Priority**: P0 (user-reported, **DNS leak = privacy regression**)
**Risk**: MEDIUM-HIGH (firewall rules, could break user's coexisting services if scoped wrong)
**User**: `Z:/brat`, IP-leak test screenshot shows 100% DNS leak to Russian ISP
**Ship target**: v2.35.0-r5 (after r4 TUN hotfix lands) OR cherry-pick to v2.32.x stable

## Symptom

User shipping ipleak.net test from browser on subscribe + split-tunnel config:

- Public IP: `104.194.156.93` (Germany — DE VPN exit ✓)
- DNS leak panel: **5 resolvers, 119 tests**:
  - `185.229.191.160` (NL Datacamp) — 122 hits (VPN-side, AdGuard upstream)
  - `195.2.238.4` (RU Trytek LLC) — 119 hits **← USER'S ISP DNS**
  - `195.2.238.58` (RU Trytek LLC) — 119 hits **← USER'S ISP DNS**
  - `195.2.239.4` (RU Trytek LLC) — 119 hits **← USER'S ISP DNS**
  - `84.17.46.77` (NL Datacamp) — 116 hits (VPN-side, AdGuard upstream)

Hit counts identical (119) across all three Russian resolvers = Windows DNS
Client is **racing** all configured resolvers in parallel **despite**
`SMHNR=disabled + ParallelAAAA=disabled` (our v2.31.0-r1 CO-5 hardening).

This is full-on **DNS privacy regression** — every domain user resolves
is visible to their ISP.

## Root cause

Two layers below sing-box's DNS hijack:

### Layer 1: Browser-side DoH fallback

Modern Chrome/Edge/Firefox enable **DNS-over-HTTPS** by default. Their DoH
talks to `dns.cloudflare.com:443` etc. — that traffic IS routed via VPN
(visible in user's singbox.log: `outbound/vless: dns.adguard-dns.com:443`).
But browsers FALL BACK to system DNS on DoH failure (network change, TLS
handshake hiccup, etc.). Windows Update / driver change ~week ago may
have triggered persistent DoH-fail-to-system fallback.

### Layer 2: Windows DNS Client multi-resolver race

Even with `SMHNR=0` + `EnableMultilabel=0`, Windows 11 22H2+ STILL queries
multiple resolvers in parallel under certain conditions:
- When TUN interface metric is tied or higher than ethernet
- When NetworkLocationAwareness service marks TUN as "private" network
- When mDNSResponder / LLMNR fallback is active
- When IPv6 is partially configured (IPv4 query + IPv6 query race)

The 119:119:119 hit ratio across Russian resolvers proves all 3 are
receiving each query in parallel from Windows DNS Client. This bypasses
sing-box entirely — queries go via Ethernet adapter (lowest metric for
the non-TUN routes) directly to ISP DNS.

Our existing `WindowsDnsHardening.cs`:
- ✓ Disables SMHNR (registry)
- ✓ Disables ParallelAAAA (registry)
- ✓ Pins VPNRouter-TUN to metric 1

What it does NOT do:
- ✗ Block UDP/53, TCP/53, TCP/853 outbound on non-TUN interfaces
- ✗ Force-stop Windows DNS Client multi-resolver behavior at the Winsock layer
- ✗ Disable IPv6 DNS resolution (browsers may use IPv6 DNS for AAAA records)

## Fix strategy: firewall-level DNS lockdown

The only foolproof way to prevent DNS leak on Windows is to **block port
53 + 853 outbound on all non-TUN interfaces** while VPN is active.
Sing-box's internal DNS flow (browser → TUN → sing-box → AdGuard DoH)
goes via `outbound/vless` to port 443, so it's NOT affected by 53/853
block. The block ONLY kills queries that bypass TUN.

### Firewall rule shape

When VPN starts (after sing-box launches + TUN ready):

```
# Block outbound UDP 53 (standard DNS) on all interfaces
netsh advfirewall firewall add rule \
  name="VPNRouter-DnsLockdown-UDP53" \
  dir=out action=block \
  protocol=UDP remoteport=53 \
  enable=yes

# Block outbound TCP 53 (DNS-over-TCP) on all interfaces
netsh advfirewall firewall add rule \
  name="VPNRouter-DnsLockdown-TCP53" \
  dir=out action=block \
  protocol=TCP remoteport=53 \
  enable=yes

# Block outbound TCP 853 (DNS-over-TLS) on all interfaces
netsh advfirewall firewall add rule \
  name="VPNRouter-DnsLockdown-TCP853" \
  dir=out action=block \
  protocol=TCP remoteport=853 \
  enable=yes
```

**Note**: these rules block 53/853 on ALL interfaces including TUN.
That's intentional. Sing-box's DNS flow does NOT use outbound 53 —
it queries `dns.adguard-dns.com:443` (DoH). So sing-box still works.
The OS DNS Client (which DOES use 53/853) is silenced.

### Exception: System loopback DNS

Some Windows apps query `127.0.0.1:53` (e.g., dnscrypt-proxy running
locally if user has one, or DnsClient stub at loopback). Don't block
loopback:

```
# Allow outbound DNS on loopback (for any local DNS proxy)
netsh advfirewall firewall add rule \
  name="VPNRouter-DnsLockdown-Loopback-Allow" \
  dir=out action=allow \
  protocol=UDP remoteip=127.0.0.1 remoteport=53 \
  enable=yes
```

Allow rule must come BEFORE block rule (Windows Firewall first-match
semantics — `name` lexical sort decides; prefix "Allow" with `0_` to
sort first, or use `New-NetFirewallRule -Action Allow -Priority`).

### Cleanup on VPN stop

```
netsh advfirewall firewall delete rule name="VPNRouter-DnsLockdown-UDP53"
netsh advfirewall firewall delete rule name="VPNRouter-DnsLockdown-TCP53"
netsh advfirewall firewall delete rule name="VPNRouter-DnsLockdown-TCP853"
netsh advfirewall firewall delete rule name="VPNRouter-DnsLockdown-Loopback-Allow"
```

Plus on app crash / abnormal exit: existing `FirewallManager.CleanupOrphanedRules`
already deletes any rule starting with `VPNRouter-` on app boot — extend
the pattern to cover these new rule names.

## Implementation

### Files to touch (Agent A)

- `VPNRouter.Core/Services/FirewallManager.cs` — add `EnableDnsLockdownAsync()` + `DisableDnsLockdownAsync()` methods
- `VPNRouter.Core/Services/WindowsDnsHardening.cs` — call `FirewallManager.EnableDnsLockdownAsync()` after registry hardening; mirror cleanup
- `VPNRouter.Core/Models/AppSettings.cs` — new `App.DnsLeakLockdown: bool` setting (default `true` for new installs, opt-in via UI for upgrade users so we don't surprise existing setups)
- `VPNRouter.Core/Services/VpnEngine.cs` — wire enable/disable into StartAsync/Stop paths

### UI (Agent B — App layer)

- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — new Settings checkbox:
  "Блокировать DNS вне VPN (защита от утечек)" — default ON for new installs,
  OFF for upgrades, with tooltip explaining what it blocks + that some local
  DNS proxies (dnscrypt-proxy, AdGuard Home local) may break.
- `VPNRouter.App/Views/SettingsPage.axaml` — checkbox + tooltip + "?" help icon

### Tests (Agent C)

- `VPNRouter.Tests/FirewallManagerDnsLockdownTests.cs` (NEW):
  - `EnableDnsLockdown_AddsThreeBlockRulesPlusLoopbackAllow`
  - `DisableDnsLockdown_RemovesAllFourRules`
  - `Lockdown_AllowRuleSortsBeforeBlockRule` — verify first-match semantics
  - `CleanupOrphanedRules_AlsoRemovesDnsLockdownRules` — existing cleanup pattern
- `VPNRouter.Tests/AppSettingsDnsLeakLockdownTests.cs` (NEW):
  - `NewInstall_DefaultsTrue`
  - `Migration_PreservesUserChoice`
  - `MigrationFromPreWave39_DefaultsFalse` (don't surprise existing users)

## Verification gate

- [ ] `dotnet build VPNRouter.sln -c Release` 0 errors
- [ ] `dotnet test VPNRouter.Tests` — full regression + new tests green
- [ ] Local repro on this VM:
  1. Start VPNRouter, connect to a server
  2. Note Windows Firewall has 4 new VPNRouter-DnsLockdown-* rules
  3. Run `nslookup google.com 8.8.8.8` from cmd — should TIMEOUT (blocked)
  4. Run `nslookup google.com` (default resolver via TUN) — should resolve
  5. Open browser to ipleak.net DNS test — should show ONLY VPN-side resolvers
  6. Disconnect VPN — verify 4 rules gone, regular DNS resumes
- [ ] User-feedback retest: ask brat to re-run ipleak.net after update
- [ ] `simplify` skill on diff (>100 LOC almost certainly)
- [ ] `security-review` skill (firewall rule changes touch privilege boundary;
  bad scoping could lock user out of all network)

## User-self-test instructions (for release notes)

After updating to v2.35.0-r5:
1. Open VPNRouter Settings → check "Блокировать DNS вне VPN" is ON
2. Click Connect, wait until status = "Connected"
3. Open https://ipleak.net in browser
4. Scroll to "DNS Addresses" section
5. **Expected**: only NL/CH/DE resolvers shown (your VPN's upstream
   DoH provider). NO Russian / your-ISP resolvers.
6. If still leaking: copy the resolver IPs and send via existing feedback
   channel — we'll diagnose what's bypassing the firewall on your specific
   Windows version.

## Risk + rollback

**Risk**: A user running a local DNS proxy (dnscrypt-proxy, Pi-hole on
the same machine, AdGuard Home on `127.0.0.1`) will break — the
loopback-allow rule covers `127.0.0.1` UDP/53 only. If their proxy uses
TCP/53 or a non-loopback IP, it breaks. Mitigation: setting is opt-in
for upgrade users; new installs get it on by default with a tooltip
warning.

**Rollback**: `git revert <commit>`. Existing users who toggled it on
have the rules removed on next Stop+Start cycle. CleanupOrphanedRules
catches any abandoned rules.

## Sibling concern (out of Wave 39 scope, document only)

The user's browser may have Chrome's "Secure DNS" enabled with explicit
Cloudflare DoH. Even with our Wave 39 firewall lockdown, Chrome's DoH
fallback path bypasses the firewall (HTTPS to dns.cloudflare.com is allowed
on port 443, which we don't block). If Cloudflare DoH talks to Cloudflare's
upstream, the upstream might be in NL Datacamp (matching 185.229.191.160 +
84.17.46.77 in the leak test). That's NOT a leak — that's expected
upstream-of-Cloudflare. The Russian Trytek resolvers ARE the leak.

If Wave 39 still shows Russian resolvers in ipleak.net after retest, the
investigation widens to Chrome's DoH config + Windows 11 DNS-over-HTTPS
service (which uses a different code path than DNS Client and may bypass
our firewall rules at the Winsock/AFD level).

## Wave 39b — defence-in-depth fallback (if firewall not enough)

If Wave 39 firewall doesn't fully resolve the leak (i.e., Windows 11
DoH service path bypasses firewall), Wave 39b options:

1. Disable Windows DNS over HTTPS service:
   ```
   Stop-Service Dnscache -Force
   Set-Service Dnscache -StartupType Disabled
   ```
   (extreme — breaks local DNS caching, every query goes uncached to
   sing-box. ~5-15% perf hit on cold-cache lookups.)

2. Set Windows 11 DoH to "Off" via registry:
   ```
   reg add "HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters" \
     /v EnableAutoDoh /t REG_DWORD /d 0 /f
   ```

3. Mark TUN adapter as `private` profile + WireGuard-style firewall
   integration (Windows DNS Client treats `private` networks
   differently — has its own anti-leak heuristics).

Defer to user feedback after Wave 39 ships.
## Audit findings (Agent C, 2026-05-19)

Read-only audit of the Wave 39 proposal against the rest of the
codebase and the broader Windows DNS ecosystem. Findings are
documented here for Agent A's reference and as a record for future
DNS-related hotfixes.

### 1. All Windows DNS Client bypass paths

The brief covers UDP/53, TCP/53, TCP/853. Other DNS paths Windows
may use:

- **UDP/443 + TCP/443 (DoH / DoT-over-443)** — Cloudflare 1.1.1.1's
  DoH endpoint is on TCP/443, and Windows 11 22H2+ has a built-in
  DoH service that talks to `cloudflare-dns.com:443`. We CANNOT
  block port 443 generally (that's HTTPS — breaks all browsing).
  Wave 39 leaves this path alone; sing-box's TUN already captures
  it because all 443 outbound goes through the routing engine. The
  Wave-39 brief §"Sibling concern" already notes this is the path
  the user's NL Datacamp resolvers (`185.229.191.160`,
  `84.17.46.77`) likely take — upstream of Cloudflare, not a leak.
- **UDP/853 (DoT-over-UDP / DoQ)** — RFC 8094 DoT is TCP-only. DNS-
  over-QUIC (RFC 9250) DOES use UDP/853. Brief doesn't mention.
  **Recommendation for Wave 39b**: add UDP/853 to the block set
  alongside TCP/853 — same defense-in-depth, no real-world
  resolver currently uses it but Windows 11 24H2 might enable it
  by default.
- **TCP/5353 / UDP/5353 (mDNS over TCP/UDP)** — see §2.
- **TCP/5355 / UDP/5355 (LLMNR)** — see §2.
- **TCP/137 / UDP/137 / UDP/138 (NetBIOS-NS)** — legacy name resolution.
  Windows Server 2022 still falls back to NBNS when DNS fails.
  Not a privacy threat at the same scale (resolves NetBIOS computer
  names, not internet hostnames) but the brat-style leak test would
  catch it as a non-VPN-DNS hit. Probably out of scope for Wave 39.

### 2. mDNS / LLMNR

Windows uses these for local network name resolution:

- **mDNS** — UDP 5353, IANA-registered, `*.local` domains
- **LLMNR** — UDP 5355, deprecated since 2024 but still default-on
  in Windows 11 Home

Both are **multicast** (224.0.0.251 for mDNS, 224.0.0.252 for LLMNR)
or destination-specific to the local subnet. Privacy risk is LOW —
the LAN-only nature means queries don't leave the user's network
segment. The brat test wouldn't see these on ipleak.net.

**Recommendation**: do NOT block 5353/5355 in Wave 39. Blocking mDNS
breaks printer discovery, Bonjour services, Apple HomeKit, Spotify
Connect on LAN. Blocking LLMNR breaks pre-Windows-Server-2008 NetBIOS
fallback (some users still have those). Trade-off: keeping these
allowed means a malicious LAN-resident attacker could observe DNS
queries, but that's already true regardless of VPN status — Wave 39
is about cloud-DNS leaks, not LAN-DNS sniffing.

**Document in release notes**: "mDNS/LLMNR remain unblocked.
Local printer / Bonjour discovery continues to work. If you need
LAN-DNS isolation, disable LLMNR via Group Policy and use
firewall-level mDNS blocking — out of scope for VPNRouter."

### 3. WireGuard / AmneziaWG / OpenVPN coexistence

Critical concern: VPNRouter Wave 39 blocks UDP/53 + TCP/53 + TCP/853
on ALL interfaces. If user has another VPN running (e.g. AmneziaWG
on `10.9.1.x` for Forgejo access, like our dev environment), THAT
VPN's DNS resolution will also be blocked.

**Coexistence matrix**:

| Other VPN type | Effect of Wave 39 |
|---|---|
| WireGuard with public DNS (`1.1.1.1`) | DNS queries blocked → other VPN cannot resolve hostnames |
| AmneziaWG with internal DNS (`10.9.1.1`) | DNS queries blocked → blocked |
| OpenVPN with `dhcp-option DNS` | OpenVPN's DNS configures resolver to a typically-non-loopback IP — blocked |
| Tailscale (uses 100.x DNS) | DNS to MagicDNS (100.100.100.100:53) — **BLOCKED** |
| Cisco AnyConnect | Internal corp DNS — **BLOCKED** |

**Mitigation paths**:

1. **Document in tooltip + release notes**: "If you use another VPN
   simultaneously, disable this setting. VPNRouter's DNS lockdown
   blocks port 53 globally — it doesn't know about other VPN tunnels."
2. **Future Wave 39b**: scope the block rules to specific
   `interfacetype=any` minus a whitelist of VPN-adapter ranges.
   netsh doesn't natively support "exclude these IPs" — would need
   PowerShell `New-NetFirewallRule -LocalAddress -RemoteAddress`
   syntax + maintenance of a curated VPN-subnet list. Complex.
3. **Auto-detect**: enumerate active network adapters at VPN-start
   time, skip the lockdown if a non-VPNRouter VPN adapter is
   detected (Tailscale, WireGuard kernel module, etc.). High false-
   positive risk — defer to user opt-in.

**Recommendation for Wave 39**: ship as-is (defaults true new
installs, false upgrades, opt-in via UI), document the coexistence
concern in tooltip + release notes. Don't block ship on this — most
users don't run two VPNs simultaneously.

### 4. Local DNS proxies

Brief covers the common case (`127.0.0.1:53`) via the loopback-allow
rule. Additional considerations:

- **dnscrypt-proxy bound to non-loopback** — some users bind it to
  `0.0.0.0:53` so other LAN devices can use them as a DNS server.
  Our lockdown blocks this (LAN devices can't query 127.0.0.1 from
  outside). User would need to disable Wave 39 OR rebind to
  loopback.
- **AdGuard Home @ 127.0.0.1:53** — covered by loopback rule. OK.
- **AdGuard Home @ 192.168.x.x:53** — NOT covered. Blocked. User
  needs to disable Wave 39.
- **Pi-hole running on same machine via Docker** — depends on bind
  address. `127.0.0.1:53` OK, `0.0.0.0:53` blocked (Docker host
  networking blocks LAN access, but the local Windows host still
  queries 127.0.0.1 internally — still OK).
- **YogaDNS / Acrylic DNS** — local DNS proxies that bind to
  loopback by default. OK.
- **DNSCrypt-Proxy + DoH** — local proxy talks to DoH upstream
  (Cloudflare, Quad9) on TCP/443 — our lockdown doesn't block 443.
  Local query 127.0.0.1:53 OK, upstream 443 OK. Works.

**Trade-off**: the loopback-allow rule is sufficient for ~95% of
local-DNS-proxy users. The remaining 5% (LAN-shared DNS proxies)
need to disable Wave 39 OR rebind to loopback. Document in tooltip.

### 5. Recovery for users locked-out

**Scenario**: Wave 39 enabled, user kills VPNRouter via Task Manager
(app crash, SIGKILL, etc.). Do firewall rules linger and break
all DNS?

**Yes — without cleanup, the user's DNS is permanently broken until
they manually delete the rules or restart VPNRouter cleanly.**
This is THE critical UX risk.

**Cleanup paths**:

1. **Process exit handler** — `FirewallManager.Dispose()` calls
   `DeleteAllRules()`. For Wave 39, the new `DisableDnsLockdownAsync`
   should ALSO be called on Dispose. Issue: Dispose only fires on
   graceful shutdown. A `Process.Kill()` from Task Manager does NOT
   call Dispose. Same risk exists for existing
   `VPNRouter_Block_*` rules — they linger if app crashes mid-VPN
   with `block_on_vpn_fail` engaged.
2. **CleanupOrphanedRules at app boot** — Agent C's test
   `CleanupOrphanedRules_AlsoRemovesDnsLockdownRules` pins this:
   on next VPNRouter launch, any `VPNRouter-DnsLockdown-*` rule
   from a prior session is detected by `FindRulesByPrefix` and
   deleted. **This is the user's recovery path.**
3. **What if user can't launch VPNRouter?** (e.g. uninstalled it
   while rules are active). They'd need to manually run
   `netsh advfirewall firewall delete rule name="VPNRouter-DnsLockdown-UDP53"`
   etc. **Recommendation**: ship a `repair.cmd` script in the
   installer (similar to existing helper.cmd) that wipes all
   `VPNRouter-*` firewall rules. v2.31.8-r10 already established
   this pattern for the helper.cmd CMD-parser fix.
4. **Service-mode cleanup** — when running as Windows Service,
   `Service.OnStop()` should also call DisableDnsLockdown. Verify
   in `VPNRouterService.cs` that the service lifecycle hook is wired.

**Critical**: extend `CleanupOrphanedRules` to handle BOTH the
`VPNRouter_Block_` prefix (existing) AND `VPNRouter-DnsLockdown-`
prefix (new). The test pin enforces this.

### 6. Test-mode bypass

**Concern**: would Wave 39 break our existing `SingBoxCheck`
integration tests that run sing-box on loopback?

**No.** sing-box check is a config validator, not a network listener
— it doesn't bind to port 53. The integration tests that run
sing-box.exe to validate generated JSON don't open DNS sockets;
they parse + dry-run the config and exit. Wave 39's firewall rules
are scoped to outbound DNS connections, not inbound sing-box
operations. Safe.

The `Generate_FromSubscribeMode_PassesSingBoxCheck` test is the one
sing-box live-spawn test in our suite — verified it does NOT do any
DNS resolution, only `sing-box check -c <file>` which is filesystem-
only.

### 7. Service mode

**LocalSystem privileges** — when running as a Windows Service
(`VPNRouter.Service.exe`), the process runs as `LocalSystem` (or a
configured service account). LocalSystem has full firewall admin
rights — netsh commands work. OK.

**User-mode CLI** — when running `VPNRouter.CLI.exe start` as a
regular user, netsh requires elevation. The CLI already does
`AdminHelper.IsAdmin()` check at startup and refuses to start
without elevation. Wave 39 doesn't change this requirement — same
posture. OK.

**Desktop App (`VPNRouter.App.exe`)** — runs as the logged-in user
by default. The App talks to the Service via IPC; the Service
performs all privileged operations including netsh. So Wave 39's
netsh calls happen in the Service context, not the App context.
If a user runs the App without Service installed (rare but possible),
the App falls back to running its own sing-box process — and in
that case it would need elevation to call netsh. Verify
`MainWindowViewModel` checks `AdminHelper.IsAdmin()` before enabling
the DNS lockdown toggle. **Agent B's UI work should handle this**
(grey out the toggle + tooltip "requires elevation" if non-admin).

### 8. Bonus: ordering vs. existing `VPNRouter_Block_*` rules

The existing `block_on_vpn_fail` subsystem uses rules named
`VPNRouter_Block_*` (underscore) — Wave 39 uses
`VPNRouter-DnsLockdown-*` (hyphen). Different prefixes ensure
`FindRulesByPrefix` enumeration doesn't accidentally match both.
Naming choice is good.

**However**: when both subsystems are active (Wave 39 lockdown +
existing `block_on_vpn_fail`), Windows Firewall evaluates rules in
lexically-sorted name order. The Wave 39 loopback-allow rule
(prefix `0_VPNRouter-` or unprefixed `VPNRouter-`) needs to sort
BEFORE both the Wave 39 block rules AND the existing
`VPNRouter_Block_*` rules to guarantee loopback DNS works. The
`0_` prefix is safer than relying on hyphen-vs-underscore ASCII
ordering (hyphen 0x2D < underscore 0x5F, so `VPNRouter-` sorts
before `VPNRouter_` naturally — but `0_` makes it explicit and
robust against future renames).

### Summary for Agent A

Wave 39 implementation is sound. Recommendations:

1. Use `0_VPNRouter-DnsLockdown-LoopbackAllow` (sort-first prefix)
   for the allow rule rather than relying on name ordering.
2. Add UDP/853 to the block set (defence-in-depth for DoQ).
3. Extend `CleanupOrphanedRules` to sweep BOTH prefixes
   (test pin enforces this).
4. Wire `DisableDnsLockdown` into the Service's `OnStop` lifecycle
   AND the App's `Dispose` path.
5. Ship a `repair.cmd` in the installer to wipe rules if user
   ever locks themselves out post-uninstall.
6. Tooltip warning: "Disable if you use another VPN client
   simultaneously" + "Local DNS proxies must bind to 127.0.0.1".
