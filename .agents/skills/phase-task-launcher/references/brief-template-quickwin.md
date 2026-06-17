# Phase 1 — <Task name> (quick-win)

**Owner**: Claude session-id <id>
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 §<#>
**Effort**: <minutes>
**Risk**: LOW (justification: <single-file, no behavior change, no test impact, etc.>)

## Why
<one or two sentences>

## What
<file(s) + line range(s) — keep small>

## Verification gate (abbreviated for quick-wins)

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Hook gates**: pre-commit + commit-msg both green (handles trailer + format + garbage-file checks)

## Outcome

**Status**: PASS / BLOCKED
**Commit**: `<hash>`
**LOC delta**: -<deleted> / +<added>
**Surprises**: <none / list>
