# A-4: check the tile on Android 15 with a state-setting test hook

## Why

The owner reported that the quick-settings tile toggles the VPN but its status text never changes (Pixel, Android 16).
A-2 (#429) made the tile follow the state record while it listens. On the x86_64 emulator the real service only ever
reaches `error`, so the states in between could not be observed.

## What

`TEST_SET_VPN_STATE --es value connected|connecting|error|disconnected [--es reason ...]` (test-hook builds only)
writes the state record with the same keys and process id as the service and calls
`TileService.requestListeningState`, as `VpnRouterService.writeVpnState` does.

## Verification

Android 15 emulator, hook build of main plus this action, quick settings shade open while the states are set two
seconds apart: the tile text follows live: Off, Connected (blue, active), Connecting..., Error, Connected, Off. Before
A-2 the same setup left the old text. Not verified on a Pixel with Android 16.

## Outcome

Hook merged with this brief; the tile fix is confirmed on the emulator. If the owner still sees a frozen status
on the phone, the fallback in the A-2 brief (drop ACTIVE_TILE) is the next step.
