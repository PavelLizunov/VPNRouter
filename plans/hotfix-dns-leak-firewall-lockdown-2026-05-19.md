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
