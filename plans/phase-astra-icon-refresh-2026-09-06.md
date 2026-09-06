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
- [x] **Gate 1 — Build clean**: CI Release build in the `test` job completed successfully at commit `926737b8`.
- [x] **Gate 2 — Tests green**: 3,005 primary tests plus 33 Windows characterization/release-gate tests passed; Go and placeholder-fingerprint jobs also passed.
- [x] **Gate 3 — Docs**: Outcome, Astra inventory/art direction, OpenDesign design system, comparison catalog, and manifest are complete.
- [x] **Gate 4 — Independent review**: clean-context verifier findings were fixed; final verifier confirmed asset/code/security contracts with no remaining blocking findings.
- [x] **Gate 5 — UI verify**: OpenDesign links, 16/24/32/64/128 light/dark renders, SVG rasterization, UI glyph geometry, and generated platform assets were verified. Headless screenshot suites are excluded by repository CI and no prepared .NET worker is available; fixed-WINBRAT remains N/A until explicit ship authorization.
- [-] **Gate 6 — Characterization diff**: N/A — no god-file split or public API change; existing pinned source surfaces remain green.

## Outcome

**Status**: PASS with documented headless-environment limitation
**Commits**: `3631d56a` `2696e0d8` `957b7a13` `926737b8`
**Pushed**: `dsh/astra-icon-refresh`, PR `https://github.com/PavelLizunov/VPNRouter/pull/243`
**Test deltas**: existing icon contract expanded to cover both Android launcher variants and five SVG masters
**Files changed**: 45 files · +2,360 / -199 lines across the complete branch diff

**Gate results:**
- [x] Gate 1: CI restored, built, and tested the .NET 10 test project successfully.
- [x] Gate 2: 3,005/3,005 primary tests and 33/33 Windows characterization tests passed; Go and grep jobs passed.
- [x] Gate 3: OpenDesign comparison and design-system artifacts added; no README behavior change required.
- [x] Gate 4: verifier caught catalog paths, dark samples, platform containers, Android round safety, accessibility, and live text; all were corrected and rechecked.
- [x] Gate 5: preview endpoints returned HTTP 200; SVGs rendered at target sizes; PNG/ICO/ICNS structure and deterministic regeneration passed. Headless page baselines remain unexecuted because current workers lack .NET and CI explicitly excludes them.
- [-] Gate 6: N/A — visual assets and private implementation only; pinned characterization suites pass unchanged.

**Surprises encountered**:
- The shipped identity was the hand-drawn listener-with-headphones and penguin, not the generic penguin in the old design SVG.
- Runtime RGB inversion would turn the retained amber beak blue, so dedicated dark assets are now loaded directly while compatibility stubs preserve pinned source surfaces.
- Platform ICO/ICNS containers can be assembled deterministically with Python's standard library from resvg PNG frames; no runtime dependency was added.

**Follow-ups spawned**:
- Run repository `PageScreenshotTests`/`VisualDiffTests` on a prepared .NET UI worker when one is available; do not substitute the control-plane host.
- Fixed-WINBRAT live verification remains a post-ship gate and requires separate ship authorization.

**Lessons for methodology doc**:
- Design asset tasks need explicit early checks for committed platform containers, dark-theme color-preserving derivatives, and the availability of headless UI workers.
