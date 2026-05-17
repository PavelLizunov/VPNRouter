# Phase 1 — Q12: Delete VPNRouter.Service/Worker.cs scaffold

**Owner**: Claude session-id (Wave 1)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #12, CLAUDE.md "Known Issues & Notes" item 1
**Effort**: 5 minutes
**Risk**: LOW (file is unused scaffold from `dotnet new worker` template; verified not in DI; not referenced)

## Why
`VPNRouter.Service/Worker.cs` is the default scaffold from `dotnet new worker` — created when the project was bootstrapped, never used. The actual Windows Service entry point is `VPNRouterService.cs`. CLAUDE.md has flagged this as removable for ~9 versions.

## What
Delete `VPNRouter.Service/Worker.cs`.

Also check `VPNRouter.Service/Program.cs` (or wherever DI is configured) for any `services.AddHostedService<Worker>()` registration — if present, remove that too.

Pre-check: `grep -rn "class Worker" VPNRouter.Service/` should show ONLY Worker.cs as the definition (no other class with that name). `grep -rn "Worker" VPNRouter.Service/Program.cs` or DI setup should show 0 references.

## Verification gate
- [ ] **Pre-check**: `grep -rn "Worker\b" VPNRouter.Service/` shows only Worker.cs definitions, no DI/Program.cs refs
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: `dotnet test` → all 765 pass (Service has no tests; Core tests unaffected)
- [ ] **Sanity**: `ls VPNRouter.Service/Worker.cs` returns "not found"
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome

**Status**: NO-OP — `Worker.cs` already absent.

### Pre-check findings (2026-05-17)
- `grep -rn "class Worker\b" VPNRouter.Service/` → no matches.
- `grep -rn "Worker\b" VPNRouter.Service/Program.cs` → no matches (only
  `VPNRouterService` registered via `AddHostedService<VPNRouterService>()`).
- `grep -rn "AddHostedService<Worker>"` → no matches in any source file.
- `git log --oneline --all -- VPNRouter.Service/Worker.cs` shows the file
  was already removed in commit `a002ed6` ("Migrate sniff to route rules,
  add unit tests, clean up service") — an earlier v2.27 cleanup.
- `VPNRouter.Service/CLAUDE.md` line 14 already documents this prior
  removal: "(`Worker.cs` template-scaffold удалён в одной из ранних v2.27
  чисток — он никогда не был зарегистрирован в DI и не запускался.)"
- The only repo-internal references to "Worker" in `VPNRouter.Service/`
  are in `VPNRouter.Service.csproj` `<Project Sdk="Microsoft.NET.Sdk.Worker">`
  (the .NET Hosting SDK template — required, not a Worker.cs reference)
  and the doc comment in `CLAUDE.md`.

### Build verification
- `dotnet build VPNRouter.sln -c Release` → fails, but the failure is
  `NU1605` Avalonia version downgrade in `VPNRouter.App.csproj` caused
  by a concurrent Phase 1 Q01 (Android strip) task with a dirty
  `Avalonia 11.3.12` vs `Avalonia.Diagnostics 11.3.15` mismatch.
  Pre-existing and unrelated to this task.
- Per-project builds confirm Q12 scope is clean:
  - `dotnet build VPNRouter.Core/VPNRouter.Core.csproj -c Release` → 0
    errors, 0 warnings.
  - `dotnet build VPNRouter.Service/VPNRouter.Service.csproj -c Release` →
    0 errors (41 CA1416 platform-warning noise pre-existing).
  - `dotnet build VPNRouter.CLI/VPNRouter.CLI.csproj -c Release` → 0
    errors.

### Doc changes applied (no code changes)
1. `plans/feature-catalog-2026-05-17.md` line 1016-1017: stale "Delete
   during v3.0 cleanup" bullet for Worker.cs rewritten to record the
   prior removal in `a002ed6` and mark Q12 as resolved.
2. The "Known Issues & Notes" item 1 referenced in the brief lives in
   the user's global `~/.claude/CLAUDE.md` and the legacy
   `C:/Project/CLAUDE.md` (both **outside the repo**). The repo's
   root `CLAUDE.md` is a thin pointer file — it does **not** contain
   that section. Updating the user's private global instructions is
   out of scope for this in-repo refactor task. The repo's
   `VPNRouter.Service/CLAUDE.md` already correctly documents the
   removal.

### Files modified
- `plans/phase1-q12-delete-worker-2026-05-17.md` (this Outcome section).
- `plans/feature-catalog-2026-05-17.md` (stale dead-code bullet updated).

### Files NOT modified
- `VPNRouter.Service/Worker.cs` — does not exist (verified via Glob and
  git log; deleted in `a002ed6`).
- `VPNRouter.Service/Program.cs` — no Worker references to remove.
- Root `CLAUDE.md` — does not contain "Known Issues & Notes" section
  (lives only in user's global instructions, out of repo scope).
