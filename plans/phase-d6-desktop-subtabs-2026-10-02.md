# D-6: inner tab strips on desktop follow the main tab strip

## Why

D-5 (#452) gave the main tab strip the Android navigation roles. The inner strips of the pages (Servers / Custom
Config, Search / Saved, Zapret / Telegram proxy, the Settings side list) still used the Fluent default selection fill,
so two tab styles sat on top of each other.

## Changes

- `App.axaml`: one set of tab roles for `ListBoxItem.main-tab` and the new `ListBoxItem.sub-tab`: secondary text when
  idle, `AccentBgSubtle` on hover, `AccentBgMuted` when pressed, `AccentBgMuted` with `AccentFg` text when selected
  (also selected with focus, hover and press), rounded `RadiusSm`. Light and dark through the tokens. The main-tab
  state styles move there from `MainWindow.axaml` (sizes stay in the window).
- `sub-tab` on the inner strips of Servers, Free configs, Tools and the Settings side list (6 px inset so the rounded
  selection does not touch the edges). Inner tabs are 11 px, medium weight, 10 x 4 padding.
- Free configs "Search" tab: plain label like "Saved" (the edge symbol of the shared string is stripped,
  `L_FcTabSearchLabel`), instead of an icon on one of the two tabs only.

## Seen, not changed

- New free-config servers are named "⚡ free" in the server list; the prefix is also the key that finds an existing
  free entry, so renaming it needs a migration of saved names.
- `VpnBadgeText` and the other runtime badge strings with coloured-circle emoji are not bound anywhere (dead public
  surface, kept).

## Verification

- Windows worker, exact SHA: `PageScreenshotDesignTests` (Servers, Free configs, Tools, Settings sub-tabs, light and
  dark, 360 and 520 px), `HeadlessGuiTests`, `VisualDiffTests`; PNGs read.
- PR CI.

## Outcome

(filled in at delivery)
