# D-5: icons in the desktop tab strip

## Why

The owner likes the Android bottom navigation icons (servers, subscription arcs, sliders, 2x2 grid, globe) and asked
for them on the PC app. The desktop keeps its top tab strip (desktop convention); each tab gets the matching icon left
of its label.

## Changes

- Lucide 1.49.0 files added unchanged (curl): `server`, `rss`, `sliders-horizontal`, `layout-grid`, `wrench`; the
  globe was already there. They match the Android navigation glyphs (stacked rounded bars, arcs, sliders, grid,
  globe); the Tools tab, which Android does not show, gets the wrench. `check-icons.py` also checks `server` (top-bottom)
  and `layout-grid` (both) for symmetry.
- `Pictogram` ids `tab-servers`, `tab-subscribe`, `tab-settings`, `tab-apps`, `tab-tools`, `tab-public`.
- `MainWindow.axaml`: each tab item is an icon (14 px) and its label; class `main-tab`: inactive in the secondary text
  colour, hover a subtle accent tint, selected `AccentBgMuted` with `AccentFg` text and icon (as the Android active
  tab), rounded, 8 px side padding so the strip stays compact (it still scrolls horizontally when narrow).
- Every tab item sets `AutomationProperties.Name` to its title, so UI automation that selects tabs by name (English or
  Russian) keeps working now that the content is a panel.
- Design captures: the window also at 360 px and in dark with the Settings and Public tabs.

## Verification

- `check-icons.py` (symmetry, grid, contact sheet).
- Windows worker, exact SHA: `PageScreenshotDesignTests`, `HeadlessGuiTests`, `VisualDiffTests`; captures read
  (light and dark, 360 and 520 px).
- PR CI.

## Outcome

Windows worker (preflight CPU 0 %, 12.9 GB free RAM, 12.9 GB free disk, no dotnet or java running), exact SHA
`88f01a7e`: 84 of 84 (`PageScreenshotDesignTests`, `HeadlessGuiTests`, `VisualDiffTests`, `PictogramTextTests`,
`UiIconsTests`); no baseline changed. Captures read: strip in light and dark at 520 px with Servers, Settings and Public
selected, and at 360 px (the strip scrolls; the selected Public tab is scrolled into view in dark). `check-icons.py`
passes. Not seen: hover with a real pointer, a live UI automation run against the new tab items.
