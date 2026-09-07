# UI pictogram catalog — approved 2026-09-07

## Why
The earlier task incorrectly redesigned the brand. The owner rejected that work and approved an interface-pictogram-only catalog, with integration requiring separate approval.

## What
Audit every Desktop/Android interface pictogram: SVG, XAML Geometry/Path/PathIcon/DrawingImage, and text symbols/emoji used as icons. Group equivalent actions without losing consumer references. Astra chooses one clean, technical, familiar monochrome vector per semantic group, matching existing Arctic/Glacier tokens.

Outputs: `opendesign/mockups/ui-icon-catalog/icon-catalog.html`, `inventory.json`, and `proposed/*.svg`. Each entry shows current name, purpose, screen, source file, old/new icon, 16/20/24/32 sizes, light/dark themes, and default/hover/active/disabled states where interactive. Filters cover screen, purpose, size, theme and state.

## Invariants and exclusions
Product source is read-only. Logos, mascot, app/tray icons, PNG/ICO/ICNS and Android launcher assets must remain byte-identical. No UI integration, dependencies, releases or merges. Preserve unrelated checkout changes. Historical design-system descriptions do not override this scope.

## How
1. Create isolated branch from origin/main, commit brief and await CI.
2. Inventory product sources read-only with exact source locations; lead verifies completeness and semantics.
3. Build a self-contained interactive comparison catalog and proposed vectors; never replace originals.
4. Verify parse/link/schema coverage, controls and themes in a clean-context review; verify product and brand diff is empty.
5. Publish preview and evidence for owner inspection; stop before integration.

## Risk and rollback
Low production blast radius: only preview artifacts and task documentation may change. Main risk is incomplete inventory or confusing icon metaphors. Mitigate with source traceability and independent review. Rollback is closing the task PR; no production rollback is needed.

## Six tailored verification gates
- Build: catalog HTML/JS/SVG syntax and local link checks; solution/APK builds N/A because product code is unchanged.
- Tests: inventory coverage and uniqueness, all specified sizes/themes/states, source-reference validity; exact PR CI checks.
- Documentation: approved scope, complete inventory and truthful Outcome.
- Independent review: coverage, recognizability, accessibility and scope preservation; source-check every finding.
- UI: catalog controls and isolated preview verified; product screenshots/WINBRAT N/A because no product change or ship.
- Characterization: empty product/brand/platform diff against base; no API change.

## Outcome
IN PROGRESS. No completion or visual verification claimed yet.
