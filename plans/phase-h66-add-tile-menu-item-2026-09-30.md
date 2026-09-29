# H-66: kebab-menu item "Add VPN button to the shade"

## Why

Owner brief `tz-android-vpn.md` (2026-09-30), item 1: the app menu must offer to put the VPN tile into the quick-settings
shade. Android 13 and newer can show a system prompt for it; older versions can only be told how to add the tile by hand.

## What

- `AndroidApp.TileMenu.cs` (new partial): `OnMenuAddTileClicked` closes the menu. On API 33+ it calls
  `StatusBarManager.RequestAddTileService` for `com.ninitux.vpnrouter.VpnTileService` (label "VPNRouter", icon
  `ic_qs_vpn`) and shows the system's answer (added / already there / not added) through `ShowMenuFeedback`. On older
  versions, or when the request cannot be made (no manager, icon missing, exception), it shows the manual instruction.
- `AndroidApp.axaml.cs`: field `_menuAddTileItem`, created after the Troubleshooting section, text refreshed in the
  language switch block.
- Core `Strings.Android.cs` and Android `Localization.cs`: `MenuItemAddTile`, `TileAddInstruction`,
  `TileAddResultAdded`, `TileAddResultAlready`, `TileAddResultDeclined` (RU/EN).
- `VPNRouter.Android/AGENTS.md`: `TileMenu` added to the partial list.

## Verification

Android compile check on a worker (API 33 members, the `IConsumer` implementation, the new partial). The Core strings are
plain properties covered by the existing localization tests in CI. Not run on a device: the system prompt and the toast
text still need a look on an Android 13+ device or emulator and on an older one.

## Outcome

Merged after green exact-head CI. Runtime behaviour on a device is not yet verified.
