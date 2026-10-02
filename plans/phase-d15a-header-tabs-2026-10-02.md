# D-15a: brand area and main tab strip

## Why

Owner feedback on r17/r18: apart from the tab icons no visible improvement, and the tab icons look unfinished. He
wants a clearly visible, finished desktop UI. This pass starts with the most visible part: the header and the main
tabs (D-15b: Servers and Subscribe, D-15c: controls, D-15d: motion).

## What a person sees (before -> after, UI MCP renders at 360, 520 and 1000 px, light and dark, English and Russian)

| Area | Before (r18) | After |
|---|---|---|
| Brand | 28 px raw mascot on a pale tint; in dark the dark art sat on a dark tile | 40 px app tile (the mascot art on its light tile from `tools/icons/make-icons.py`, art unchanged) with a soft shadow and a hairline border; reads in both themes |
| Title | 12 px | 14 px bold, chips one line below |
| Status chips (VPN, Zapret, TG) | 9 px text, 1 px padding | one chip shape: 20 px high, 10 px semibold, 8 px side padding |
| Simple / Advanced switch | accent text, no shape | tonal pill (accent tint, stronger on hover) |
| Main tabs | 14 px icon, selected = pale tint on a transparent strip | 16 px icon, 6 px gap, 32 px high; the selected tab is a solid accent pill with white (light) or dark (dark) label and icon and a soft shadow; hover tint, pressed stronger tint |
| Tab strip | transparent row under the header | its own bar on the base surface with a bottom rule, 4 px between tabs |

The compact rule (`MainWindow.ShouldCompactTabs`, fixed widths per language, no layout measuring) is unchanged; in
compact mode the unselected tabs still show only their icon (the local `Spacing` on the tab content was removed, it
had overridden the compact style).

## Verification

- UI MCP binary built by `build-ui-mcp.yml` at the branch SHA (workflow dispatch): before (r18 binary) and after
  renders read; `tools/ui-mcp/audit.py --tabs 1 --widths 360,520,1000`: 468 cells, 0 warnings.
- Windows worker, exact SHA: `MainWindowCompactTabsTests`, `HeadlessGuiTests`, `VisualDiffTests`, window design
  captures.
- PR CI.

## Outcome

Windows worker (preflight CPU 3 %, 13.1 GB free RAM, 11.0 GB free disk, no dotnet or java running), exact SHA `18474267`:
37 of 37 (`MainWindowCompactTabsTests`, `HeadlessGuiTests`, `VisualDiffTests`, window captures; no baseline changed).
UI audit at `2ca5927c`: 468 cells, 0 warnings. Not seen: hover with a real pointer.
