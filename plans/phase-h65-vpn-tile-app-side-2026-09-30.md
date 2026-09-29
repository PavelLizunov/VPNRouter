# H-65: the app side of the quick-settings tile

## Why

Owner brief `tz-android-vpn.md` (2026-09-30), items 1 and 2. H-64 added the tile and the service-written state record. The app
must (a) react when the tile could not act from the shade, and (b) show the true state when it is opened after the tile
changed it, instead of a stale local flag.

## What

- `MainActivity`: handles the tile's `OPEN_FROM_TILE` intent in `OnCreate` and the new `OnNewIntent` (the activity is
  `singleTask`) and consumes it so a rotation does not repeat it. Reasons `permission` and `connect` post the existing
  `RequestConnect()`, which shows Android's VPN consent dialog when needed and then starts the tunnel; `setup` shows a
  toast telling the user to set up a connection first. `OnResume` additionally promotes the card from Off to Connected
  when the state record is `Connected`, the tunnel-live flag is set and a VPN transport is active (a tunnel started
  outside the app, for example from the tile while the app was closed).
- `TunnelStateResync.TryPromoteOnResume` (Core): the promotion rule, deliberately stricter than the demotion rule:
  the record must be `Connected` after `VpnStateResolver` (so a record from a dead process cannot promote), plus the live
  flag and the transport check. The old comment "never promote Off to On" still holds for the two signals it named.
- `AndroidStorage.ResolveVpnState()` reads and resolves the record; `TileIntentContract` (Core) holds the intent strings
  shared by the tile and the activity; new string `TileSetupNeeded` (RU/EN).

Not in this step: the main screen still derives its "connecting" and "error" display from its own events; showing the
record there, the notification wording and the "add the tile" menu item are next.

## Verification

`TunnelStatePromotionTests` (10 cases including the dead-process and every-other-state cases) in CI; the Android compile
check for the activity, storage and tile changes. Not run on a device.

## Outcome

Merged after green exact-head CI. Runtime behaviour on a device is not yet verified.
