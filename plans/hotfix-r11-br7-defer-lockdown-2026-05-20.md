# v2.35.0-r11 — BR-7: defer DnsLeakLockdown until TUN warm-up succeeds

## Brief

brat re-tested r10, internet broke, rolled back to v2.32.2 stable
(again). 6th iteration. New logs in Z:/brat/ — diagnosis below.

## What brat reported

> "Положил новые логи, снова пришлось откатываться к сабильной
> версии чтоб заработал интернет Z:\brat"

## Root cause

The new BR-3 diagnostic line (BR-6b r10 wire-up — now actually
working!) gave us visibility into the post-load `AppSettings` state.
That ruled in / out the previous suspects:

```
01:00:49 [SettingsLoader] Loaded …: schema=5, config_mode=subscribe,
  subs=1[+0], vless.servers=0, vless.server=empty, …
01:00:49 [Subscription] Fetched 2 servers from ninitux.com   ← provider returned only 2 now
01:00:55 [VpnEngine] sing-box started (PID 20416)
01:00:55 [DnsHardening] DnsLeakLockdown enabled — installing firewall rules in background
01:00:55 [FirewallManager] DNS leak lockdown enabled — UDP/53, TCP/53, TCP/853 blocked on non-loopback interfaces
01:01:51 [HealthMonitor] VPN is up
01:01:54 [WRN] [StartupPipeline] TUN warm-up failed after 33009ms
```

The killer is the 33-second gap between sing-box starting and the
warm-up probe succeeding. During this window:

1. `WindowsDnsHardening.Apply` installs firewall blocks on UDP/53,
   TCP/53, TCP/853 on every non-loopback interface (Ethernet).
2. The warm-up HTTP probe to `gstatic.com` needs to resolve the
   host first. .NET's `HttpClient` calls Windows resolver →
   needs DNS → blocked on Ethernet.
3. TUN adapter is still bringing itself up on brat's slow Win11
   LTSC, so DNS through TUN → sing-box → DoH isn't routing either.
4. DNS resolution times out. Probe fails. Retry 15 times × ~2 s
   each = 33 s of broken DNS, broken internet.
5. After 33 s, TUN finally routes properly, but by then the user
   has hit the rollback button.

We DoS'd ourselves by enabling the lockdown before TUN was
demonstrably routing.

## Fix

`WindowsDnsHardening.Apply` no longer installs the firewall lockdown
inline. The registry + TUN-metric hardening still runs immediately
(those don't break DNS). The firewall lockdown is extracted to a new
`WindowsDnsHardening.EnableLockdownIfConfigured` method, called by
`StartupPipeline.ScheduleWarmupProbe` from the **success branch only**:

```csharp
// Inside ScheduleWarmupProbe after the successful HTTP probe:
await http.GetStringAsync("https://www.gstatic.com/generate_204", ct);
_host.Logger?.Information("[StartupPipeline] TUN ready after {Ms}ms …");
// BR-7: arm the lockdown ONLY now that TUN is confirmed routing.
WindowsDnsHardening.EnableLockdownIfConfigured(settings, _host.Logger);
```

On warm-up failure (15 attempts × ~2s = ~30s total): lockdown is
**intentionally NOT installed**. The user keeps internet (DNS leak
risk noted in the warning log). Better than 33 s of zero internet.

## Behaviour matrix

| TUN warm-up | Before r11 | After r11 |
|---|---|---|
| Fast (<1 s, typical) | Lockdown on, DNS protected | Lockdown on, DNS protected (≤1 s later) |
| Slow (brat, 33 s) | Lockdown on, DNS blocked for 33 s, user perceives "no internet" | Lockdown on after warm-up succeeds → no broken-DNS window |
| Failed (>30 s) | Lockdown on forever, DNS broken until VPN stopped | Lockdown NOT installed; user has internet with DNS leak risk |

## Verification

- `dotnet build -c Release` — 0 errors
- `dotnet test` — **1174/1178 pass / 0 fail / 4 skip**

## Risk

LOW. The lockdown is moved from an inline call to a deferred one;
the same `FirewallManager.EnableDnsLockdownAsync` runs from the same
fire-and-forget `Task.Run` pattern. Failure modes are unchanged.
The Restore path (`WindowsDnsHardening.Restore` → `DisableDnsLockdownAsync`)
is idempotent and still runs unconditionally on Stop.

## What user does

**Nothing required.** Update normally.

For brat specifically: after r11 the warm-up probe will succeed
(no self-DoS), and once TUN is confirmed routing the firewall
lockdown will install behind the scenes. He should regain immediate
internet access AND keep the DNS leak protection.

## Carry-over

Ships on top of r10 (BR-6a HealthMonitor race + BR-6b Serilog wire),
r9 (BR-5a Stop reorder + BR-5b DnsLeakLockdown default-on),
r8 (BR-4 orphan cleanup preserves active server), and r5 (Wave 39
firewall DNS lockdown infrastructure).

## Audit honesty

The r9 audit flagged loop risk on flip-flopping `DnsLeakLockdown`
defaults. r11 does NOT flip the default again — it keeps r9's
opt-in-for-upgrade-users (default true) BUT changes WHEN the
lockdown installs. The design is now: "default-on, but only when
the VPN routing is actually working". Resolves the loop principal:
the original tension was "protect everyone vs surprise LAN-proxy
users", and r11 sidesteps it by adding a TUN-ready gate that
benefits both groups (no broken-internet panic for either).

The brat-specific 33-second TUN warm-up remains a separate issue
— wintun init on his Win11 LTSC is unusually slow. r11 stops us
from making it worse; we don't speed it up. That's a deferred
investigation.
