# D-7: the Applications page in the current design system

## Why

The owner tested r16: the inside of the Applications page (`VPNRouter.App/Views/Pages/ApplicationsPage.axaml`) is
"completely old, legacy and ugly". It never got the D-1..D-6 pass because its page captures crashed until D-3.

## Found (captures of `main` at `b7ba2c54`, light and dark, 360 and 520 px, English and Russian)

- Mode switch: one solid accent half and one outlined half, unlike the other segmented controls.
- Category list: bare text rows, tiny monospace counts, the selection only bold accent text on white.
- The bypass categories showed their internal ids (`Russian_Desktop_Apps`, `Game_Launchers`, ...).
- Header: Select all / Clear crowded the category title; at 360 px in Russian the title was cut to "Di...".
- App rows: sunken grey blocks; the remove action a bare text cross.
- Bottom bar: at 360 px in Russian the Add button was cut off; "Browse..." and "Running" were plain text buttons.
- "Category name:" placeholder with a colon; empty states in italic at 50 % opacity; banners without an icon.

## Changes

- Page on `SurfaceApp`. Mode switch is a segmented control: `SurfaceSunken` track, the selected half raised on
  `SurfaceBase` with `AccentFg` text and `ShadowSm`, tint on hover.
- Category list uses the tab roles of D-5/D-6 (`ListBox.sub-tabs` alias in `App.axaml`): rounded selection on
  `AccentBgMuted`; counts are neutral pills. New-category field and tonal button with spacing.
- Bypass categories get proper names in English and Russian (`Strings.GroupDisplayName`, two cases locked in
  `AndroidCategoryLocalizationTests`); unknown and user names stay verbatim as before.
- Header: title on its own line, Select all / Clear (tonal, small) below, remove-category as a trash icon button.
- App rows: `SurfaceBase` cards with a subtle border (accent border on hover), VPN / BYPASS / custom pills, trash icon
  button with a danger tint on hover.
- Bottom card: warning icon with the pending-apply hint, input with Add next to it, Browse (folder icon) and Running
  (activity icon, Lucide `folder-open` added) as two equal tonal buttons; their labels and accessible names are set in
  code as before.
- Empty states: icon plus muted text, no italic; banners have an info or warning icon.
- Design captures: Applications states (category open with a custom app, full tunnel, bypass list, Russian, 360 px),
  About and the setup wizard, Settings in dark, content states of Servers, Subscribe, rules views, Zapret and
  Telegram inner tabs, saved free configs (for D-8).

Bindings, commands, names (`CustomAppInput`, `NewCategoryInput`, `BrowseExeButton`, `RunningProcessesButton`,
`AddCustomAppButton`) and behaviour unchanged.

## Verification

- Windows worker, exact SHA, `PageScreenshotDesignTests`, `HeadlessGuiTests`, `VisualDiffTests`,
  `AndroidCategoryLocalizationTests`; PNGs read.
- PR CI.

## Outcome

Windows worker (preflight CPU 2 %, 13.1 GB free RAM, 12.3 GB free disk, no dotnet or java running): `ad1ad8ab`
110 of 110, `b01416c0` 54 of 54, `3e2fa9d0` 92 of 92 design captures. Read: every Applications state in light and
dark, 360 and 520 px, English and Russian. Not seen: hover with a real pointer, the running-process flyout.
