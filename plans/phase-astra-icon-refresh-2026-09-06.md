# Phase — Astra icon refresh

**Owner**: DSH session goal `goal-597138ac-df06-4f0c-98bc-fb2ba0294666`
**Branch**: `dsh/astra-icon-refresh`
**Roadmap ref**: owner-approved standalone visual refresh
**Effort**: 1 session
**Risk**: MEDIUM
**Blast radius**: brand SVG masters, desktop application/tray assets, Android launcher assets, two Avalonia page glyphs, OpenDesign comparison artifact; visual-only runtime impact
**Rollback**: revert task commits or delete the task branch

## Why
VPNRouter currently mixes a simplified gradient SVG penguin, raster hand-drawn mascot assets, and three unrelated inline Avalonia glyph styles. The owner asked Astra to audit every product icon, choose the strongest coherent direction, generate it, and integrate it without a separate concept-selection round.

## What
- Inventory every product-owned SVG, embedded XAML vector, desktop icon, tray icon, and Android launcher asset, including actual consumers.
- Use Astra to generate and select one coherent Arctic / Glacier icon system while preserving the penguin identity and action semantics.
- Produce clean vector masters, then derive desktop PNG/ICO/ICNS and Android density assets from those masters.
- Replace the Telegram, start/play, and DPI shield inline geometries where Astra improves them.
- Add an OpenDesign comparison catalog showing old/new artwork at target sizes and on light/dark surfaces.
- Exclude screenshots, third-party application icons, fonts, historical evidence, and unrelated working-tree changes.

## How
1. Record the exact inventory, consumers, dimensions, alpha modes, and duplicate relationships.
2. Provide Astra with the current artwork, product design tokens, use contexts, and output constraints.
3. Inspect Astra output; select the strongest internally consistent candidate based on small-size legibility, brand continuity, silhouette, and platform safety zones.
4. Integrate SVG and XAML vectors; generate all derived raster/container formats reproducibly from the selected masters.
5. Add structural icon contract tests and update intentional visual baselines where required.
6. Build the comparison catalog, regenerate OpenDesign manifest, and run independent design and code verification.

### Tests written
- Icon asset contract tests: required files, image dimensions, alpha/safe-area expectations, SVG validity, and platform variant coverage.
- Existing page screenshot/visual-diff tests: Telegram and DPI pages after intentional glyph changes.

### Verification approach
Run focused icon and headless UI tests, full Release build and discovered test suite, inspect generated images at all target sizes, run independent bug/design review, and record exact results. Fixed-WINBRAT live verification is required only after a separately authorized shipped candidate; this task stops at green PR readiness.

## Verification gate
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] **Gate 2 — Tests green**: focused icon/UI tests and all discovered tests pass.
- [ ] **Gate 3 — Docs**: this brief Outcome and OpenDesign comparison catalog are complete; README changes only if the public logo presentation changes materially.
- [ ] **Gate 4 — Independent review**: bug-hunt plus clean-context design verification; all findings source-checked.
- [ ] **Gate 5 — UI verify**: deterministic headless Telegram/DPI screenshots and visual baselines pass; WINBRAT post-ship gate remains N/A until explicit ship authorization.
- [ ] **Gate 6 — Characterization diff**: N/A — no god-file split or public API change.

## Outcome (filled before final handoff)

**Status**: PENDING
**Commits**: pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results:**
- [ ] Gate 1: pending
- [ ] Gate 2: pending
- [ ] Gate 3: pending
- [ ] Gate 4: pending
- [ ] Gate 5: pending
- [-] Gate 6: N/A — visual assets and XAML geometry only

**Surprises encountered**:
- Pending.

**Follow-ups spawned**:
- Pending.

**Lessons for methodology doc**:
- Pending.
