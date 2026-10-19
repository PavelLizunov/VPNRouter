# Protocol icons in the server lists

## Why

The owner asked for an icon per connection type (VLESS, Hysteria, AWG and so on) so the types are easier to tell apart.
The lists only had a text chip ("Daily", "Games/voice"). The owner saw a preview with two variants
(https://claude.ai/artifact/G5aDTibPkhwTqdnsDcy9F9) and chose B on 2026-10-19: a coloured disc in front of the name.

## What

- `ProtocolGroups` (Core): one mapping from a server entry to its group (Daily, StealthWeb, WebFallback, GamesVoice,
  LowPing, WebOnly, Fallback, Emergency), the group's Lucide icon and its colour token prefix. Desktop and Android use it.
- Icons: venetian-mask (Reality), ghost (XHTTP), globe (WS/gRPC), zap (Hysteria2/TUIC), activity (AmneziaWG),
  compass (Naive), life-buoy (Shadowsocks), siren (DNS tunnel). Five new Lucide 1.49.0 SVGs, generated into `UiIcons`.
- `Proto{Group}FgBrush` / `Proto{Group}BgBrush` tokens in both themes (shared with Android through the linked Tokens.axaml).
- Desktop: `ProtocolDisc` control in the Subscribe and Servers lists (column after the radio, tooltip = the use-case tip).
  `ServerViewModel.ProtocolUseCase` and its tooltip now derive from the group, so chip and disc cannot disagree.
- Android: the same disc in `BuildServerRowCore` (Subscribe and Servers rows).
- `lucide-to-cs.py`: arc flags are always one digit; `01.471` was read as flag 0 and coordinate 1.471 (zap.svg).

## Risk and rollback

UI only; the connection code is untouched. Rollback: revert the PR.

## Gates

1. Release build of the solution and the Android project, zero errors (worker/CI).
2. Focused tests `ProtocolGroupsTests`, `ServerViewModelSubtitleTests`, `DnsTunnelLabelTests`, then the full suite.
3. `tools/icons/check-icons.py` passes; existing icon path data unchanged.
4. Headless screenshots of the Subscribe list in light and dark, inspected.
5. Live check on WINBRAT: the installed branch build shows the discs in the real window.
6. Second-model review of the diff.

## Outcome

- Files: `ProtocolGroups.cs` (new, Core), `ProtocolDisc.cs` (new, desktop control), `Pictogram.cs` (eight ids and
  `Has`), `Tokens.axaml` (16 brushes per theme), `ServerViewModel.cs` (`ProtocolGroup`, chip text from the group),
  `SubscribePage.axaml` and `ServersPage.axaml` (disc column), `AndroidApp.ServerList.cs` (disc in the shared row
  builder), `UiIcons.cs` (generated, five icons added, existing path data unchanged), `lucide-to-cs.py` (arc flags),
  `ProtocolGroupsTests.cs` (new). AppVersion `2.50.5-r1`.
- Tests: `ProtocolGroupsTests` 19/19 on WINBRAT (mapping, unique icons known to both UIs, tokens in both themes, the
  Subscribe list on screen with eight discs whose background is the group's token and whose icon is the group's icon);
  `ServerViewModelSubtitleTests`, `DnsTunnelLabelTests`, `PictogramTextTests` pass. PR CI: compile, test,
  characterization-windows, go-test-windows, grep and the Android compile check green.
- `check-icons.py`: all checks passed.
- Headless capture of the Subscribe list (light) inspected: discs in the colours and shapes of the preview.
  The headless theme switch did not render dark (also with a `ThemeVariantScope`), so the dark theme is checked on the
  real window after the release; the dark tokens are asserted by the token test.
- Second-model review (pi `gpt-sol-latest`, medium): no defects. Its coverage note (the test did not check the actual
  brushes) is addressed by the on-screen assertions.
- Surprise: a compact arc in Lucide's `zap.svg` (`a.5.5 0 01.471.666`) was parsed as flag 0 and x 1.471; flags are now
  always one digit.

Rollback: revert the PR.

### Release v2.50.5-r1 (2026-10-19)

- PR #600 squash-merged to main `0006fd0b`; annotated tag `v2.50.5-r1` there (remote check equal), draft prerelease.
- SignPath 0/0, so the six Windows files are unsigned; built on WINBRAT from a checkout verified at `0006fd0b`, size,
  SHA-256 and sidecars matched. With the tag workflows: 18 assets.
- Tag runs all success; `verify-release-integrity` success; strict gate 8 green; `check-open-p0` OK.
- Published as a prerelease; Latest stays v2.50.4. Release-event integrity, APT and Windows update test success.
- `post-ship-local.ps1 -Version 2.50.5-r1`: `POSTSHIP-LOCAL: PASS` (2 cold cycles).
- Real window on WINBRAT (published build, dark theme, the worker's own subscription): Subscribe and Servers lists show
  the mask disc for VLESS Reality, the zap disc for Hysteria2 and the globe disc for gRPC servers. Writing `theme: light`
  into the config did not switch the window to light, so the light theme is covered only by the headless capture.

Not verified: Android on a real phone; the light theme in the real window.
