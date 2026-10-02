# D-8: audit of every desktop page and window

## Why

Owner feedback on r16: some pages look as if nobody opened them. After D-7 (Applications) every other desktop page
and window was walked through on real captures and fixed where it still looked old, misaligned or asymmetric.

## Method

`PageScreenshotDesignTests` (extended in D-7 and here): every page in light and dark at 360 and 520 px, Russian at
360 px for Simple, Servers, Subscribe, Network, Applications, DPI, Telegram and Free configs, every Settings sub-tab in
light and dark, content states (servers with entries, a subscription, custom rules in Cards / Read / Edit, every
Zapret and Telegram inner tab, saved free configs, the custom-config tab), the window in simple and advanced mode,
About and the four setup wizard steps. Design captures now paint `SurfaceApp` behind a page like the app window does
(`ScreenshotHelper.CapturePage(..., appBackground: true)`), so cards read as they do in the app.

A script scan of the desktop sources for text symbols and emoji (same rules as `tools/icons/scan-symbols.py`) finds
no symbol shown as an icon any more: the remaining ones are converted to `Pictogram` icons by `PictogramText`, sit in
sentences or log lines, or belong to view-model strings that no view binds.

## Checked, no change needed

Simple page, the window header and both tab strips (D-2, D-5, D-6), Subscribe (rows, subscription row, form), Free
configs search tab, Settings: Routing, Leak protection, Content, Updates, Autostart, rules Read and Edit views,
Telegram settings and help tabs, Tools tab strip, setup wizard steps 1 to 3.

## Fixed

1. Servers: the header row had one column less than the rows, so IP, Ping and Port headers sat 32 px right of their
   values; the protocol pill ("Daily") floated at the right edge of the name column below the name, now inline after
   the host line; rows use `RadiusSm`.
2. Servers, server detail editor: 10 px labels and fields with 2 px padding; now 11 px, secondary labels, 6 px rhythm.
3. Custom config tab: the list was a grey Fluent box; now a card like the server list; texts use tokens.
4. DPI bypass: the inner tab card had no background (black on dark); now `SurfaceBase`. Advanced tab: stretched
   buttons with left-aligned labels mixed with compact ones; labels centred, "Remove Zapret service" in the new
   `danger-soft` role (danger text on a danger tint) instead of red text on cyan, GitHub button without the accent
   override.
5. Telegram version tab: "Reopen in Telegram" label centred.
6. Muted text: 44 hint and caption text blocks on Telegram, Subscribe, Servers, Network, Free configs and DPI drew
   secondary text at 45 to 75 % opacity; they use `TextMuted` now (one token instead of opacity on top of a token).
   Counts inside the rules filter buttons and two free-config row cells keep their opacity on purpose.
7. Rules cards: the action ("direct", "proxy", "block") was a 74 px bordered box that read as a disabled button; now a
   pill.
8. Setup wizard result step: an empty status line pushed the summary to the bottom of its card; hidden when empty.
9. About: fixed 420 px height left a gap above the Close button; the window now sizes to its content.
10. Tray menu: "Settings...", "Connect" / "Disconnect" and "Quit" were hard-coded English; they use the existing
    localized `TraySettings`, `TrayStart` / `TrayStop` and `TrayExit` (symbols dropped from the two start/stop
    strings, a native menu cannot draw them as icons) and follow a language switch.

## Seen, not changed

- New free-config servers are named "⚡ free" (the prefix is also the lookup key for existing entries).

## Verification

- Windows worker, exact SHA: `PageScreenshotDesignTests`, `HeadlessGuiTests`, `VisualDiffTests`; PNGs read.
- PR CI.

## Outcome

Windows worker (preflight CPU 1 %, 12.1 GB free RAM, 11.7 GB free disk, two idle dotnet processes): `7bf7e774`
104 of 104, `5cff0cc4` 105 of 105 (`PageScreenshotDesignTests`, `HeadlessGuiTests`, `VisualDiffTests`,
`MainWindowViewModelTests`; no visual baseline changed). Captures read before (`3e2fa9d0`, D-7 branch) and after.
Not seen: hover with a real pointer, the native tray menu (no headless capture), a live WINBRAT run.
