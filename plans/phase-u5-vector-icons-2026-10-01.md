# U5: vector icons instead of text symbols, a redrawn status ring

## Why

The owner tested r11 on a Pixel (2026-10-01): many icons have geometry and symmetry problems, the power glyph in the
status ring included; many places still use old text symbols or emoji instead of icons; the bottom navigation icons are
good. Everything else may become vector icons, with vector animation where it helps.

## Inventory (script, 2026-10-01, `origin/main` at `26ac2320`)

`tools/icons/scan-symbols.py` (a scan of the Android sources and the Core strings for symbol characters) found 179 characters on
123 lines. Classified by hand from the list:

- Icons in buttons (to replace): kebab menu `⋮` (2), close `✕` (4 overlays), refresh `⟳` (2), camera emoji for QR
  scan (2), subscription row actions `✎ ↻ ✕` (3 plus the `✕?` confirm), free configs stepper `−`, flag `⚑` and
  chevrons `› ⌄` on the main screen (4), saved-config remove `✕`.
- Symbols at the edge of a button label (to replace by an icon next to the text): `◂ Simple`, `Advanced ▸`, `↻ Apply`,
  `✓✓ Find working configs`, `✕ Stop`, `✕ Clear all`, `▾ Settings`, `▶ Search`.
- Status marks inside text (to replace by an icon next to the text): `✓ JSON is valid`, `✗ Invalid`, `✓ Last check`,
  `✓ VPNRouter is excluded from battery optimization`, `✗` before the delete-category confirm, `✓`/`✓✓` after the
  latency in the free configs list.
- Real text (kept): arrows in sentences (`VPN → gear → Always-on`, `domain → action`, `Wi-Fi ↔ cellular`,
  `files → name`, `Select a row ↑`, `by ping ↑`), the traffic line `↓ rate ↑ rate`, log messages, country flags.
- Not shown on Android (kept, desktop or unused wrappers): 33 string keys such as `SmpStartVpn`, `FcDeepStop`,
  `ChannelStable`, `RulesEditorDirty`.

Shared strings keep their symbols because the desktop UI still shows them; the Android code removes edge symbols with
`UiIcons.StripSymbols` and draws the icon. Android-only strings lose the symbol in the string itself.

## Decisions

- Icon set: Lucide 1.49.0 (ISC). The bottom navigation icons (drawn for the app in U3, 24 dp, 1.8 dp stroke, round caps
  and joins) are Lucide-like, so Lucide matches them without redrawing what the owner likes. The SVG files are copied
  unchanged into `tools/icons/lucide/` with the licence; `tools/icons/lucide-to-cs.py` converts them to Avalonia path
  data in `VPNRouter.Core/Services/UiIcons.cs`. One measured correction: the Lucide power arc ends 0.03 and 0.04 units
  off the mirror of its start; it is snapped to the mirror.
- One control, `Controls/IconView.cs`: stroke 1.8 dp at every size (as the navigation icons), colour inherited like text
  so an icon in a button follows the button's role, state and theme; sizes next to text go through `UiScale.Ic` (the
  same clamped system font scale as the text).
- Status ring: 112 dp, track and power icon both 4 dp, icon 52 dp centred on the ring and lifted 1 dp (its arc sits
  lower than its line). States: off (grey), connecting (amber, a 100 degree arc turns, at most about 45 s), connected
  (green), error (red while the error line is shown). Before U5 the ring never showed "connecting" (only the chip did)
  and had no error look; both now follow the existing chip state and error line.
- Animations: Avalonia animations in code only, no new dependency; none when Android "Remove animations" is on
  (`animator_duration_scale` 0); no endless loop while idle.

## Steps

1. Icon set, registry, `IconView`, status ring (this record, first PR).
2. Replace the symbols page by page.
3. Animations: state transitions of the ring and the chips, press feedback, check mark drawing in.

## Verification

- Local, by script: `python3 tools/icons/check-icons.py` renders the generated path data, checks mirror symmetry for 16
  icons that must be symmetric (the unfixed Lucide power fails it with 117 pixels, the fixed one passes with 0), checks
  that no icon leaves the 24 grid, and writes light and dark contact sheets, which were looked at.
- CI: Android compile, the new `UiIconsTests` (unique names, every path parses with Avalonia's parser and stays on the
  grid, `StripSymbols` cases), the existing suite.
- Emulator (Linux worker, Android 14): before and after screenshots of the main screen and the pages with icons.

## Outcome

In progress.
