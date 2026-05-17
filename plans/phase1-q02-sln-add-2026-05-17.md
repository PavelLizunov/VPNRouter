# Phase 1 — Q2: Add VPNRouter.Android + tools/VpnRouterTestMcp to VPNRouter.sln

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #2, plans/dead-code-audit-2026-05-17.md §2 "Solution gap"
**Effort**: 10 minutes
**Risk**: LOW (sln file format is well-known; adding projects can't break existing project builds)

## Why
`VPNRouter.Android` and `tools/VpnRouterTestMcp` are not registered in `VPNRouter.sln`. They compile out-of-band only (Android via `dotnet build VPNRouter.Android/VPNRouter.Android.csproj` with explicit flags; MCP tool via its own csproj). Adding them to the sln makes `dotnet build VPNRouter.sln` aware of them — better IDE support, single-command rebuild, no surprise "why isn't this in the solution" moments.

Note: Android needs to be flagged `Configuration|EnableAndroidTarget` so it only builds when explicitly opted-in (matches current behavior).

## What
`VPNRouter.sln` — add 2 `Project(...) = ...` blocks for:
1. `VPNRouter.Android` (.csproj path `VPNRouter.Android\VPNRouter.Android.csproj`, GUID generated)
2. `VpnRouterTestMcp` (.csproj path `tools\VpnRouterTestMcp\VpnRouterTestMcp.csproj` if exists; otherwise note as deferred)

For Android, configure `BuildableEnabled` to false in default `Configuration|Platform` rows so it doesn't break a vanilla `dotnet build sln` without the EnableAndroidTarget flag.

## Verification gate
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors (still skips Android by default config).
- [ ] **Hook gates**: pre-commit + commit-msg both green.
- [ ] **Sanity**: `dotnet sln list` shows both projects in the solution.

## Outcome
*(filled by agent after impl)*
