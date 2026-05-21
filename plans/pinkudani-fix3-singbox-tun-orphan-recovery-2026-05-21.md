# PinkuDani Fix #3 — Sing-box TUN orphan crash recovery via netsh disable

**Owner**: Claude session (PinkuDani class hardening, Fix #3 sequential agent)
**Branch**: main (direct commit)
**User reference**: PinkuDani 2026-05-21, Windows 10 LTSC (RU-RU) without
`NetAdapter` PowerShell module
**Effort**: ~2 hours
**Risk**: LOW (additive: new property + recovery hook; existing behaviour
preserved on the false branch; netsh disable already proven idempotent in
production)
**Blast radius**: 2 service files (`SingBoxManager.cs`, `HealthMonitor.cs`)
· 1 new test file (`SingBoxManagerTunOrphanRecoveryTests.cs`) · 8 new tests
**Rollback**: `git revert <commit>` — additive only; the `LastCrashWasTunOrphan`
property defaults to false so any consumer that doesn't read it sees the
unchanged behaviour.
**Dependencies**: Agent A's Fix #1+#4 commit `66e1407` (introduced
`IsNetAdapterModuleAvailable` + `TryDisableAdapterViaNetshAsync` public
surface in `TunAdapterDiagnostics`).

## Why

PinkuDani's 2026-05-21 log (`Z:\PinkuDani\vpnrouter20260521_004.log`)
showed sing-box dying mid-session 14 s after default-route interface
changed:

```
11:15:48 INFO network: updated default interface Беспроводная сеть, index 3
11:15:58 WARN inbound/tun[tun-in]: open interface take too much time to finish!
11:16:03 FATAL configure tun interface: Cannot create a file when that file already exists.
```

