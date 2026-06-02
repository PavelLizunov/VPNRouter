# Android no-doze hardening — proactive battery-opt prompt + onTaskRemoved recovery

**Date**: 2026-06-02 · **Risk**: MEDIUM (Android service lifecycle + a new
system-dialog trigger) · **Branch**: main (2.x reliability, not a v3.0 refactor)

## Why

User's hard constraint: *"приложение не должно попадать в телефоне по дефолту в
режим энерго эффективности, как будто оно будет кешироваться и выключаться раз в
несколько минут-часов"*. Read-only device probe (KYOCERA A101BM / BALMUDA Phone,
Android 12 / SDK 31, via Mac SSH adb) confirmed the real-world gap:

```
dumpsys deviceidle whitelist → com.ninitux.vpnrouter NOT present
```

The no-doze *foundations* already exist (v2.32.0 AND-NETRES): `START_STICKY`,
`startForeground` with `FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED` (API 34+), a
60s connect `WakeLock`, and an `ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`
deep-link. But the exemption request is **buried** in Settings → Reliability
(`OnReliabilityBatteryClicked`), so a normal user never grants it — and without
the exemption the OS Doze bucket caches + kills the service, exactly the symptom
the user described.

Two gaps remain:
1. **No proactive ask.** The exemption is the single most important no-doze
   lever and it's hidden behind two taps in Advanced settings.
2. **No swipe-away recovery.** Aggressive OEMs (KYOCERA/BALMUDA, Xiaomi, etc.)
   call `stopService` when the user swipes the app from Recents. `START_STICKY`
   only covers a *memory-pressure* kill, NOT an explicit `stopService`, so the
   tunnel dies on swipe with no recovery. There is no `onTaskRemoved` override.

## What

| File | Change |
|---|---|
| `AndroidStorage.cs` | New `battery_opt_prompt_shown` bool flag (`Get/SetBatteryOptPromptShown`, default false). |
| `AndroidApp.Permissions.cs` | Extract grant-intent into `RequestBatteryOptimizationExemption()`; add `MaybePromptBatteryOptimizationExemption()` = fire the native grant dialog ONCE when not exempt AND not-yet-prompted, then set the flag. |
| `AndroidApp.VpnLifecycle.cs` | Call `MaybePromptBatteryOptimizationExemption()` from `UpdateConnectionState(connected=true)` — the first successful connect is the highest-intent moment to ask. |
| `VpnRouterService.java` | (a) wrap `startForeground` in try/catch → on a refused background-FGS start, broadcast `foreground-start-blocked` + `stopSelf` instead of crashing; (b) override `onTaskRemoved` → if tunnel active AND battery-opt exempt, schedule a ~1.5s `AlarmManager` self-restart (`ACTION_RESTART` → existing last-good-config restore branch); when not exempt, log only (can't legally restart from bg). New `ACTION_RESTART` const + `isIgnoringBatteryOptimizations()` helper + `AlarmManager`/`SystemClock` imports. |

Native system dialog = no new localized strings; the OS provides the text. VPN
is a documented qualifying use case for the exemption request (sideload/F-Droid,
not Play, so policy is moot anyway).

## How (sequence)

1. Connect tap → `RequestConnect` (unchanged) → tunnel up → `IntentChanged(true)`
   → `UpdateConnectionState(true)` → `MaybePromptBatteryOptimizationExemption()`.
   First time only: native grant dialog. User grants → exempt.
2. With exemption granted, `onTaskRemoved` can legally `AlarmManager.set` a restart
   PendingIntent → service recreated via the null/`ACTION_RESTART` branch →
   `loadLastGoodConfig()` → `startTunnel()`. The battery exemption is also what
   lets that background FGS start succeed on Android 12 — FIX#1 and FIX#2 are
   synergistic.

## Verification gate

- [ ] Gate 1 build: `dotnet build VPNRouter.Android … /p:EnableAndroidTarget=true` 0 errors.
- [ ] Gate 1b: `AndroidAppCharacterizationTests` re-pinned (new private members change the source-surface SHA).
- [ ] Gate 2 tests: full suite green.
- [ ] Gate 5 device-verify (A101BM via Mac SSH adb):
  - first connect fires the battery dialog;
  - after grant, `dumpsys deviceidle whitelist` shows the package;
  - connect with test subscription, `dumpsys deviceidle force-idle` → FGS + tunnel survive;
  - swipe from Recents → tunnel recovers (logcat shows the scheduled restart).
- [ ] Gate 3 docs: VPNRouter.Android/CLAUDE.md no-doze note updated.

## Risk / rollback

MEDIUM — touches the FGS start path. Mitigation: startForeground guard is
strictly additive (catches a previously-uncaught throw); onTaskRemoved restart
is gated on `boxService != null && exempt` so it can't fire spuriously; the
prompt is once-ever and reuses the existing, already-shipped grant deep-link.
Rollback: `git revert` the commit; no persisted-state migration to undo (the new
flag is read-with-default).
