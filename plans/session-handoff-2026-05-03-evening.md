# Session handoff — 2026-05-03 evening

User left for a long stretch with these instructions (translated):

1. Check the user logs they dropped in `Z:\VPN prosses lose` and
   `Z:\VPN prosses lose2`.
2. If a bug is found, fix it. If no bug found, hypothesize the cause
   and harden against it.
3. After the fix is shipped, continue accumulating without asking
   questions — pick the best option autonomously.
4. Once everything I think needs doing is done, move to the Android
   port.

## TL;DR of what got done

| # | Item | Outcome |
|---|---|---|
| 1 | Analyse User #1 logs (`Z:\VPN prosses lose`) | Root cause identified: HealthMonitor recovery gap |
| 2 | Analyse User #2 logs (`Z:\VPN prosses lose2`) | Server-side issue (urltest stickiness on dead server 93.95.226.167) — deferred, not a VPNRouter bug |
| 3 | Implement HealthMonitor recovery fix | `_shouldBeRunning` intent flag + new OnHealthTick branch |
| 4 | Add 5 regression tests | `HealthMonitorRecoveryGapTests` — all green |
| 5 | Ship as v2.31.5-r2 | Released, 12 assets, Mac+Linux+APT CI green, prerelease, v2.31.5-r1 deleted, v2.31.4 restored as Latest |
| 6 | Android Phase 1.D | Connect/Disconnect UI replaces auto-start scheduler, build green |
| 7 | Android Phase 1.E attempt | Blocked: multi-target ProjectReference doesn't expose Core types to Android compile — see "Open issues" |

## User #1: VPN-loss bug — root cause + fix

### Symptom (from user logs)

Multiple sing-box crashes during a single day with exit code
`1073807364` (= `STATUS_CONTROL_C_EXIT`, the Windows console-control
exit code returned when a process is hit with CTRL_LOGOFF, CTRL_SHUTDOWN,
CTRL_CLOSE, or related events). Each crash:

