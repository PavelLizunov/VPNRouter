# Phase 1 — Q6: Bump CommunityToolkit.Mvvm to 8.4.2 in App (resolve drift)

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #6, plans/nuget-audit-2026-05-17.md "Real drift: one"
**Effort**: 15 minutes
**Risk**: LOW (8.2.1 → 8.4.2 is patch-level + small minors, source-gen compat preserved; CTM has strong back-compat)

## Why
Audit C found drift: `VPNRouter.App/VPNRouter.App.csproj` pins `CommunityToolkit.Mvvm 8.2.1`, but `VPNRouter.Android/VPNRouter.Android.csproj` pins `8.4.0`. Both should be on **8.4.2** (current latest stable). Cross-version source-gen between App and Android packages can produce subtle binding/INPC differences. Align them.

## What
1. `VPNRouter.App/VPNRouter.App.csproj` — bump `CommunityToolkit.Mvvm` from `8.2.1` → `8.4.2`
2. `VPNRouter.Android/VPNRouter.Android.csproj` — bump from `8.4.0` → `8.4.2`

**Validation**: after bump, both projects build clean. The CTM source generators emit `INotifyPropertyChanged` boilerplate at compile time — any breaking change between 8.2 and 8.4 would surface as `MVVMTKxxxx` errors.

Known: Audit C noted `MVVMTK0034` warnings already exist in `MainWindowViewModel.SimpleMode.cs` lines 393/420/424 (direct field reference instead of generated property). Those warnings are pre-existing — don't fix in this task, just verify they don't escalate to errors after bump.

## Verification gate
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors (warnings OK if pre-existing)
- [ ] **Gate 2 — Tests green**: `dotnet test` → all 765 pass
- [ ] **Sanity**: `dotnet list package` on both projects shows 8.4.2
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome
*(filled by agent after impl)*
