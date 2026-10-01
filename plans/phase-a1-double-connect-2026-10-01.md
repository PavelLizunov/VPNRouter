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

Pending.
