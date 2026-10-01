# A-2: the quick-settings tile keeps showing a stale status

## Why

Owner report from a Pixel (Android 16), 2026-10-01: the tile switches the VPN on and off, but its status text
never changes (it stays "Podklyuchen", also when the tile is resized to one or two cells).

## Cause (best hypothesis, not reproduced on a device yet)

The tile is an active tile: it refreshed only in `OnTileAdded`, `OnStartListening` and at the end of `OnClick`,
where it still reads the old state (the service has not run the stop or start yet). Afterwards it relied on the
service calling `TileService.requestListeningState`. That request only produces `OnStartListening` when the
tile is not listening, and an active tile gets no stop callback from the shade, so while the tile counts as
listening (the shade is open, which is exactly when the owner taps it) the request is dropped and the tile
keeps the state from the moment of the tap. On the API 34 emulator the state change arrived with the shade
closed, so the request worked there.

## What

- While the tile is listening it watches the state record (`vpn_state*` keys in SharedPreferences, written by
  the service) and refreshes on every change; the watcher is removed in `OnStopListening` and `OnDestroy`.
  `requestListeningState` stays for the case where the tile is not listening.
- Identical repeats are skipped (one write changes several keys); a forced refresh (system callback, tap) always
  pushes.
- A "connecting" tile is unavailable (ignores taps): `VpnStateResolver.NextChangeIn` (Core, tested) gives the
  moment the attempt turns into a timeout, and the tile refreshes itself then, so it cannot stay frozen.
- On Android 11+ the tile also sets `ContentDescription` (`TileAppearanceFactory.Spoken`, tested) and
  `StateDescription`; Android 16 hides the subtitle on a one-cell tile, so the colour and the spoken text carry
  the status there.

## Not covered

`ACTIVE_TILE` stays (a non-active tile would start the app process on every shade pull). If the device check
shows that `requestListeningState` itself is ignored while the shade is closed, drop `ACTIVE_TILE` next.

## Verification

Unit tests for `NextChangeIn` and `Spoken`; exact-head CI compiles the Android project. Device check (Android 16
emulator or the tester's Pixel): add the tile, open the shade, tap it twice: the subtitle must follow
Connecting, Connected, Off within a second, and also when the VPN is changed from the app with the shade open.

## Outcome

Pending.
