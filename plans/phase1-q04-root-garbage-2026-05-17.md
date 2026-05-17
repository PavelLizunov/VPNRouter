# Phase 1 — Q4: Delete root garbage files

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #4, plans/dead-code-audit-2026-05-17.md §8 "Stale top-level items"
**Effort**: 15 minutes
**Risk**: LOW (all targets are untracked-or-stale, audit verified)

## Why
Root directory accumulates project artifacts that aren't real source. Audit A flagged: `test_parser.csx` (one-off C# script, never referenced), `build-stable.log` + `build-v2.32.log` (build outputs left behind), `LOCALIZATION_PLAN.md` (planning doc from work that completed), `docs/SSH_FALLBACK_PLAN.md` (misnamed dir from earlier work). Removing them cleans repo root.

## What
Delete these files (if they still exist — some may have been removed earlier):

1. `test_parser.csx` (already removed in v3.0 enforcement commit — verify gone)
2. `build-stable.log` — root log file
3. `build-v2.32.log` — root log file
4. `LOCALIZATION_PLAN.md` — completed planning doc
5. `docs/SSH_FALLBACK_PLAN.md` — if directory `docs/` is otherwise empty after this, remove it too. Check first.

DO NOT touch:
- `README.md`, `README.ru.md`, `README-VM.md` — current docs
- `CLAUDE.md`, `CLAUDE.local.md` — methodology docs (gitignored or tracked)
- `LICENSE`, `.gitignore`, `.gitattributes` — repo metadata
- `build.ps1`, `build-mac.sh`, `build-linux.ps1`, `build-singbox.ps1` — build scripts
- `Setup-Hooks.ps1` — newly-added enforcement
- `setup-vm.ps1` — VM bootstrap script
- Any `*.png`, `*.ico` — branding assets

## Verification gate
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors (build won't depend on docs anyway)
- [ ] **Hook gates**: pre-commit + commit-msg both green (no garbage-file regrowth check fires)
- [ ] **Sanity**: `ls C:/Project/VPNRouter/*.log` returns empty

## Outcome

Completed 2026-05-17 (Phase 1 Wave 1, Q4 agent).

### Files acted on

| Target | Pre-state | Action | Post-state |
|---|---|---|---|
| `test_parser.csx` | already absent (removed in v3.0 enforcement commit) | none — verified gone | absent |
| `build-stable.log` | present, **untracked** | `rm` (worktree only) | absent, no git entry |
| `build-v2.32.log` | present, **untracked** | `rm` (worktree only) | absent, no git entry |
| `LOCALIZATION_PLAN.md` | present, **tracked** | `git rm` (staged delete) | absent, `D ` in index |
| `docs/SSH_FALLBACK_PLAN.md` | present, **tracked** | `git rm` (staged delete) | absent, `D ` in index |
| `docs/` directory | contained only `SSH_FALLBACK_PLAN.md` | removed via `rmdir` after file delete | gone |

Content sanity-check before deletion: `LOCALIZATION_PLAN.md` was a step-by-step
EN/RU localization planning doc (never executed against current code — there
is no `Strings.cs` matching its shape on disk now). `SSH_FALLBACK_PLAN.md`
was a transport-fallback design doc from Apr-22; not referenced anywhere in
code, and only mentioned in the four audit/plan files that flagged it for
removal. Both safe to delete.

### Verification gate

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors, 94 warnings. Warnings are unrelated MSB3026 file-lock noise from a parallel wave running `dotnet test` (testhost PID 10280 holding `VPNRouter.Core.dll` / `CommunityToolkit.Mvvm.dll` in `VPNRouter.Tests/bin/Release/`). Build itself completed: `0 Error(s)`.
- [x] **Sanity — no root `*.log`**: `Get-ChildItem -Filter "*.log"` returns empty.
- [ ] **Hook gates (pre-commit, commit-msg)**: NOT run — brief instructs "DO NOT COMMIT". Hooks will run at commit time when the integrator commits.

### Concurrency notes

Multiple Phase 1 waves are running in parallel. Two `git reset: moving to HEAD`
events fired in `git reflog` during this task — they reset my staged
deletions back twice. Re-applied each time. Other concurrent unstaged
modifications observed (Android.csproj, App.csproj, Core.csproj, CLI.csproj,
Service.csproj, sln, `plans/phase1-q01-android-strip-2026-05-17.md`,
`plans/feature-catalog-2026-05-17.md`) belong to other waves and were left
untouched.

### Final git state (Q4 contribution only)

```
D  LOCALIZATION_PLAN.md
D  docs/SSH_FALLBACK_PLAN.md
```

Worktree-only deletions (no git entry, expected for untracked files):
- `build-stable.log` — gone
- `build-v2.32.log` — gone
- `docs/` — empty dir removed
