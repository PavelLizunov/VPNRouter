# D-12: fixes from the full size/theme/language audit

## Why

The owner asked (2026-10-02) to check every page in every size for fit, breakage, symmetry and design errors. `tools/ui-mcp/audit.py` (M-1b) renders every
surface of the desktop UI through the UI MCP server in every inner tab (first level) at 360, 520 and 1000 px, in both themes and both languages, and tabulates the
lint (336 cells in about 3 minutes; the first, deeper run of 1760 cells also found that a long-lived probe session eventually stops answering, so the audit now
restarts the server after a 90 s silence and records the cell). Findings and what was done:

| Finding | Fix |
|---|---|
| Main tabs cut off at 360 px ("Applications", "Public" unreachable, 479 px of labels) | Compact tabs when the labels do not fit: the selected tab keeps its label, the others show their icon (tooltip is the label). Decided by measuring, so it also covers the longer Russian labels. |
| Servers: the link box shrank to ~130 px between two buttons | Box on its own full-width row, buttons below it |
| Settings > Updates: "Current version" clipped next to the check button | Button under the version, texts wrap |
| Settings > Rules: title and help header clipped | They wrap |
| Apps (RU, 360 px): "Новая категория" clipped | Smaller horizontal padding |
| DPI tab "Дополнительно" clipped at 360 px | Russian tab label "Прочее" |
| Text boxes: near-black Fluent border, different on every page, faint placeholder | Fluent TextControl resources from the tokens (quiet border, accent border on focus), placeholder in the muted token |
| White text on the accent / success fill below 4.5:1, muted text on panels | D-11 (#466, already merged) |

## Not covered / still open

- Text-at-edge, uneven-gutter and the small-target lint items (info level) were reviewed, not all changed: small targets stay (dense tool UI), no real
  asymmetry was found by eye in the contact sheets.
- Servers / Subscribe: the plain list header (Server, IP, Ping, Port) above the card and the unstructured bottom form (checkbox, combo, name and URL boxes)
  still look basic; they need a redesign, not a fix.
- Combo boxes still use the Fluent look; the Rules "domain_suffix" combo is narrow at 360 px.
- Only the first level of inner tabs was visited in the final run; the Applications categories (about 40 cells each) were sampled, not exhaustive.
- Real Windows fonts differ from the Linux fonts of the probe: overflow and overlap findings hold, pixel positions do not.

## Verification

`windows-worker` at the exact SHA: visual, headless, probe and localization suites (184 of 184 before the final layout edits; the final SHA is re-run), baselines
for Telegram re-rendered. Final audit with the rebuilt MCP binary is recorded in the Outcome.

## Outcome

Pending.
