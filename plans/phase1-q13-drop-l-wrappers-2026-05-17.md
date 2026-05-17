# Phase 1 — Q13: Drop 270 dead `L_X` wrappers in MainWindowViewModel.Localization.cs

**Owner**: Claude session-id (Wave 3 — SEQUENTIAL, after Wave 1 + Wave 2)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #13, plans/dead-code-audit-2026-05-17.md §"Dead L_ wrappers"
**Effort**: 1 hour
**Risk**: MEDIUM (XAML binding paths may reference some L_ wrappers via `{Binding L_FooBar}`; audit said 270/531 have ZERO refs, but pure grep can miss dynamic XAML binding errors that only surface at runtime)

## Why
Audit A: `VPNRouter.App/ViewModels/MainWindowViewModel.Localization.cs` has 531 `L_X` getter wrappers. 270 of them have ZERO references outside the file itself (~51% dead). They were added defensively at some point and never wired up. Removing buys back 270 LOC and reduces visual noise when reading the file.

## Why SEQUENTIAL (not Wave 1 parallel)
This is the only Phase 1 task with MEDIUM risk. XAML bindings can reference `L_X` properties via `{Binding L_FooBar}` strings — those don't show up in grep against C# code, but DO show up in `.axaml` files. Must verify by greping both .cs AND .axaml across all .App + .UI projects.

Also: PageScreenshotTests + HeadlessGuiTests + VisualDiffTests must all pass after — these are the regression net for binding errors.

## What
1. **Inventory**: walk `MainWindowViewModel.Localization.cs` and list every `L_X` getter. Expected: 531.
2. **Reference check**: for each `L_X`, grep across:
   - `VPNRouter.App/**/*.cs`
   - `VPNRouter.App/**/*.axaml`
   - `VPNRouter.UI/**/*.cs` (if exists)
   - `VPNRouter.UI/**/*.axaml`
   - `VPNRouter.Tests/**/*.cs`
   - Exclude self-reference in `MainWindowViewModel.Localization.cs`
3. **Build kill-list**: every `L_X` with 0 refs OUTSIDE the def file → safe to delete
4. **Cross-check with audit**: audit said ~270. Our number should be close (±5). If wildly different (e.g. 100 or 400), STOP and investigate.
5. **Delete in batches**: 30 wrappers per batch, run build + tests between batches. If a batch breaks something, narrow down.
6. **Final run**: full build + headless GUI tests + page screenshot tests + visual diff tests

## Verification gate
- [ ] **Pre-check**: inventory count matches audit (~531 total, ~270 dead)
- [ ] **Pre-check**: cross-reference grep includes .axaml files
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: `dotnet test` → all 765 pass
- [ ] **Gate 2b — Headless tests green**: HeadlessGuiTests + PageScreenshotTests + VisualDiffTests all pass
- [ ] **Gate 5 — MCP verify**: launch VPNRouter.App, screenshot Simple page + Advanced shell, verify no `{}` or `<binding error>` text visible in UI
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome
*(filled by agent after impl)*

**Sequence**: this task runs LAST in Phase 1 because:
- Other Phase 1 tasks land first → tests stay clean baseline
- Q8 (dotnet test workflow) MUST be merged + green before this — gives CI safety net
- Q13 is the most likely to break something subtle; isolating it makes regression source easy to spot