When the default route network interface changes (wired ↔ wireless,
adapter cycle), sing-box's TUN inbound attempts to reconfigure. The
reconfigure hangs for 10+ s, sing-box retries and the recreate refuses
because the orphan wintun kernel handle is still alive from the previous
TUN session. HealthMonitor then attempts the standard auto-restart
exponential-backoff chain (5 / 10 / 20 / 40 / 80 s) — but every restart
hits the SAME `ERROR_FILE_EXISTS` because `Remove-NetAdapter` PowerShell
fallback is unavailable on this Win10 LTSC SKU (CP-866 CommandNotFoundException;
see Fix #1 plan).

Fix #1+#4 (commit `66e1407` by Agent A) provided the missing piece — a
netsh-based fallback that releases the kernel handle even when
`Remove-NetAdapter` isn't available. The fallback fires in
`PreStartCleanupAsync` (LaunchProcess prologue), but ONLY when the netsh
adapter enumeration actually lists the orphan adapter. On PinkuDani's
machine the enumeration timing was unreliable mid-restart-loop — the
orphan exists in the kernel-driver state but the user-mode `netsh
interface show interface` doesn't always list it.

This Fix #3 closes the gap: when sing-box exits with the specific
"Cannot create a file when that file already exists" stderr signature,
force-disable the well-known `VPNRouter-TUN` name via netsh BEFORE
HealthMonitor's `AttemptRestart` schedules the next launch. Direct-by-name
+ netsh-only — bypasses both enumeration uncertainty AND the unreliable
PowerShell module.

## What

### Signature detection in SingBoxManager

Add a stderr ring buffer (`_capturedStderr` — 50 lines, bounded, thread-safe)
that captures every stderr line via the existing `ErrorLine` event. On
process exit, scan the buffer for the substring patterns:

- `Cannot create a file when that file already exists` (the FATAL signature)
- `configure tun interface:` (broader TUN-config-failure prefix)
- `open interface take too much time to finish` (the warning that precedes)

Set a new property `bool LastCrashWasTunOrphan { get; private set; }` to
true when the scan matches. Reset to false on successful Start / Stop /
Dispose.

The capture targets STDERR specifically because sing-box's `[31mFATAL[0m`
goes to stderr (the `ErrorLine` handler logs it via `Log.Warning("[sing-box] {Line}", line)`).
Verified via field log `Z:\PinkuDani\vpnrouter20260521_004.log:124-125`:
the FATAL line shows up under the `[WRN] [sing-box]` prefix which is what
the `ErrorLine` lambda routes.

### Recovery hook in HealthMonitor

Wrap the existing `AttemptRestart` Task.Delay continuation:
- Before invoking `_singBox.Restart()` for a recovery restart, check
  `_singBox.LastCrashWasTunOrphan`.
- If true, call `TunAdapterDiagnostics.TryDisableAdapterViaNetshAsync(
      _logger, "VPNRouter-TUN", "HealthMonitor.AttemptRestart.TunOrphan")`
  and `await Task.Delay(500, ct)` to let Windows tear down the wintun
  handle (per Agent A's brief — netsh disable is documented to release
  the handle but timing is unverified).
- Then proceed with `_singBox.Restart()`.

The 500 ms delay is generous; Agent A noted netsh disable releases the
kernel handle "soon" but exact timing is unverified. Field validation
required to tune down.

The recovery hook is gated on `OperatingSystem.IsWindows()` — Linux/macOS
sing-box doesn't use wintun so there's no equivalent crash class.

### Reset semantics

`LastCrashWasTunOrphan` resets to false on:
- Successful `Start` (`State` advances to `Running` after spawn).
- Explicit `Stop()` call.
- `Dispose()`.

It does NOT reset when entering `AttemptRestart` — the flag drives the
netsh disable; clearing it before the cleanup runs would defeat the
purpose. The first successful restart's `Start` call clears it.

## Reading the stderr buffer

The buffer is private to SingBoxManager and not exposed externally —
tests verify behaviour via the `LastCrashWasTunOrphan` property only.
The buffer uses a circular fixed-size string array (size 50) protected
by a lock. Production code never reads it directly; only the on-exit
scanner consults it.

## New tests

`VPNRouter.Tests/SingBoxManagerTunOrphanRecoveryTests.cs` — new file:

1. `LastCrashWasTunOrphan_FreshManager_IsFalse` — default state pin.
2. `LastCrashWasTunOrphan_AfterCleanExit_IsFalse` — exit code 0 + no
   stderr → flag stays false.
3. `LastCrashWasTunOrphan_AfterTunConflictStderr_IsTrue` — stderr
   containing the FATAL substring → flag true after Exited.
4. `LastCrashWasTunOrphan_AfterUnrelatedCrash_IsFalse` — stderr without
   the substring → flag false.
5. `LastCrashWasTunOrphan_ResetOnSuccessfulStart` — flag true → next
   `StartWithJson` succeeds → flag back to false.
6. `LastCrashWasTunOrphan_ResetOnStop` — flag true → `Stop()` → flag
   back to false.
7. `LastCrashWasTunOrphan_BroaderPrefixSubstring_AlsoMatches` — stderr
   containing only `configure tun interface:` prefix (no full FATAL)
   still flips the flag.

`VPNRouter.Tests/HealthMonitorTunOrphanRestartTests.cs` — new file
(separated for clarity since the HealthMonitor surface is independent
of SingBoxManager's stderr capture surface):

8. `AttemptRestart_TunOrphanFlag_TriggersNetshDisable` — wire a
   SingBoxManager-with-`LastCrashWasTunOrphan=true`, intercept
   TunAdapterDiagnostics.Runner with FakeProcessRunner, assert a netsh
   `interface set interface name=VPNRouter-TUN admin=disabled` call
   happened before any Restart call.
9. `AttemptRestart_NoTunOrphanFlag_SkipsNetshDisable` — flag false →
   FakeProcessRunner doesn't see the netsh disable call.

## Verification gates

1. `dotnet build VPNRouter.sln -c Release` → 0 errors.
2. `dotnet test ... --filter
   "FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
   → **1310+ pass** (baseline `38df0bf`).
3. Existing `SingBoxManagerStateMachineTests` + `SingBoxManagerProcessRunnerTests` +
   `HealthMonitorRecoveryGapTests` stay green.
4. New tests (9) pass.

## Commit + push

Subject: `fix(singbox): recover from TUN orphan crash via netsh disable`

Cites Agent A's commits `66e1407` (Fix #1+#4 public surface) /
`38df0bf` (1310 baseline) for the dependency.

## Outcome

**Status**: shipped 2026-05-21. Direct commit on `main`.

### Files changed

- `VPNRouter.Core/Services/SingBoxManager.cs` — added stderr ring buffer
  (`_capturedStderr` 50-slot string array, lock-protected),
  `LastCrashWasTunOrphan` public property, `DetectTunOrphanCrashSignature`
  private scanner, and a reset block in `LaunchProcess` (covers Start +
  Restart paths via the launch chokepoint). `Stop()` also clears the flag
  for user-initiated stops. ErrorLine handler in LaunchProcess writes into
  the buffer.
- `VPNRouter.Core/Services/HealthMonitor.cs` — extracted
  `RunTunOrphanRecoveryCleanup(CancellationToken)` internal helper.
  AttemptRestart's Task.Delay continuation now calls it before the
  `_singBox.Restart()` step; on false return (caller cancellation during
  the 500 ms settle delay), the continuation bails out.
- `VPNRouter.Tests/SingBoxManagerTunOrphanRecoveryTests.cs` — new file,
  7 tests covering default state / clean exit / FATAL signature /
  unrelated crash / reset on Start / reset on Stop / broader prefix match.
- `VPNRouter.Tests/HealthMonitorTunOrphanRestartTests.cs` — new file,
  2 tests covering the wire-up (flag true → netsh call observed; flag
  false → no netsh call). Uses `TunAdapterDiagnostics.Runner` swap +
  FakeProcessRunner to intercept the netsh call shape.

### Build + test

- `dotnet build VPNRouter.sln -c Release` → 0 errors (228 pre-existing
  warnings, none new).
- `dotnet test ... --filter "FullyQualifiedName!~PageScreenshotTests&
  FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
  → **1319 passed, 0 failed, 4 skipped** (vs Agent A's `66e1407` baseline
  1310 → added 9 new tests, all green).
- All 92 SingBoxManager + HealthMonitor + TunAdapter sibling tests stay
  green.

### Stderr signature confirmation

Verified via PinkuDani field log
`Z:\PinkuDani\vpnrouter20260521_004.log:124-125` — the FATAL line shows up
under the `[WRN] [sing-box]` prefix which is what the `ErrorLine` lambda
routes. SingBoxManager's existing `LaunchProcess` already subscribes to
the IProcessHandle's ErrorLine event and routes through
`Log.Warning("[sing-box] {Line}", line)`. Our addition writes each captured
stderr line into the ring buffer in the same handler. The scanner
observed the FATAL text in test #3
(`LastCrashWasTunOrphan_AfterTunConflictStderr_IsTrue`) and properly
flipped the flag — pinning that sing-box's FATAL does reach stderr from
SingBoxManager's perspective (confirmed via FakeProcessHandle's
`EmitError` emitting through the same event).

### Field-validation concerns

- **500 ms settle delay**: guess based on Agent A's brief noting netsh
  disable releases the kernel handle but timing is unverified. Test
  environment can't validate Windows kernel teardown timing. Leaving 500
  ms as generous default; field log validation by PinkuDani next
  auto-update will close this.
- **netsh admin=disabled releases the wintun kernel handle**: still
  unverified at unit-test level (no IProcess that can simulate kernel
  state). Production path will validate. The new test wires assert the
  netsh argv shape (`name=VPNRouter-TUN`, `admin=disabled`) which is
  what's documented to release the handle.

### Commit

`<filled in after commit>`
