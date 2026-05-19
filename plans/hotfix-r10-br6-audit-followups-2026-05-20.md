# v2.35.0-r10 — BR-6: audit follow-ups (HealthMonitor race + Serilog wire)

## Brief

Two concrete fixes from the r9 audit. No new behaviour; closes
correctness gaps in the r5-r9 chain. See
`plans/hotfix-r9-br5-stop-timing-dns-default-2026-05-19.md` for the
r9 fixes that this iteration refines.

## What got fixed

### BR-6a — close HealthMonitor false-restart race in VpnEngine.Stop

r9's Stop reorder put `_singBox.Stop()` second, but left
`_healthMonitor.Stop()` after the DnsHardening + ETW + firewall
cleanup. That created a ~50-200 ms window where:

1. `_singBox.Stop()` already killed the sing-box process
2. `_healthMonitor` still running, periodic timer can fire
3. `OnHealthTick` sees `!isHealthy && _vpnWasRunning` → calls
   `AttemptRestart()` → **false restart of the VPN immediately after
   the user pressed Stop**

The relevant branch in `HealthMonitor.OnHealthTick` doesn't check
`_isStopping`, so the only safe ordering is "stop the monitor before
killing its target". `HealthMonitor.Stop()` is fast (~ms — just
disposes a `System.Threading.Timer`) so the user-visible disconnect
window is unchanged.

New order:

```csharp
try { _probeCts?.Cancel(); } catch { }
try { _healthMonitor?.Stop(); } catch { }  // BR-6a: BEFORE sing-box
try { _singBox?.Stop(); } catch { }
#if PLATFORM_WINDOWS
try { WindowsDnsHardening.Restore(_logger); } catch { }
#endif
try { _etw?.Stop(); } catch { }
// ... firewall cleanup
```

Race probability: timer interval is 30 s by default, so the race
window was ~0.7% per Stop. Real, just rare. Closes it deterministically.

### BR-6b — wire static `Serilog.Log.Logger` in Program.cs

Audit revealed that **r7's BR-3 (SettingsLoader diagnostic Serilog
mirror) was non-functional in the App.exe host**. The mirror called
`Serilog.Log.Logger?.Information(line)`, but no code anywhere in
`VPNRouter.App` assigned `Log.Logger`. It stayed at the SilentLogger
default, so the call was a silent no-op.

Confirmed via:

```bash
$ grep -r "Serilog.Log.Logger\s*=" VPNRouter.App/ --include="*.cs"
(no matches)
```

And via brat's 23:29-23:33 log: zero `[SettingsLoader] Loaded …`
lines despite r7+ being installed.

Fix: assign `Log.Logger` early in `Program.Main` (after admin
elevation, before any code that would trigger
`SettingsLoader.Load`). Uses the same File + Console sink as the
MainWindowViewModel instance logger, so they write to the same
`vpnrouter*.log` file. Serilog's `WriteTo.File` supports concurrent
writers from one process — no lock contention.

Side effect: every existing `Serilog.Log.Logger?` call in the App
(line 295 SingleInstance, line 310 FirewallManager, App.axaml.cs
LockFile) now actually logs. Previously those were all silent. The
log volume will increase slightly — same logger config writes to the
same file, no spam.

## Verification

- `dotnet build -c Release` — 0 errors
- `dotnet test` — **1174/1178 pass / 0 fail / 4 skip**
- No new tests (BR-6a is a code reorder pinned by behavior; BR-6b is
  observability)

## Risk

LOW for both:
- BR-6a is a 2-line swap; HealthMonitor.Stop is sync and fast.
- BR-6b adds a Log.Logger assignment in a try/catch; failure path
  falls through to SilentLogger (= pre-r10 behaviour).

## Carry-over

Ships on top of r9 (BR-5a Stop reorder + BR-5b DnsLeakLockdown
default-on) + r8 (BR-4 orphan cleanup preserves active server) +
r6 (BR-1 F-12 softening + BR-2 NetAdapter cache + BR-3 load-state
diagnostic, NOW actually functional).

## Open items from the audit (deferred)

- **brat's manual=2 → manual=0 mystery**: still unexplained. r4-r9
  fixed adjacent issues; the actual mechanism for the first-r5-read
  wipe remains unidentified. BR-6b (functional BR-3) should give the
  next user-report investigation visibility into the on-load
  `vless.servers` count.

- **DnsLeakLockdown default flip-flop risk**: r5 set false, r9 set
  true. If LAN-DNS-proxy users complain, we may flip back. The
  principled fix (auto-detect LAN proxy and default accordingly)
  is deferred — not blocking.

- **95.85.16.212 verification**: I claimed it's a Russian ISP DNS in
  r9 brief without geolocation lookup. Could be a Cloudflare RU PoP.
  Honest re-read pending user verification.

## What user does

**Nothing required.** Update normally.
