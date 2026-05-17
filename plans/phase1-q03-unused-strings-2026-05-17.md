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

**Status**: COMPLETE — files staged (not committed per brief constraint #9).

### Pre-check (grep across all 6 projects)
All 4 strings confirmed zero external references:

| Name | Occurrences outside `VPNRouter.Core/Localization/Strings.cs` |
|---|---|
| `SmpRefreshButton` | 0 |
| `SmpSaveButton` | 0 |
| `SmpSaveFirstServer` | 0 |
| `SmpSaveFirstSubscription` | 0 |

`VPNRouter.App/Localization/Strings.cs` — no matching pass-throughs found.
`VPNRouter.Android/Localization.cs` — no matching pass-throughs found.
No deletions needed in App or Android.

### Edits
**File**: `C:/Project/VPNRouter/VPNRouter.Core/Localization/Strings.cs`

1. Removed 2 single-line getters `SmpSaveButton` + `SmpRefreshButton` (lines 1293-1294 pre-edit).
2. Removed F-12 comment block (8 lines) + `SmpSaveFirstSubscription` (3 lines, with trailing blank) + `SmpSaveFirstServer` (3 lines, with trailing blank) — 16 lines including 2 blank separators.

**Total LOC removed**: 18 lines (2 getters + 8-line F-12 comment + 8 lines for the two `SaveFirst*` getters and their surrounding blanks). File: 2734 → 2716 LOC.

### Gate results
- [x] **Pre-check**: 0/4 strings had external refs → all 4 safely deletable
- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors (only pre-existing CA1416 / CS8602 warnings on unrelated files)
- [x] **Gate 2 — Tests green**: 834 passed, 5 failed, 3 skipped (842 total). **All 5 failures are pre-existing on main HEAD and unrelated to this task**:
   - 4× `VlessUriParserTests.Parse_RealityUri_*` / `TryParse_ValidUri_*` — `PlaceholderConfigException : reality.public_key matches a known placeholder fingerprint` (v2.32.3 placeholder guard triggers on the test sample URIs)
   - 1× `AppAutostartTgProxyTests.Bootstrap_IsInvokedFromConstructor` — asserts `BootstrapAutostartAsync` text in `MainWindowViewModel` constructor source (test source-text drift, unrelated)
   - Grep confirms none of the failing tests reference `Strings.`/`Localization.`/`Smp(Save|Refresh)`.
- [ ] **Hook gates**: not run (no commit per brief constraint #9 "DO NOT COMMIT")

### Files staged but not committed
- `VPNRouter.Core/Localization/Strings.cs` (-18 LOC)
- `plans/phase1-q03-unused-strings-2026-05-17.md` (this file)
