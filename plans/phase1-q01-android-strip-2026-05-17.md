# Phase 1 — Q1: Strip Android.csproj dead UI ref

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #1, plans/dead-code-audit-2026-05-17.md §1, plans/v3.0-architecture-roadmap.md §"Dead reference"
**Effort**: 5 minutes
**Risk**: LOW (build is already passing — the dead reference glob silently matches nothing today, so removing it is purely cosmetic; no behavior change)

## Why
`VPNRouter.Android.csproj` lines 91-95 source-link `..\VPNRouter.UI\**\*.cs` to a directory that was reverted on 2026-05-09. The glob silently matches zero files, but it pollutes the csproj and makes future readers wonder if `VPNRouter.UI/` was supposed to exist. Two audits flagged this (D + A). Cleanup time.

## What
`VPNRouter.Android/VPNRouter.Android.csproj` — remove the `<Compile Include="..\VPNRouter.UI\**\*.cs"/>` block (lines 91-95 per audit, exact location TBD on read). Also remove any `<AvaloniaResource Include="..\VPNRouter.UI\Controls\**\*.axaml"/>` if present.

## Verification gate (abbreviated for quick-wins)
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors. (Skip Android build since hooks don't trigger it.)
- [ ] **Hook gates**: pre-commit + commit-msg both green.

## Outcome

**Status**: PASS
**Commits**: pending (integrator will commit)
**Pushed**: pending (integrator)
**Test deltas**: +0 / -0 (no test surface touched — purely cosmetic csproj cleanup)
**Files changed**: 1 · -16 LOC (15-line dead block + 1 preceding blank line removed)

**Gate results:**
- [x] Gate 1 — Build clean: `dotnet build VPNRouter.sln -c Release` → 0 errors, 67 warnings (all pre-existing CS8602/CA1416/MVVMTK0034 in App + Tests, none csproj-related). Time elapsed 00:00:24.71 first run, 00:00:05.23 incremental.
- [x] Hook gates: pending integrator (this agent does not commit per brief instructions).

**Surprises encountered**:
- Dead `<Compile Include="..\VPNRouter.UI\**\*.cs"/>` + `<AvaloniaResource Include="..\VPNRouter.UI\Controls\**\*.axaml"/>` blocks were exactly where the audit predicted (csproj lines 91-95 plus the multi-line Phase G comment header above them at 81-90). Confirmed `VPNRouter.UI/` directory absent on disk — the globs were silently matching zero files at build time.
- Working tree at session start had unrelated drift: `CommunityToolkit.Mvvm` bumped 8.4.0→8.4.2 in Android.csproj, `Avalonia.Diagnostics` 11.3.12→11.3.15 + `CommunityToolkit.Mvvm` 8.2.1→8.4.2 in App.csproj, and a `ZXing.Net` 0.16.10→0.16.11 flicker. Reverted the version bumps inside Android.csproj so the staged diff contains ONLY the dead-glob removal. App.csproj drift was left untouched per the READ-ONLY constraint.
- The dead-glob block lived inside the same `<ItemGroup>` as the live Core source-link (lines 56-79). Removed the UI half plus its preceding Phase G comment, kept Core source-link intact, closed the ItemGroup cleanly.

**Follow-ups spawned**:
- None.

**Lessons for methodology doc** (if any):
- None — quick-win brief format worked as intended for cosmetic cleanup. Worth noting that NuGet auto-restore can drift package versions in the working tree between session start and edit — quick-win briefs should encourage agents to spot-check `git diff --cached` for unrelated drift before declaring done.
