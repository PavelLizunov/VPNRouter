# H-64: the quick-settings tile and the shared connection-state record

## Why

Owner brief `tz-android-vpn.md` (2026-09-30), items 1 and 2. H-63 added the platform-independent rules; this step wires them
to Android: the VPN service records its connection state in one place and a real `TileService` shows it and reacts to taps.

## What

- `VpnRouterService.java` writes one record to the existing `vpnrouter_settings` preferences (`vpn_state`,
  `vpn_state_reason`, `vpn_state_pid`, `vpn_state_at_ms`) at every transition, and asks the tile to refresh:
  `connecting` at the start of `startTunnel`; `connected` right before the tunnel-up broadcast; `error` with a reason when
  the start fails (exception text, scrubbed and capped at 300 characters), when the foreground start is refused
  (`foreground-start-blocked`), when no saved configuration exists (`no-config`) and when Android revokes the VPN
  (`no-permission`); `disconnected` when a live (`connecting` or `connected`) tunnel is stopped. An error is not overwritten
  by the clean-up that follows it. New constant `ACTION_TILE_START` names the tile's start intent; it takes the existing
  path that restores the last good configuration.
- `VpnTileService.cs`: a `TileService` registered by attributes (bind permission, `QS_TILE` filter, active tile). On
  every listen it resolves the record (`VpnStateResolver`), so a record from a dead process or a stuck "connecting" shows
  the true state. A tap follows `TileClickPlanner`: ignored while connecting; stop when connected; start otherwise, marking
  "connecting" first so a second tap in the same moment is ignored; when Android's consent (`VpnService.Prepare`) is missing
  or nothing was ever configured, the shade collapses and the app opens with a reason extra (`permission`, `setup`,
  `connect`); the app-side handling is the next step. The tile shows Off, Connecting (unavailable state), Connected
  (active) or Error with a short reason, in Russian or English.
- `Resources/drawable/ic_qs_vpn.xml`: the tile icon. `AGENTS.md` of the zone documents the record.

Not in this step: the app screen and notification still use their own state (next step), the "add the tile" menu item and
the app-side reaction to the tile's reason extra.

## Verification

Compiles in the Android compile check (CI); the rules it uses are covered by the 16 tests of H-63. There is no device or
emulator run, so nothing here has been seen working on a phone: Android's rules for starting a foreground service from a
tile tap (Android 12 and newer), the pending-intent variant of `startActivityAndCollapse` (Android 14 and newer), and the
tile refresh after each transition are the parts that need a device check.

## Outcome

Merged after green exact-head CI. Runtime behaviour on a device is not yet verified.
