# D-19: the Zapret and Telegram proxy pages in the look of the home screen

## Why

The owner (2026-10-03, after testing Android): "rework the Zapret / Telegram proxy pages so they are as beautiful as the main one". Renders of r23 show them as the old look: a small icon tile and left-aligned title in a bordered card, a two-line text hero, plain text tabs, flat form
fields on a grey page.

## What

1. `Styles/HomeKit.axaml` (global, `hk-` classes): the home screen's status emblem, state word and sub-line, the 48 px pill button (`hk-cta`, `.stop` variant), soft button, card, label/hint, segmented control. `StatusEmblem` control (`Controls/StatusEmblem.axaml`):
   halo, track, state ring, connecting arc, disc with the page's icon and a badge; driven by `IsOn` / `IsBusy` / `IsError` / `IsWarn` bound to existing view-model booleans (no new view-model members, so the pinned public-surface hash stays).
2. DPI bypass page: emblem (on = DPI bypass enabled, busy = probing or downloading, error = antivirus block), state word and lede, one pill Enable/Disable button, the strategy picker with Run and the legend in a card, the four tabs as a segmented control, tab content in a card, the whole page scrolls.
3. Telegram proxy page: the same hero (emblem with the Telegram icon, state, lede, traffic line, pill button, Copy link when running), three-segment tabs, content card, page scrolls.
4. The segmented tabs are radio buttons (a pressed selected segment keeps its highlight, see F-5).

## Not covered

The inner tab contents (hosts lists, filters, advanced fields) keep their markup; the Tools page and the Android Zapret/Telegram screens.

## Verification

UI MCP renders of both pages (EN/RU, light/dark, 380/520/1000) and the audit matrix for the `dpi`, `telegram`, `tools` surfaces; existing page and view-model suites on windows-worker at the exact SHA; a real Windows screenshot of the next candidate.

## Rollback

Revert the PR.

## Outcome

Pending.
