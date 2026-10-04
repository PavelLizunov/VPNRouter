# Phase H: home screen concept A, "status emblem" (2026-10-03)

Branch `concept/home-a`, based on `832d4861` (tools/probe-theme-via-vm: the probe switches the theme through the view
model, so dark renders get the inverted mascot). Concept only: no PR, no merge, no release. One of three competing
concepts for the Simple mode home page.

## Intent

Redesign `VPNRouter.App/Views/Pages/SimplePage.axaml` so that it reads like a VPN app: one dominant centred action, a
status hero with designed states, setup separated from action, quiet secondary rows.

## Critique of the page before (renders of `832d4861`)

- The primary action was a thin outlined button at the bottom, with the same weight as the rows around it, below a
  form. In the connected state the solid button was Disconnect, so the strongest colour on the page meant "stop".
- The status "hero" was a small text card with a 12 px dot: no ring, no colour, no motion, the same weight as the form.
- Setup and action were mixed: a mono "Config - Mode / manual - split" row with a "Hide" pill controlled the card under
  it; that card stacked the config text box, two radio buttons with sub-lines and an Autostart row.
- "Advanced settings" was a full card with the weight of the status card.
- Three corner radii (3, 6, 8), paddings 12/14, label sizes 9/10/11; left-aligned text in a 420 px column.
- No first-run state: a fresh install showed the same page with an empty text box and an enabled Connect that
  answered with an error.

## References and what was taken

- ExpressVPN desktop (14.x): the large centred connect control with a ring that changes with state, the state word
  under it, then one "current location" card with a change affordance. Taken: the vertical order emblem, word, action,
  connection card.
- Mullvad: state word coloured by state, red Disconnect while connected, one switch-location row. Taken: colour on the
  state word, danger-soft Disconnect.
- Cloudflare WARP: one control, one sentence explaining what is happening ("Your Internet is private"). Taken: a single
  sub-line, no secondary status text in the hero.
- Proton VPN / Windscribe: profile or location row with a quiet "change" control, settings out of the main path.
  Taken: the config row with a "Change" pill, secondary items in a quiet list.
- Tailscale: restraint, a list of plain rows for settings. Taken: the quiet list (no fill, one hairline border).
- OpenDesign packs `expressvpn` and `linear` (spec.json): 4 px spacing base, radius scale with a 12 px card and a
  pill, micro motion around 150-220 ms with a standard ease. Taken: the 4/8/12/16/24 rhythm, 14 px cards (RadiusXl)
  with 10 px inner controls (RadiusLg, concentric with the 4 px list padding), pill for the action and the segments,
  0.24 s brush/opacity transitions.
- Not taken: maps, server lists and country flags on the home screen (VPNRouter has one config, not a location
  catalogue), brand colours of any reference. The palette stays VPNRouter's sky-blue accent and slate neutrals.

## Design

One column, `MaxWidth=400`, centred, 16 px gutters, 24 px top. Top to bottom:

1. Status emblem (136 px): a state halo, a 3 px track ring, a state ring (success / warning / danger) or a rotating
   100 degree accent arc while connecting, the mascot (original art, unchanged, `LogoSource` so the dark theme gets
   the inverted bitmap) on a 92 px round tile, and a 34 px shield badge at six o'clock (shield, shield-check,
   shield-alert). Idle has no halo, so only live states glow.
2. State word, 22 px semibold, coloured by state (success, warning, danger; primary otherwise).
3. One sub-line, 12 px secondary text, max 340 px: what is happening or what to do. Live traffic (arrow icons) under it
   while connected.
4. First run only (no saved config), or after "Change": a card with the config text box (link icon inside) and its
   hint. It sits above the button so the flow reads top-down: paste, then connect.
5. The primary action: a 48 px pill the width of the column. Connect = accent fill with a power icon; Disconnect =
   danger-soft; while connecting a disabled accent-tint pill with a small spinner and "Please wait".
