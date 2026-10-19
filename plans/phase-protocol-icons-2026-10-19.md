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

(to be filled)
