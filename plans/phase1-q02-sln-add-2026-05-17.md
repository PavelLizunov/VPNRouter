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

**Status**: DONE (impl complete; not committed per brief constraints — `git add VPNRouter.sln` staged for caller).

### What was added to `VPNRouter.sln`
1. **`VPNRouter.Android`** — `VPNRouter.Android\VPNRouter.Android.csproj`, GUID `{1E1BC019-F81F-4AA2-93D5-4BC65B566C10}`. Listed at solution root (not nested under any folder).
2. **`VpnRouterTestMcp`** — `tools\VpnRouterTestMcp\VpnRouterTestMcp.csproj`, GUID `{5BCBA32F-85BF-4CAD-8004-3957AB8AC386}`. Nested under existing `VPNRouter.Tools` solution folder (next to `PoolAggregator`).

Both projects pre-existed; only the .sln registration was missing — confirmed via `Glob` (`VPNRouter.Android/*.csproj`, `tools/**/VpnRouterTestMcp*.csproj`).

### How Android opt-out is enforced
For Android's `{1E1BC019-...}` GUID, only the two `ActiveCfg` lines are written; both `Build.0` lines are stripped. Per the .sln format, a missing `Build.0` line means MSBuild loads the project metadata but skips compilation for that configuration. Result: `dotnet build VPNRouter.sln -c Release` evaluates the Android csproj's restore/metadata but never invokes its build target — matching prior out-of-band behavior. To build Android explicitly, callers still use the standalone command:
```
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release /p:EnableAndroidTarget=true /p:AndroidSdkDirectory=$ANDROID_HOME /p:JavaSdkDirectory=$JAVA_HOME
```

VpnRouterTestMcp has both `ActiveCfg` and `Build.0` for both configurations — it's a regular `net8.0-windows` console exe and builds by default.

### Verification gate

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors, 0 warnings, 7 projects built (Core / Service / PoolAggregator / App / CLI / VpnRouterTestMcp / Tests). Android correctly absent from the build output line list. ~1.3s elapsed (incremental, no restore needed).
- [x] **Sanity — `dotnet sln list`**: shows all 8 projects: `tools\VpnRouterTestMcp\VpnRouterTestMcp.csproj`, `VPNRouter.Android\VPNRouter.Android.csproj`, plus the original 6.
- [ ] **Hook gates**: not run (brief says DO NOT COMMIT). Caller's commit step will exercise pre-commit / commit-msg.

### Workflow note for caller (not a problem with this task)

While running this task I observed that a parallel git activity (commit `d9f7027` "revert: roll back all desktop changes to v2.32.0 stable visual state") landed during my session — at one point this reverted my in-progress `VPNRouter.sln` edits. I detected this via `git status` showing my changes had vanished after a `dotnet build` cycle, re-ran `dotnet sln add` cleanly against the new HEAD, and finished without issue. New project GUIDs were generated for the second pass (different from the first attempt's GUIDs); both rounds were equivalent — GUIDs are arbitrary identifiers. The committed result reflects the second pass.

This is **not** a blocker for Q2 but worth flagging: Phase 1 tasks running in parallel can step on each other's working trees when the orchestrator force-resets `main`. Q2 is now a single 71-line .sln modification, idempotent under re-running `dotnet sln add` if needed.

### Files modified

- `C:\Project\VPNRouter\VPNRouter.sln` — 14 line insertions (2 Project blocks, 6 ProjectConfigurationPlatforms lines, 1 NestedProjects line), staged via `git add`.
- `C:\Project\VPNRouter\plans\phase1-q02-sln-add-2026-05-17.md` — this Outcome section.
