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
*Wave 2 — implementation, 2026-05-17.*

**Delivered**: `.github/workflows/test.yml` (79 lines, staged but **not committed**
per Wave 2 protocol — Wave 4 integrator owns the merge).

### What was built (vs. brief §What)

- Triggers exactly as specified: `push` on `[main, v3.0-prep]`, `pull_request`
  on `[main, v3.0-prep]`, plus `workflow_dispatch`. No `paths:` filter — every
  push runs the full suite.
- Single job `test` on `ubuntu-latest` with `timeout-minutes: 15`. Job id
  matches the contract Wave 4 will PATCH into branch-protection required
  checks.
- Six steps:
  1. `Checkout` — `actions/checkout` (SHA-pinned by pre-stage hook to
     `34e114876b0b11c390a56381ad16ebd13914f8d5  # v4.3.1`; brief asked for
     floating `@v4` "Q11 will pin later", but the repo's auto-pinning hook
     ran ahead of Q11, which is harmless — net effect is what Q11 would
     have produced).
  2. `Setup .NET 8 SDK` — `actions/setup-dotnet` SHA-pinned to
     `67a3573c9a986a3f9c594539f4ab511d57bb3ce9  # v4.3.1`,
     `dotnet-version: 8.0.x`.
  3. `Restore` — `dotnet restore VPNRouter.Tests/VPNRouter.Tests.csproj`
     (single-project restore; transitive Core reference pulls itself in).
  4. `Build` — `dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-restore`.
  5. `Test` — full command as specified in brief, with the headless-Avalonia
     filter folded in:
     `--filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff"`.
     TODO comment in YAML explains the exclusion and points the future
     `xvfb-run` wrapper at this site. Confirmed test class names exist:
     `HeadlessGuiTests.cs`, `PageScreenshotTests.cs`, `VisualDiffTests.cs`
     (plus `VisualDiffHelper.cs` which xUnit will not pick up since it has
     no `[Fact]`/`[Theory]`, so the substring match is safe).
  6. `Upload test results` — `actions/upload-artifact` SHA-pinned (same
     pre-stage hook) to `ea165f8d65b6e75b540449e92b4886f43607fa02  # v4.6.2`,
     `if: always()` so failed runs still upload `.trx`, 30-day retention
     matches `build-linux.yml` convention.

### Verification (gates 1 + 1b + Sanity, run locally)

- **Gate 1 — YAML syntax**: parsed with PowerShell `powershell-yaml`
  module (`ConvertFrom-Yaml`). Top-level keys `{jobs, name, on}` resolved
  cleanly; job ids `{test}`; runs-on `ubuntu-latest`; timeout-minutes 15;
  step count 6; step names `[Checkout, Setup .NET 8 SDK, Restore, Build,
  Test, Upload test results]`; trigger branches `{main, v3.0-prep}` on
  both push and pull_request; `workflow_dispatch` present.
- Trailing whitespace scan: zero hits. Tab characters: zero hits.
  2-space indent throughout.
- **Gate 1b / Gate 2**: deferred to Wave 4 integrator — first commit to
  `main` (or `v3.0-prep`) triggers the workflow; we don't trigger it here
  (per brief constraint "DO NOT trigger the workflow yourself").
- **Hook gates**: pre-stage hook ran and produced the SHA-pinning result
  visible above (clean exit; no rejection). commit-msg hook gate will be
  exercised by Wave 4 when the commit is actually authored.

### What was deliberately NOT touched

- The other ` M` workflows in `git status` (build-android, build-free-pool,
  build-linux, build-mac, publish-apt, test-windows-update) carry
  uncommitted changes from parallel Wave 2 tasks (Q9 NuGet cache, Q11
  SHA-pinning). Left intact — those belong to their owners.
- No `gh workflow run` / `gh api PATCH` — both reserved for Wave 4 per
  brief.
- No `actions/cache` for NuGet — reserved for Q9 per brief.

### Known follow-ups (no blockers for Q8)

- Wave 4 integrator: after first green run on `main`, PATCH branch
  protection (the exact `gh api` call is in the brief's "After workflow
  exists" section).
- Future Q-series: lift the headless-suite exclusion once `xvfb-run`
  (or Avalonia.Headless display bootstrap) is wired into the workflow.
  The TODO comment at the `Test` step is the canonical reminder.
- The brief estimates Q14 (separate task) will surface an environmental
  flake on `SettingsLoader` — independent of this workflow's structure,
  noted only for traceability.

**Status**: file staged, ready for Wave 4 integrator.

**Follow-up**: Wave 4 integrator adds `test` to required status checks via `gh api PATCH` after first green run.
