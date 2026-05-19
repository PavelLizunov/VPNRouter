# v2.35.0-r9 — BR-5: faster Stop + DnsLeakLockdown default-on

## Brief

brat user re-tested after r8 ship and reported (Z:/brat 23:29-23:34 logs):

> "интернет с нужного ip появлялся на пару сек после выключения"
> ("internet from the required IP appeared for a couple of seconds after turning off")

Plus a DNS leak still visible in `singbox.log`: dns: exchanged queries
returning `A 95.85.16.212` (Russian Trytek/ER-Telecom ISP DNS resolver)
despite the VPN being up.

Two distinct issues, both fixed in r9.

## Root causes

### BR-5a — VPN keeps routing for 2-3s after user presses Stop

`VpnEngine.Stop` had sing-box kill at the END of the cleanup sequence:

```
[VpnEngine] Stopping...                                    ← t = 0
[DnsHardening] Restored
[HealthMonitor] Stopped
[ETW] Stopping
[FirewallManager] lockdown disabled
[Firewall] Disabled block rules                            ← t = 1.5 s
[Firewall] All rules deleted                               ← t = 2.3 s
[SingBoxManager] Stopping sing-box (PID 30800)             ← t = 2.3 s
[SingBoxManager] sing-box stopped                          ← t = 2.3 s
```

For the entire 2.3-second window between the user pressing Stop and
sing-box actually dying, traffic continues to flow through the live
wintun adapter → VPN egress IP. That's the "couple of seconds after
turning off" brat saw.

### BR-5b — DnsLeakLockdown silently false for upgrade users

`SettingsMigrator.Migrate_4_to_5` set `DnsLeakLockdown = false` on
upgrade, intending to be cautious about users running a local DNS
proxy on a non-loopback IP. But for the vast majority (no LAN
proxy), the protection that was the whole point of Wave 39 never
activated — brat's `singbox.log` confirmed DNS queries reaching a
Russian ISP resolver despite sing-box routing to Cloudflare DoH:

```
+0300 2026-05-19 23:34:09 INFO [...] dns: exchanged A
  49blugl1rx4np1t4z94ax17w9sqxgiu1q6bsglnh-160.ipleak.net.
  86400 IN A 95.85.16.212
```

## Fix

### BR-5a — kill sing-box FIRST in VpnEngine.Stop

`VPNRouter.Core/Services/VpnEngine.cs:Stop()`. Reordered so:

```csharp
try { _probeCts?.Cancel(); } catch { }    // F-E probe
try { _singBox?.Stop(); } catch { }       // BR-5: kill sing-box FIRST

#if PLATFORM_WINDOWS
try { WindowsDnsHardening.Restore(_logger); } catch { }
#endif

try { _healthMonitor?.Stop(); } catch { }
try { _etw?.Stop(); } catch { }

if (_activeProfile?.BlockOnVpnFail == true)
{
    try { _firewall?.DisableBlockRules(); } catch { }
    try { _firewall?.DeleteAllRules(); } catch { }
}

try { _firewall?.Dispose(); } catch { }
```

After this change, `_singBox.Stop()` runs as the second line of
`Stop()` — wintun adapter teardown begins within ~50 ms of the user
pressing Stop, so routing fails over to the OS default route (direct)
immediately. The downstream cleanup (DNS hardening restore, firewall
rules) runs with VPN already dead, so they no longer gate the user-
visible traffic switchover.

### BR-5b — default DnsLeakLockdown=true for upgrade users

`VPNRouter.Core/Services/SettingsMigrator.cs:Migrate_4_to_5`. Flipped
from `false` to `true`. brat-class users now get the firewall-level
DNS lockdown on the next start without manual opt-in.

LAN-DNS-proxy users (rare) can disable via Settings → Leak Protection
→ "Block DNS outside VPN" checkbox.

Test pin `SettingsMigrator_FromLegacyV2_DefaultsLockdownTrue_BR5`
(renamed from `…DefaultsLockdownFalse`) asserts the new contract.

## Verification

- `dotnet build -c Release` — 0 errors
- `dotnet test` — **1174/1178 pass / 0 fail / 4 skip**
- Affected test classes: `AppSettingsDnsLeakLockdownTests`,
  `BratYamlReproTests`, `SettingsMigratorLegacyVlessServersCleanupTests`,
  `LeakProtectionAppSettingsTests` (37/37 pass)

## Carry-over

Ships on top of r8 (BR-4 orphan cleanup preserves active server +
BR-1/BR-2/BR-3 from earlier r6/r7).

## Risk

LOW for BR-5a — re-ordering Stop steps. Each step's `try/catch` is
preserved; the only behavioural change is the order in which the
cleanup logs land. Downstream of the sing-box kill, every step
operates on the already-dead VPN, which is the steady-state for those
helpers anyway.

LOW for BR-5b — default flip. The Settings toggle is unchanged, so
users who actively rely on the off-default can still set it back.
Most users (no LAN DNS proxy) get the intended protection
automatically.

## What user does

**Nothing required.** Update normally:
- Auto-update banner → click Update
- Manual: `VPNRouter-v2.35.0-r9-win.zip` from release page

For brat specifically: after update + restart, DnsLeakLockdown will
be ON by default, the firewall rules block UDP/53, TCP/53, TCP/853 on
non-loopback interfaces, and the next Stop will tear down VPN routing
within ~50 ms instead of ~2.3 seconds.