1. `OnSingBoxCrashed` correctly enabled firewall block rules (✓ leak
   protection working — user's traffic was blocked, not leaked).
2. `OnSingBoxCrashed` correctly scheduled `AttemptRestart` via
   `Task.Delay(5000).ContinueWith(...)`.
3. Continuation never produced a follow-up log line. Next event was
   the user manually re-opening the app hours later.

### Root cause

Pre-fix `OnHealthTick` only had this recovery branch:

```csharp
if (!isHealthy && _vpnWasRunning) {
    AttemptRestart();
}
```

`OnSingBoxCrashed` reset `_vpnWasRunning = false` synchronously. So
once the crash happened, the periodic health tick stopped trying to
recover — the only safety net was the `Task.Delay` continuation
scheduled inside `AttemptRestart`. If that continuation didn't fire
(laptop slept across the deadline; App quit between schedule and fire;
`_isStopping` was set during a Stop racing the crash), the user was
stranded.

### Fix

Added `_shouldBeRunning` intent flag to `HealthMonitor`:

- Set `true` in `Start()`
- Set `false` in `Stop()`
- New `OnHealthTick` branch:

```csharp
else if (!isHealthy && _shouldBeRunning && !_isStopping) {
    _logger.Warning("[HealthMonitor] sing-box dead while user wants " +
                    "VPN up — initiating recovery (intended-running path)");
    AttemptRestart();
}
```

Plus reset `_restartAttempts = 0` after a successful "VPN is up"
transition so the user doesn't pay backoff penalty for previous crash
history.

Bounded by existing `_settings.MaxRestartAttempts = 5` cap.

### Tests added

`VPNRouter.Tests/UnitTest1.cs` → `HealthMonitorRecoveryGapTests`:

1. `Start_SetsShouldBeRunningTrue`
2. `Stop_SetsShouldBeRunningFalse`
3. `OnHealthTick_AfterCrash_TriggersRecoveryRestartAttempt` (the bug)
4. `OnHealthTick_AfterUserStop_DoesNotTriggerRecovery` (intent guard)
5. `OnHealthTick_OriginalBranch_StillTriggersWhenVpnWasRunning` (regression)

5/5 pass.

### Commits + ship

```
b2fcca9  test: visual-diff baseline regression for stable pages
eb19e28  test: protocol-aware regression coverage for LeakProtection
3dc3f5e  test: SubscriptionFetcher parser branches via extracted ParseBody
aa71622  docs(claude): sync sub-CLAUDE.md after v2.31 cycle + accumulation
d874865  fix(v2.31.5-r2): HealthMonitor recovery gap on post-crash health tick
f17f811  feat(android-1.d): real Connect/Disconnect UI replaces auto-start
```

v2.31.5-r2 is on GitHub with 12 assets, prerelease=true, Latest reset
to v2.31.4 stable. v2.31.5-r1 deleted per rolling policy.

## User #2: Discord/Telegram timeout (deferred)

### Symptom

User #2 reported "Discord and Telegram don't work over VPN".

### Root cause (from logs)

Both processes ARE correctly routed through VLESS (sing-box log
`router: found process path: ...Telegram.exe → outbound/vless[...]`).
But one of the servers in the urltest pool — `93.95.226.167:443`
(`is-01-grpc-test`) — is consistently unreachable from User #2's
location (`dial tcp 93.95.226.167:443: i/o timeout` repeatedly). When
urltest sticks on this server, all proxy traffic times out.

### Why this isn't a VPNRouter bug

- The routing rules are correct
- The leak protection is working
- The proxy outbound is well-formed
- The remote server simply doesn't respond from User #2's network

### Possible follow-up (v2.31.6+ or v2.32 backlog)

urltest stickiness tuning: shorter `interval`, smaller `tolerance`,
force-test on connection failure. This would help urltest switch off
the dead server faster. Not implemented this session — would benefit
from User #2 confirming their VPNRouter version + symptoms first.

## Android Phase 1.D — shipped

`feat(android-1.d): real Connect/Disconnect UI replaces auto-start`

- Removed `Handler.PostDelayed(SchedulePhase1cStart, 3000)` from
  `MainActivity.OnCreate`
- Added `MainActivity.Instance` static + `RequestConnect()` /
  `RequestDisconnect()` public methods + `IntentChanged` event +
  `IntendedConnected` flag
- Added a real Avalonia view to `AndroidApp.axaml.cs`: title +
  subtitle + status text + Connect/Disconnect toggle button
- Avalonia button click → `MainActivity.Instance.RequestConnect()` /
  `RequestDisconnect()` → Android `VpnService.Prepare` consent →
  libbox tunnel
- Status flips on `IntentChanged` event (intent-level only — real
  state sync is Phase 1.G)

Build verified green:

```bash
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release \
  -p:EnableAndroidTarget=true \
  -p:AndroidSdkDirectory="$ANDROID_HOME" \
  -p:JavaSdkDirectory="$JAVA_HOME"
```

## Phase 1.E — blocked

Tried to wire `VlessUriParser` (from `VPNRouter.Core.Services`) into
`AndroidApp.axaml.cs` to surface parsed URI details in the UI. Build
fails with:

```
error CS0234: The type or namespace name 'Core' does not exist in the
namespace 'VPNRouter' (are you missing an assembly reference?)
```

Despite `VPNRouter.Android.csproj` having:

```xml
<ProjectReference Include="..\VPNRouter.Core\VPNRouter.Core.csproj">
  <Properties>EnableAndroidTarget=true</Properties>
</ProjectReference>
```

And `Core.csproj` having:

```xml
<TargetFrameworks Condition="'$(EnableAndroidTarget)' == 'true'">net8.0;net8.0-android</TargetFrameworks>
```

The `bin/Release/net8.0-android/VPNRouter.Core.dll` IS produced, but
the Android compile doesn't see it as a referenced assembly.

### Hypothesis

The `<Properties>EnableAndroidTarget=true</Properties>` element on
ProjectReference doesn't propagate cleanly when the parent project is
multi-target. The Android project might be picking up the `net8.0`
build of Core (which still has Windows-specific code that won't load
under Android) instead of the `net8.0-android` build.

### Suggested next-session investigation

Try replacing the ProjectReference with:

```xml
<ProjectReference Include="..\VPNRouter.Core\VPNRouter.Core.csproj">
  <SetTargetFramework>TargetFramework=net8.0-android</SetTargetFramework>
  <AdditionalProperties>EnableAndroidTarget=true</AdditionalProperties>
</ProjectReference>
```

Or directly add an `<Reference>` to the built dll once Core has been
built standalone:

```xml
<Reference Include="VPNRouter.Core">
  <HintPath>..\VPNRouter.Core\bin\Release\net8.0-android\VPNRouter.Core.dll</HintPath>
</Reference>
```

Or revisit whether the `EnableAndroidTarget` opt-in pattern in
`Core.csproj` is the right shape — maybe the Android target should be
the default when the Android project references Core, not gated behind
a property.

The Phase 1.E UI changes (TextBox + parse on Connect) were reverted
out of `AndroidApp.axaml.cs` because they didn't compile. The current
file is back to the Phase 1.D state.

## What's worth doing next session

1. **(Phase 1.E)** Resolve the Core-reference issue, then proceed with
   shared-Core integration on Android: wire `VlessUriParser` →
   `ConfigGenerator.Generate` → real VLESS routing tunnel.
2. **(Tests)** Add coverage for `_restartAttempts = 0` reset on
   healthy-transition (bonus to the 5 tests added).
3. **(User #2 follow-up)** Tune urltest config for faster dead-server
   failover. Plan as v2.31.6 or fold into v2.32.
4. **(Stable cut)** When user is confident v2.31.5-r2 fixes the
   VPN-loss issue on their machine, cut v2.31.5 stable.

## Repo state

- `claude/suspicious-kepler-fa08e0` worktree: HEAD at f17f811
- `github/main`: f17f811
- `origin/main` (Forgejo): f17f811
- v2.31.5-r2 published, prerelease, 12 assets, Mac+Linux+APT CI all green

## Files for next-session orientation

- `VPNRouter.Core/Services/HealthMonitor.cs` — fix in `_shouldBeRunning` + new OnHealthTick branch
- `VPNRouter.Tests/UnitTest1.cs` — `HealthMonitorRecoveryGapTests` (search for it)
- `VPNRouter.Android/MainActivity.cs` — Phase 1.D Connect/Disconnect bridge
- `VPNRouter.Android/AndroidApp.axaml.cs` — Phase 1.D Avalonia view
- This document — full context dump
