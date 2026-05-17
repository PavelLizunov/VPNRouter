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
*(filled by agent after impl)*

**Update**: also remove from `CLAUDE.md` "Known Issues" list — that item resolves with this commit.
