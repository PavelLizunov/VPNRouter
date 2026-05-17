# Phase 1 — Q8: Add `dotnet test` GitHub Actions workflow

**Owner**: Claude session-id (Wave 2)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #8 (BIGGEST VELOCITY GAIN), plans/ci-audit-2026-05-17.md "Key findings: NO test workflow"
**Effort**: 1 hour
**Risk**: MEDIUM (new workflow can be flaky on first runs; mitigate by initial `continue-on-error: true` for a few runs to baseline)

## Why
Audit F's biggest finding: 765 tests exist but **run only locally**. No CI workflow executes them on PR/push. This means:
- Any commit can break tests undetected until next local run
- Refactoring confidence is zero — can't trust "build green" alone
- Phase 2 god-file splits CANNOT begin safely without this gate

This is the highest-impact Phase 1 task. Closes a regression class.

## What
Create `.github/workflows/test.yml`:

```yaml
name: dotnet test

on:
  push:
    branches: [main, v3.0-prep]
  pull_request:
    branches: [main, v3.0-prep]
  workflow_dispatch:

jobs:
  test:
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@v4  # pin to SHA after Q11
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - name: Restore
        run: dotnet restore VPNRouter.Core/VPNRouter.Core.csproj VPNRouter.Tests/VPNRouter.Tests.csproj
      - name: Build
        run: dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-restore
      - name: Test
        run: dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --logger "trx;LogFileName=test-results.trx" --logger "console;verbosity=normal"
      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ github.sha }}
          path: VPNRouter.Tests/TestResults/*.trx
          retention-days: 30
```

Job id `test` (per Audit F finding — must match what GH branch protection requires; the protection check is currently `verify` from verify-release-integrity, but we'll add `test` to required checks once this workflow has 1 green run).

**Special handling for headless Avalonia tests**:
- `HeadlessGuiTests`, `PageScreenshotTests`, `VisualDiffTests` use Avalonia.Headless + Skia
- Linux runner needs `xvfb` if Avalonia uses any display calls
- Likely OK since `UseHeadlessDrawing=false + UseSkia()` is offscreen
- If first run fails on Linux due to display, add a `xvfb-run` wrapper OR mark those tests with `[Platform("Windows")]` (already partial — see CLAUDE.md note)

**Path filter**: NO `paths:` filter on this workflow — every push must verify all tests. (Don't repeat the test-windows-update.yml mistake of hardcoded `paths` that breaks during refactor.)

**Caching**: Q9 will add `actions/cache` for NuGet in a separate task. Don't add here; Q9 owns the cache contract.

## After workflow exists
- Push commit → workflow runs once on main → green
- Then PATCH branch protection to add `test` as required check:
  ```
  gh api -X PATCH repos/PavelLizunov/VPNRouter/branches/main/protection/required_status_checks \
    -f checks[][context]=verify -f checks[][context]=test
  ```
- Note: that PATCH happens in the integration step (Wave 4), not in this task

## Verification gate
- [ ] **Gate 1 — Build clean**: yaml syntax valid (no Yaml-Lint errors)
- [ ] **Gate 1b — Workflow runs**: triggered by this commit; finishes successfully
- [ ] **Gate 2 — Tests green** (in CI): 765/765 pass
- [ ] **Sanity**: `gh run list --workflow="dotnet test" --limit 1` shows latest as completed-success
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome
*(filled by agent after impl)*

**Follow-up**: Wave 4 integrator adds `test` to required status checks via `gh api PATCH` after first green run.
