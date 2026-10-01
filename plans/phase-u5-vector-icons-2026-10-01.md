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

1. Icon set, registry, `IconView`, status ring (#430).
2. Replace the symbols page by page (#431): icon buttons (kebabs, close, refresh, QR scan, row actions, stepper,
   remove), icon plus text (Simple / Advanced, Apply, Start / Stop VPN, Add, Find working configs, Stop, Clear all,
   the Settings expander), status icons next to text (health check, custom JSON validation, battery status, autosaved,
   latency badge). After it the scan finds 124 symbols on 78 lines; the rest are text arrows, log lines, desktop-only
   strings, country flags and the free-config name prefix.
3. Motion (this PR): ring colours fade and the ring settles once when it turns green, chevrons turn, chips fade, an
   appearing check draws in, the chip pulse stops after about 43 s. The Fluent button theme already scales a pressed
   button, so no press effect was added.

## Verification

- Local, by script: `python3 tools/icons/check-icons.py` renders the generated path data, checks mirror symmetry for 16
  icons that must be symmetric (the unfixed Lucide power fails it with 117 pixels, the fixed one passes with 0), checks
  that no icon leaves the 24 grid, and writes light and dark contact sheets, which were looked at.
- CI: Android compile, the new `UiIconsTests` (unique names, every path parses with Avalonia's parser and stays on the
  grid, `StripSymbols` cases), the existing suite; green on #430 and #431.
- APK: Release build with the test hook of all three steps together on the Windows worker (preflight: CPU 1 %, 12.5 GB
  free RAM, 16.7 GB free disk, no other build running): exit code 0, 0 errors.
- Emulator (Linux worker, Android 14): see Outcome.

## Outcome

Steps 1 to 3 merged after green exact-head CI (#430, #431, #432). A device pass on the Android 15 emulator (Linux worker,
1080x2400, light and dark, English) with test-hook APKs built on the Windows worker found two things, fixed in a
follow-up PR: after a connect error the ring kept turning amber (the VPN chip stays in "connecting" on that path, see
ANDROID-CONSENT-DENIED-CONNECTING-STATE), and "+ New category" on the Apps page was still a text plus. A reported error
now ends the connecting look and a new connect clears the error look.

Seen on the device: the ring in all four states (off, connecting behind the consent dialog, connected, error) and off in
dark; the main screen (QR icon, flag, turning chevron), both kebab menus, the Advanced header (chevron Simple), Servers
(row refresh icons, Add, Start VPN), Subscribe (pencil, refresh, trash and the trash-? confirm, QR, Add), Settings
(autosaved check), Public (search-check button, turning Settings chevron, minus and plus steppers) and Apps. On-device
mirror check of the off ring: 32 of 115600 pixels differ at 15 % fuzz (anti-aliasing at the crop edge).

Not seen: the motion itself (screenshots only; animations were reviewed in code), the free configs latency badge with
a check (needs a real search), the custom JSON validation icons, the battery status icon, Russian, font scale 1.3,
and a real phone.
