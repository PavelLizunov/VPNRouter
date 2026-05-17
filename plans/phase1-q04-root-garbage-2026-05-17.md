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
*(filled by agent after impl)*
