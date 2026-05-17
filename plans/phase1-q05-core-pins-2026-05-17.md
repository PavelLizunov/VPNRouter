# Phase 1 — Q5: Remove redundant Core.csproj explicit pins

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #5, plans/nuget-audit-2026-05-17.md "Tidy-up"
**Effort**: 5 minutes
**Risk**: LOW (SDK pulls these transitively at the same version; removing the explicit pin doesn't change build output)

## Why
`VPNRouter.Core/VPNRouter.Core.csproj` has explicit `PackageReference` entries for `System.Management 8.0.0` and `Microsoft.Win32.SystemEvents 8.0.0`. The .NET 8 SDK / Microsoft.WindowsDesktop.App reference set already pulls these in transitively at the SAME version. Explicit pin is redundant and causes confusion about which version is authoritative.

## What
`VPNRouter.Core/VPNRouter.Core.csproj` — remove:
- `<PackageReference Include="System.Management" Version="8.0.0" />` (if explicitly pinned)
- `<PackageReference Include="Microsoft.Win32.SystemEvents" Version="8.0.0" />` (if explicitly pinned)

**Pre-check**: verify they ARE explicitly pinned (read csproj first). If audit C was wrong and they're not pinned, document that finding and proceed to no-op.

**Validation**: after removal, `dotnet build` should still resolve these transitively to the same version (8.0.0). Check via `dotnet list package --include-transitive | grep -E 'System.Management|Microsoft.Win32.SystemEvents'`.

## Verification gate
- [ ] **Pre-check**: read VPNRouter.Core.csproj, confirm pins exist
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: `dotnet test` → all pass
- [ ] **Sanity**: `dotnet list VPNRouter.Core/VPNRouter.Core.csproj package --include-transitive` shows both packages still resolved
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome
*(filled by agent after impl)*
