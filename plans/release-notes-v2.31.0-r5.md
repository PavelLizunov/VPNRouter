# VPNRouter v2.31.0-r5 — F-26 toast scope fix

Hotfix follow-up to r4. MCP+UIA verification of r4 caught a scope bug
in the **F-26** Health Check toast: I'd reused the existing
`RulesToastText` Border from `NetworkPage.axaml`, which lives inside
the Rules sub-section. The Health Check menu item is reachable from
the global `…` menu on any tab, but the toast was only visible if the
user happened to be on Настройки → Правила when triggering it.

## Fix

Moved the toast `Border` from `NetworkPage.axaml` to `MainWindow.axaml`
as the last child of the root `Grid`, with `Grid.RowSpan=5`,
`HorizontalAlignment=Right`, `VerticalAlignment=Bottom`, `ZIndex=100`.
Now the toast floats above whatever page is shown.

Same `RulesToastText` binding on the `MainWindowViewModel`. Side
benefit: the rules-bulk-action feedback that originally lived on
NetworkPage (sort etc.) also becomes globally visible — small UX
upgrade for free.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 25/25 regression tests pass (no Core changes)
- MCP+UIA: trigger Health Check from any tab — toast renders bottom-right

## Cycle progress

All four pillars of v2.31.0 closed:

| Pillar | Status |
|---|---|
| 1. Core stability | r1: 7/8 (AU-9 deferred) |
| 2. A11y systemic | r2: 20/20 |
| 3. ViewModels | r3: 4/4 |
| 4. UX closure | r4: 8/8 + r5 fix to F-26 |

After r5 verification gate green (build + tests + Mac/Linux CI + 12
assets), this cycle is ready for stable cut.

## Cross-refs

- `plans/vpnrouter-v2.31.0-roadmap.md`
- `plans/release-notes-v2.31.0-r4.md` — Pillar 4 closure
- `plans/release-notes-v2.31.0-r1.md` — Pillar 1
