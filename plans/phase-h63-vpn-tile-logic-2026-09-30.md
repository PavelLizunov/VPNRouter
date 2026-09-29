# H-63: state and decision logic for the Android VPN quick-settings tile

## Why

Owner brief `tz-android-vpn.md` (2026-09-30), item 1 (the tile) and item 2 (reliable connect and disconnect). Today the
Java `VpnRouterService` keeps only `tunnel_live` (a boolean) plus in-process broadcasts, so there is no persisted
"connecting" or "error" state, no reason, and a value left over from a killed process is trusted. A tile that survives
process restarts and never claims "connected" before the tunnel exists needs a single state source with rules for
staleness, and a fixed answer to "what does a tap do". That logic has no Android dependency, so it belongs in Core where
it can be unit-tested; the Android glue (service writes, tile, screen) follows in later steps (H-64, H-65).

## What

`VPNRouter.Core/Services/VpnTileLogic.cs`, no behaviour change anywhere yet:

- `VpnConnectionState` (`Disconnected`, `Connecting`, `Connected`, `Error`), `VpnStateSnapshot` (state, reason, owner
  process id, timestamp) and `VpnStateCodec` (the four words the Java service will write, and their parser).
- `VpnStateResolver.Resolve`: a `Connecting` or `Connected` record written by another process is `Disconnected`
  (`interrupted`, the tunnel died with its process); `Connecting` older than 90 seconds, or stamped far in the future, is
  `Error` (`connect-timeout`), so "connecting" cannot last forever; `Connected` never times out.
- `TileClickPlanner.Plan`: taps are ignored while connecting (no parallel connections); a tap on a connected tile stops
  the VPN; otherwise start, unless Android's VPN consent is missing (open the app for the system prompt) or no saved
  configuration exists (open the app to set up).
- `TileAppearanceFactory`: visual (`Off`, `Busy`, `On`, `Error`) and a short Russian or English subtitle per state, and
  short localised texts for the known failure reasons; unknown reasons stay generic so raw exception text never reaches the
  tile.

## Verification

`VPNRouter.Tests/VpnTileLogicTests.cs`, 16 test methods with theories covering every state, the 90-second boundary, the
future-stamp case, the codec round trip and the full click table. Exact-head CI (all platforms build Core; the Android
compile check builds the same file).

## Outcome

Merged after green exact-head CI.
