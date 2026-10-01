# A-1: ignore a second Connect while the VPN consent dialog is open

## Why

Ledger line ANDROID-DOUBLE-CONNECT-STATE: two `TEST_CONNECT` calls a few seconds apart while the system VPN
consent dialog was open left the state `disconnected` after the user accepted. A double tap on Connect, or a tile
tap followed by a button tap, takes the same path: `MainActivity.RequestConnect` called
`StartActivityForResult` a second time. Android answers the first dialog with "cancelled", which the app reads
as a refusal (`SetIntent(false)`).

## What

- `ConnectConsentGate` (Core, platform neutral, unit tested): one open consent dialog at a time.
- `MainActivity.RequestConnect` asks the gate before it opens the dialog and ignores the repeat; the gate closes
  in `OnActivityResult` for the consent request code (any result) and if opening the dialog throws.

## Not covered

`StartTunnelService` and the service itself are unchanged. A dialog that outlives its activity (rotation while it is
open) gets a fresh gate in the new activity; accepted.

## Verification

Gate unit tests (including 64 racing requests opening exactly one dialog). Exact-head CI compiles the Android
project. Emulator check with the test hook on the Linux worker: two `TEST_CONNECT` calls while the dialog is open,
the log shows one dialog and one ignored request; the result is recorded below.

## Outcome

Verified 2026-10-01 on the Linux worker emulator (Android 14, x86_64, test-hook builds, fresh install so the consent
is not granted yet, two `TEST_CONNECT` calls 2 s apart):

- Before (main `fb1194f9`): the log shows "presenting system VPN consent dialog" twice and a second `ConfirmDialog`
  stays on top after the first OK; the state stays `disconnected` although `consentGranted` is true. The bug reproduces.
- After (this PR `9fd175a8`): one dialog, the second request is logged as ignored; after OK the consent is granted
  and the tunnel start runs (it ends in `error` with the known missing `libbox.so` of an x86_64 emulator, which is
  the expected result of a single call there).
- Build load on `windows-worker`: CPU 0 to 1 percent, 13.2 GB free RAM, 17.5 GB free disk before each of the two builds.
