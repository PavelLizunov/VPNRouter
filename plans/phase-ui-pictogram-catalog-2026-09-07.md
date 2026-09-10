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
Catalog prepared for owner review; no product integration. 48 proposed SVGs, 481 exact source references in active groups, and four appendix groups for preserved/dynamic/source-only cases.

Executed evidence:
- `python3 opendesign/mockups/ui-icon-catalog/verify-catalog.py`: PASS (safe monochrome SVGs, exact source excerpts, source locations, local links, original primitive representations, screen mappings, empty product diff).
- `node opendesign/mockups/ui-icon-catalog/browser-check.mjs`: PASS in isolated Chromium 151 for all 32 combinations (two themes, four sizes, four states), filters/reset/empty results and widths 360/768/1440.
- Independent visual review inspected all 48 proposals and size strips plus dark hover/active/disabled. Original-geometry/resource-label overflow and screen-metadata findings corrected; final split-metadata readback PASS; no remaining blocking review findings.
- `git diff b7ce0e4f --exit-code -- VPNRouter.App VPNRouter.Android VPNRouter.Core VPNRouter.CLI VPNRouter.GUI design/project/assets`: PASS, no changes.
- Brief PR #246 CI: test, Windows characterization, Go and grep passed. Implementation-head CI recorded in final handoff.

Six gates: catalog syntax/build PASS; targeted browser/source tests PASS; documentation PASS; independent review PASS after final metadata readback; catalog visual checks PASS with limits; product/brand characterization PASS (empty diff). Native solution/APK/WINBRAT tests N/A: no production source or asset changes, no ship.

Limits: existing Unicode appearance depends on installed font; dependency-owned notification/widget originals use honest descriptions. Country flags remain country metadata. Source audit does not enumerate arbitrary user-provided text or third-party widget internals. No claim of native Avalonia raster equivalence or formal usability certification.

Review fixes and source-coordinate correction are recorded in task-worktree OPEN-DEFECTS.md. Original dirty checkout remains untouched. Owner must approve the visual proposals before a separate integration task.
