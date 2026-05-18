# Phase 3 — 3A: Avalonia 11.3.12 → 12 upgrade

**Owner**: Wave 12 (sequential, single agent — UI-baseline reset needs full attention)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` §3A
**Depends on**: Wave 11 (3E Free Configs stages landed — minimize concurrent UI churn)
**Effort**: 1-2 weeks
**Risk**: HIGH (UI assemblies + headless test harness + visual baseline reset)

## Why

Audit C identifies Avalonia 11 → 12 as P1. Benefits:
- Active development: 11.3.x is on long-term maintenance only; 12.x gets new features + perf wins
- Better Skia binding (SkiaSharp 2 → 3 transitive bump, ~15% render perf)
- Better Wayland support on Linux (relevant for AppImage + .deb shipping)
- Cleaner async dispatcher (fixes documented "PageScreenshotTests + HeadlessGuiTests sequential hang" quirk in `VPNRouter.Tests/CLAUDE.md`)

## What

3 sub-tasks:

1. **Bump packages**:
   - `Avalonia` 11.3.12 → 12.x (latest stable)
   - `Avalonia.Desktop` matching
   - `Avalonia.Themes.Fluent` matching
   - `Avalonia.Headless` matching
   - `Avalonia.Headless.XUnit` matching
   - `SkiaSharp` 2.x → 3.x (transitive)

2. **Fix API breaks** per Avalonia 12 migration guide. Catalog: `dotnet build` red lines after package bump → fix each.

3. **Re-baseline `VisualDiffTests`**: existing PNG baselines at `VPNRouter.Tests/screenshots/baseline/` are tuned for Avalonia 11 + Skia 2. Pixel rendering will differ:
   - Regenerate page-dpi-bypass.png + page-telegram.png + page-tools.png
   - Verify diffs are AA / hinting noise only (no layout drift). If layout drift, that's a 12-introduced regression — investigate before accepting.

## How

**Step 1 — Branch**: create `claude/ph3a-avalonia-12` feature branch. Long-lived per methodology §1 ("Phase 3 Avalonia → long-lived `v3.0-avalonia12` branch" — actually use existing branch name from methodology).

**Step 2 — Pre-flight characterization**:
- Run `MainWindowViewModelCharacterizationTests` + `AndroidAppCharacterizationTests` on main pre-bump.
- The hash MAY drift if Avalonia 12 changes property attribute generation (e.g., `[ObservableProperty]` source generator output). If hash drifts, update the pin AFTER verifying the drift is Avalonia-12-only and not a real public-surface change.

**Step 3 — Package bump**:
```xml
<PackageReference Include="Avalonia" Version="12.0.x" />
<PackageReference Include="Avalonia.Desktop" Version="12.0.x" />
<!-- ... -->
```

**Step 4 — Build**: `dotnet build VPNRouter.sln -c Release`. Catalog errors. Fix in priority order:
- `[ObservableProperty]` attribute changes (CommunityToolkit.Mvvm + Avalonia interaction)
- `IBrush` → `Brush` namespace moves
- `DataTemplate` resolution changes
- Theme dictionary key changes (Fluent → Fluent2)
- Reactive binding API changes

**Step 5 — Tests**: `dotnet test`. Headless Avalonia harness may need updates:
- `[AvaloniaFact]` attribute compatibility
- `TestAppBuilder.cs` AppBuilder pattern may change in 12
- `CaptureRenderedFrame` API may rename

**Step 6 — Visual baseline reset**:
- Run `PageScreenshotTests` to regenerate PNGs in `screenshots/`
- Diff each against `screenshots/baseline/` — verify diffs are AA/hinting only
- Copy fresh PNGs to `screenshots/baseline/` to lock the Avalonia 12 baseline
- Re-run `VisualDiffTests` to confirm green

**Step 7 — MCP verify on running binary**:
- Build the install ZIP locally
- Install over current install
- Launch + screenshot each page
- Compare with pre-bump screenshots — ANY layout / styling drift = investigate

**Step 8 — Update `VPNRouter.App/CLAUDE.md`** + relevant Tokens.axaml notes.

## Verification gate
- [ ] Package bumps committed atomically (one commit)
- [ ] All API-break fixes committed atomically (one commit per type of break)
- [ ] Build 0 errors on Windows + Linux (Android target separately gated)
- [ ] Full scoped suite + headless suite green
- [ ] `VisualDiffTests` baseline reset documented (per-page screenshot diff inspected)
- [ ] Characterization hashes (MVM + AndroidApp) re-pinned if drift is Avalonia-only
- [ ] **Gate 5 MCP verify**: 6 desktop tabs PASS + 4 Android tabs PASS (screenshots attached to brief)
- [ ] **Gate 4 simplify**: per-fix commit < 200 LOC
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Wayland-specific issues may surface only on Linux AppImage — defer to Linux CI workflow + dogfood on `ubuntu-latest` test runner.
- If Avalonia 12 deprecates Fluent in favor of Fluent2/Material, evaluate theme migration in Phase 4 (separate task).
