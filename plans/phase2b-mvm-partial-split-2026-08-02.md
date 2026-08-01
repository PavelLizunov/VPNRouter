# Phase 2B — MainWindowViewModel partial split

**Owner**: Codex with Qwen read-only analysis
**Branch**: `codex/ph2-mvm-partial-split`
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` Phase 2B; `plans/v3.0-architecture-roadmap.md` §2A
**Effort**: 1-2 days
**Risk**: MEDIUM
**Risk rationale**: the change is intended to be a pure member relocation, but the source file owns most desktop orchestration and contains platform-conditioned members.
**Blast radius**: `MainWindowViewModel.cs` plus six new partials and existing module maps; net-zero product logic; no intended runtime or UI change
**Rollback**: `git revert <implementation-commit>` / branch delete

## Why

`VPNRouter.App/ViewModels/MainWindowViewModel.cs` is 7,692 lines, 368,574 bytes,
or approximately 102k tokens by the repository audit convention (`bytes / 3.6`).
It is the only ordinary product file above the current 50k context ceiling and
mixes six independent concerns. Moving existing members into concern partials
lets a reviewer load one complete behavior flow without loading the whole god-file.

## What

Move complete existing members, unchanged, into:

- `VPNRouter.App/ViewModels/MainWindowViewModel.CustomRules.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.SettingsPersistence.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.Zapret.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.TgProxy.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.Servers.cs`

Keep state, constructor and the smallest necessary cross-concern orchestration in
`MainWindowViewModel.cs`. Update the existing App/Android zone maps and the stale
Phase 2B/2C roadmap descriptions. Remove only false or duplicate comments about
the already-removed `CustomDirectRulesText` / `CustomDirectRulesErrorText` aliases.

```diff
- MainWindowViewModel.cs (~102k tokens, six mixed behavior concerns)
+ MainWindowViewModel.cs (~20k target, state/constructor/orchestration)
+ six concern partials (~6k-24k target each)
```

## How

1. Run the existing characterization test before editing and record the pinned result.
2. Give Qwen the full main file, existing partial inventory, zone instructions and
   this brief in read-only mode; require a symbol-level move manifest with stable anchors.
3. Validate every Qwen boundary against complete C# members, attributes, XML docs,
   `#if` blocks, initialization order and real callers.
4. Move members only. Do not add types, interfaces, services, helpers, DI or dependencies.
5. Build after each concern extraction so using/directive errors are localized.
6. Run an adversarial Qwen review over the finished diff and personally verify every claim.
7. Refresh existing module maps; do not add another architecture manifest.
8. Run all gates, fill Outcome, commit, push and open a draft PR to `main`.

### Tests written

No new behavior test is planned: this is a pure relocation and the existing
`MainWindowViewModelCharacterizationTests` plus `PublicSurfaceHashHelper` already
pin the sorted member surface on Windows and Linux. A new test is added only if
the split exposes an unpinned structural failure mode.

### Verification approach

- Pre/post `MainWindowViewModel_PublicSurface_MatchesPinnedHash` must match without
  changing either pinned hash.
- Release solution build with the repository's .NET 10 SDK.
- Full `VPNRouter.Tests` suite plus focused headless App tests.
- `git diff --check` and a symbol/reference audit for moved members.
- `ponytail-review` on the >100 LOC diff, used as the available minimality review.
- No MCP screenshot: XAML, bindings and visible behavior are intentionally unchanged.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` returns 0 errors.
- [ ] **Gate 2 — Tests green**: full suite passes; focused characterization and headless App tests pass.
- [ ] **Gate 3 — Docs**: Outcome filled; `VPNRouter.App/CLAUDE.md`, `VPNRouter.Android/CLAUDE.md` and stale Phase 2B/2C roadmap text refreshed.
- [ ] **Gate 4 — Self-review**: `ponytail-review` and read-only Qwen diff review return no surviving issue.
- [ ] **Gate 5 — MCP verify**: N/A — no XAML, binding path or visible behavior change.
- [ ] **Gate 6 — Characterization diff**: pre/post member-surface hashes match without re-pinning.

## Outcome (filled after implementation verification)

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
- [-] Gate 5: N/A — no visible UI change
- [ ] Gate 6: pending

**Surprises encountered**:
- Pending.

**Follow-ups spawned**:
- Pending.

**Lessons for methodology doc**:
- The historical `v3.0-prep` branch no longer exists. This task follows the current root policy: branch from `origin/main`, push to `origin`, open a draft PR.
