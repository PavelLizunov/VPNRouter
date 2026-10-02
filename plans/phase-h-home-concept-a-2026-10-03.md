# Phase H: home screen concept A, "status emblem" (2026-10-03)

Branch `concept/home-a`, based on `832d4861` (tools/probe-theme-via-vm: the probe switches the theme through the view
model, so dark renders get the inverted mascot). Concept only: no PR, no merge, no release.

## Intent

Redesign the Simple mode home page (`VPNRouter.App/Views/Pages/SimplePage.axaml`) so that it reads like a VPN app:
one dominant centred action, a status hero with designed states, setup separated from action, quiet secondary rows.

## Scope

- `SimplePage.axaml` (+ `.axaml.cs` for the reduced-motion check).
- Additive view model projections in `MainWindowViewModel.SimpleMode.cs`, one wiring call in the constructor, the
  editor default `SmpFormExpanded = false`.
- New bilingual strings in `Strings.SimpleMode.cs` and their App proxies.
- Lucide icons `shield-alert`, `link` (1.49.0, unchanged SVG) and Pictogram ids `shield-check`, `shield-alert`, `link`,
  `power`.
- Probe scenarios `configured` and `alert` in `VPNRouter.Tools/UiProbe/Scenarios.cs` so the states can be rendered.

## Invariants

- The primary action stays a `Button` named exactly `Connect` / `Подключить` and `Disconnect` / `Отключить`
  (`tools/brat-stability.ps1`).
- Not touched: `MainWindow.axaml`, Servers/Subscribe pages, existing `Tokens.axaml` values, the mascot art.
- Animations: RenderTransform rotation and Opacity/Brush transitions only; reduced motion respected on Windows.

## Verification

CI `build-ui-mcp.yml` on the branch, headless renders through `tools/ui-mcp/mcpcall.py`, the audit
`tools/ui-mcp/audit.py` with zero warnings. No local builds.

## Outcome

(filled in at the end)