6. Connection card: config row (server icon tile, config name, kind such as "Server link" or "Subscription - 12
   servers", unknown-outbound warning when it applies, "Change" pill), a hairline, then "Route through VPN" with a
   two-segment pill control (Selected apps / All traffic) and one hint line for the selected option.
7. Quiet list: Autostart (status line, opens the autostart settings as before) and Advanced settings, as plain rows
   with a leading icon and a trailing chevron.

Symmetric about the vertical axis at every width; the hero is centred, everything else spans the column.

## State matrix

| State | Emblem | Word (EN / RU) | Sub-line | Button |
|---|---|---|---|---|
| No config | grey track, shield badge | Add a VPN config / Добавьте конфиг VPN | paste a link, then connect | Connect, disabled until text is typed |
| Config typed (first run) | idle | Add a VPN config | same | Connect, enabled |
| Configured, idle | idle | Not connected / Не подключено | existing disconnected hint | Connect (accent) |
| Connecting | accent halo, spinning arc (still with reduced motion) | Connecting... / Подключение... | handshake hint | Please wait / Подождите (disabled) |
| Connected | green halo and ring, shield-check | Connected / Подключено (green) | via server and IP | Disconnect / Отключить (danger-soft) |
| Error (connect failed, `SmpErrorText`) | red halo and ring, shield-alert | Couldn't connect / Не удалось подключиться | the error text | Connect (retry) |
| Warning (failover alert while up) | amber halo and ring, shield-alert | Connection problem / Проблема с подключением | the alert without the text symbol | Disconnect |

The Connect / Disconnect buttons keep `AutomationProperties.Name="{Binding SimpleCtaText}"`, so UI Automation still
finds "Connect" / "Подключить" and "Disconnect" / "Отключить" (`tools/brat-stability.ps1`).

## Changes

- `SimplePage.axaml`: rewritten; page-local styles only (App.axaml, MainWindow.axaml and Tokens.axaml untouched).
- `SimplePage.axaml.cs`: reduced motion on Windows (SPI_GETCLIENTAREAANIMATION) sets the class `calm`, which removes
  the arc rotation. Other systems expose no setting Avalonia reads; the animation stays on there.
- `MainWindowViewModel.SimpleMode.cs`: read-only projections `SmpHasConfig`, `SmpNeedsConfig`,
  `SmpConfigEditorVisible`, `SmpCanConnect`, `SmpHeroIs{On,Busy,Idle,Error,Warn}`, `SmpHomeTitle`, `SmpHomeSubline`,
  `SmpConfigName`, `SmpConfigKind` and label getters; `WireSimpleHomeNotifications` (called once from the
  constructor) raises them when their inputs change. `SmpFormExpanded` now defaults to false (the editor shows by
  itself when there is no config).
- Strings (EN + RU): `SmpHeroAddConfigTitle`, `SmpHeroAddConfigHint`, `SmpHeroErrorTitle`, `SmpHeroWarnTitle`,
  `SmpCtaWait`, `SmpSegSplit`, `SmpSegFull`, `SmpKindServer`, `SmpKindSubscription`, `SmpKindCustom`,
  `SmpKindServers(n)` with Russian plural forms.
- Icons: Lucide 1.49.0 `shield-alert.svg`, `link.svg` copied unchanged, `UiIcons.cs` regenerated,
  `check-icons.py` passes; Pictogram ids `shield-check`, `shield-alert`, `link`, `power`.
- Probe: scenarios `configured` (one saved server) and `alert` (failover alert while connected).
- Test: `MainWindowViewModelHomeStateTests` pins the projections.

## Verification

- CI `build-ui-mcp.yml` rounds: 37052684293 (round 1), 37053136728 (round 2), 37053531263 (round 3),
  37053810240 (round 4, final renders). All green.
- Renders: states noconfig, typed, connecting, connected, error (plus alert, editing) x light/dark x en/ru x widths
  360/520/720/1000 with zero lint warnings; final set in the session scratchpad `home-a/`.
- Audit `tools/ui-mcp/audit.py --surfaces simple,window-simple --widths 360,520,720,1000`: see Outcome.
- `test.yml` dispatched on the branch for the new tests: see Outcome.

## Not done

- No PR, merge or release (concept). No WINBRAT check: the UIA names are unchanged by construction, not verified live.
- Reduced motion is read on Windows only.
- The Autostart row keeps opening the autostart settings (it is not a toggle): `SmpAutostartChecked` would install
  the service from the home page, which is a behaviour change outside a visual concept.
- There is no cancel during connecting: `SmpToggleConnectAsync` ignores a press while connecting, so the button says
  "Please wait" instead of the old disabled "Cancel".
- The connected sub-line repeats the server name from the card below; an exit IP line needs data the view model does
  not have yet.
- Android home screen not touched.

## Outcome

- Final renders come from build 37053810240 at `e7bba840` (80 files, `<state>-<theme>-<lang>-<width>.png`, states
  noconfig, typed, connecting, connected, error; light/dark, en/ru, 360/520/720/1000): zero lint warnings.
- Audit `--surfaces simple,window-simple --widths 360,520,720,1000 --scenarios default,configured,connecting,connected,alert`:
  160 cells, 0 warnings, 0 broken. Remaining info: `uneven-gutter` on the inset divider of the quiet list (deliberate,
  it lines up with the row text) and the header's small "VPN" badge (MainWindow, not in scope).
- `test.yml` run 37053624558 on `79a493ca`: Linux 2961 passed / 0 failed (the four new home-state tests included),
  Windows characterization and Go jobs green.
- Commits after `e7bba840` touch this record only.
