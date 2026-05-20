# v2.35.0-r12 — BR-8: allow TUN DNS endpoint in Wave 39 firewall lockdown

## Brief

brat reported r11 works "only if checkbox 'Block DNS outside VPN' is
unchecked". That's a partial-fix outcome — the lockdown still breaks
DNS when on, even with r11's deferred install.

## Root cause

The Wave 39 block rule (r5):
```
netsh advfirewall firewall add rule
  name=VPNRouter-DnsLockdown-UDP53
  dir=out action=block protocol=UDP remoteport=53
  enable=yes profile=any
```

is **unscoped**. It blocks every UDP/53 outbound packet — including
queries Windows DNS Client sends to sing-box's TUN endpoint
(`172.19.0.2:53` per the bundled `Tun.Ipv4Address=172.19.0.1/30`).
The TUN-bound DNS doesn't leak (it's headed straight into sing-box,
which forwards via VLESS+DoH), so blocking it accomplishes nothing
useful and breaks resolution for the apps that depend on it.

The original Wave 39 brief identified the threat as the Windows DNS
Client racing ISP resolvers in parallel despite SMHNR / ParallelAAAA
hardening — those queries go OUT through Ethernet/Wi-Fi, not through
TUN. Banning UDP/53 globally was over-broad and the implementation
gap only surfaced now that r10 made the diagnostic line visible
(BR-3 actually surfaces in logs) and r11 stopped DoS'ing the warm-up
probe.

## Fix — BR-8

Add a second allow rule alongside the existing loopback exception,
scoped to the TUN /30 range (derived from `settings.Tun.Ipv4Address`):

```
netsh advfirewall firewall add rule
  name=0_VPNRouter-DnsLockdown-TunAllow
  dir=out action=allow
  protocol=UDP remoteip=172.19.0.0/30 remoteport=53,853
  enable=yes profile=any
```

Plus a TCP twin (`-TCP` suffix) for the TCP/53 + TCP/853 cases. The
leading `0_` prefix sorts both allow rules to the top of the Windows
Firewall UI list so the install order is visible at a glance.

Windows Firewall outbound semantics: **Allow takes precedence over
Block when both match**. So this allow wins for TUN-bound DNS while
the block still wins for any ISP-bound or public-resolver-bound DNS
that would leak.

`DisableDnsLockdownAsync` updated to also delete the two new rules
(7 rules total, was 5).

## Behaviour matrix

| Destination | Rule match | Effective action |
|---|---|---|
| 127.0.0.1:53 (loopback DNS proxy) | LoopbackAllow + UDP53Block | Allow |
| 172.19.0.2:53 (TUN DNS endpoint) | TunAllow + UDP53Block | Allow ← BR-8 |
| 8.8.8.8:53 (Google ISP) | UDP53Block | Block ← intended |
| Russian ISP DNS | UDP53Block | Block ← Wave 39 target |

## Verification

- `dotnet build -c Release` — 0 errors
- `dotnet test` — **1187/1191 pass / 0 fail / 4 skip** (13 new BR-8
  parsing tests in `FirewallManagerTunAllowTests`)
- `NormalizeTunAllowIp` parsing helper pinned for the bundled
  `172.19.0.1/30` and edge cases (bare IP, malformed, IPv6 rejection).

## What user does

**Nothing required.** Update normally:
- Auto-update banner → click Update
- One-liner: `iwr -useb https://vpn.ninitux.com/install.ps1 | iex`
- Manual: `VPNRouter-v2.35.0-r12-win.zip` from release page

For brat specifically: after r12, with the lockdown checkbox ON,
DNS continues to work through sing-box's TUN endpoint while
real-interface leaks remain blocked. No more checkbox-juggling.

## Risk

LOW. Two new allow rules added; existing block rules unchanged.
Restore path tears down all 7 rules. New unit tests pin the
parsing logic for the rule scope.

## Carry-over

Ships on top of r11 (BR-7 deferred lockdown), r10 (BR-6a HealthMonitor
race + BR-6b Serilog wire), r9 (BR-5a Stop reorder + BR-5b lockdown
default-on), r8 (BR-4 orphan cleanup), r5 (Wave 39 infrastructure).

## Audit honesty

The Wave 39 design from r5 was structurally incomplete: the block
rules needed an allow exemption for sing-box's own TUN DNS endpoint
and never got one. r6 / r7 / r8 / r9 / r10 / r11 fixes addressed
adjacent issues (F-12, NetAdapter, orphan cleanup, Stop timing,
DnsLeakLockdown default, HealthMonitor race, Serilog wire,
deferred lockdown) without catching the rule-scope bug because we
were chasing user-reported symptoms rather than re-auditing the
Wave 39 spec.

r12 closes that loop. The DnsLeakLockdown default-on (BR-5b) and
deferred-install (BR-7) are kept — both are correct given the now-
properly-scoped rules. No flip-flop.
