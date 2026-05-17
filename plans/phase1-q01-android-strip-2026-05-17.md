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
*(filled by agent after impl)*
