# H-67: a missing native library must end in the Error state, not kill the app

## Why

Found while checking the quick-settings tile on an Android 14 x86_64 emulator with an `android-x64` build (the bundled
`libbox.aar` only carries `arm64-v8a`). Pressing Connect logged
`java.lang.UnsatisfiedLinkError: dlopen failed: library "libbox.so" not found` from `ensureLibboxSetup` on the
`vpn-lifecycle` thread. `startTunnel()` only caught `Exception`; `UnsatisfiedLinkError` is a `LinkageError` (an `Error`),
so it escaped, the default handler ended the process, the system restarted the crashed foreground service, and the state
record stayed `connecting` until the dead-process rule turned it into "interrupted".

Real phones are arm64 and carry the library, so this is a hardening fix for x86 devices, Chromebooks, emulators and any
broken install, not a fix for a reported user crash.

## What

`VpnRouterService.startTunnel()` now catches `Exception | LinkageError`, so the existing failure path runs: teardown,
`vpn_state` = `error` with the scrubbed message, `TUNNEL_ERROR` broadcast (the main screen shows it), tile refresh,
`stopSelf()`. One line; no behaviour change for the success path.

## Verification

Android compile check in CI. On the emulator: an `android-x64` build of this branch must show the error text on the main
screen after Connect, keep the process alive and show the tile in an error/off state without a restart loop. Result is
recorded below once run.

## Outcome

Merged after green exact-head CI.
