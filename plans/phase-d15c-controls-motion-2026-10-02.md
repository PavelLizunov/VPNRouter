# D-15c: one look for inputs, small motion

## Why

Third step of the D-15 redesign (owner: the UI must look clearly finished). Input controls differed page by page
(24 to 34 px high, square or rounded, near-black or faint borders) and nothing moved.

## What a person sees

| Area | Before (r18) | After |
|---|---|---|
| Text boxes and combo boxes | 22 to 34 px, per-page padding, Fluent borders | 30 px everywhere (the per-page `MinHeight="0"` overrides removed), 8 x 5 padding, 11 px text in combos, 6 px radius, a visible border on the base surface (about 3:1), accent border on hover and focus, raised drop-down (`Styles/ControlRoles.axaml`, Fluent resource keys, light and dark) |
| All Fluent controls | 4 px corners | `ControlCornerRadius` 6 px, overlays 8 px |
| Tabs, buttons, server rows | colours jump | colours fade (120 to 150 ms brush transitions) |
| Connecting status dot | 1.2 s pulse to 55 % | same pulse to 40 %, now off when the system asks for reduced motion |

Motion lives in `Styles/Motion.axaml`, added by `App.Initialize` only when `Services/UiMotion.Allowed()` (Windows
"Show animations in Windows" via `SPI_GETCLIENTAREAANIMATION`, or `VPNROUTER_REDUCED_MOTION=1`). Only Background,
BorderBrush and Opacity animate, so nothing invalidates layout during a render (D-3). Check boxes and radio buttons
keep the Fluent shapes in the app accent (D-2).

Re-pinned: `VisualDiffTests` baselines `page-dpi-bypass.png` and `page-tools.png` (their combo boxes and text boxes
change on purpose).

## Verification

- UI MCP built at the branch SHA: renders read; `audit.py --tabs 1 --widths 360,520,1000`: 468 cells, 0 warnings.
- Windows worker at `61a6450c`: 112 of 114, the two failures are the two baselines above (re-pinned from that run).
- PR CI.
