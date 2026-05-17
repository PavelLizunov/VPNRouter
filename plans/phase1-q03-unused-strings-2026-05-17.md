# Phase 1 — Q3: Delete 4 unused Core strings

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #3, plans/dead-code-audit-2026-05-17.md §5 "Unused Core strings"
**Effort**: 10 minutes
**Risk**: LOW (audit A verified zero references via grep; delete is safe)

## Why
Audit A walked `VPNRouter.Core/Localization/Strings.cs` (2734 LOC) and found exactly 4 getters with zero references anywhere in the codebase: `SmpRefreshButton`, `SmpSaveButton`, `SmpSaveFirstServer`, `SmpSaveFirstSubscription`. Removing them shrinks the god-file marginally and removes dangling Ru/En translations.

## What
`VPNRouter.Core/Localization/Strings.cs` — remove 4 getter blocks (each ~3-4 lines) for:
- `SmpRefreshButton`
- `SmpSaveButton`
- `SmpSaveFirstServer`
- `SmpSaveFirstSubscription`

**Validation step**: BEFORE deleting, agent must `grep -rn` for each name across:
- `VPNRouter.Core/**/*.cs`
- `VPNRouter.App/**/*.{cs,axaml}`
- `VPNRouter.Android/**/*.cs`
- `VPNRouter.CLI/**/*.cs`
- `VPNRouter.Service/**/*.cs`
- `VPNRouter.Tests/**/*.cs`

If ANY reference found → STOP, do not delete, report to integrator. Audit A may have missed one.

Also check `VPNRouter.App/Localization/Strings.cs` for cascading pass-through wrappers (e.g. `L_SmpRefreshButton`); remove those too.

Also check `VPNRouter.Android/Localization.cs` for the same; remove if present.

## Verification gate
- [ ] **Pre-check**: grep across all 6 projects returns 0 references for each of the 4 strings
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release` → all pass (~765)
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome
*(filled by agent after impl)*
