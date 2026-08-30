---
name: phase-task-launcher
description: Start every v3.0 task or change over 30 lines with a brief, task branch, verification, outcome, commit, push and PR.
whenToUse: Before v3.0/refactor work, audit follow-ups, or any approved code change over 30 lines. User-reported release hotfixes use the release workflow instead.
---

# Phase task launcher

Read `docs/agent-contract.md`, the relevant zone document and `plans/v3.0-execution-methodology.md` before implementation.

## Required lifecycle

1. Create a `dsh/` task branch from the accepted base. Never work directly on `main`, regardless of phase or apparent risk.
2. Create or complete `plans/phaseN-<slug>-<date>.md` with Why, What, How, risk, rollback, tests and six tailored gates. Commit the brief first on that task branch.
3. Implement the smallest scoped change. Behavior changes require tests; god-file splits require a pre-change characterization baseline.
4. Verify all applicable gates:
   - Release solution build, zero errors;
   - focused tests plus the full discovered suite;
   - documentation and brief Outcome;
   - `bug-hunt` with distinct correctness/test/security lenses for non-trivial, >100-line, public-API, process/file/network/firewall or release changes;
   - UI changes on fixed WINBRAT only, never the developer machine;
   - exact characterization match for mechanical splits.
5. If any applicable gate fails, stop, fix it and rerun. Do not commit a known failure or replace an unavailable skill/test/VM with a weaker local claim.
6. Fill Outcome with files/delta, test counts, review findings, surprises, follow-ups and rollback.
7. Commit without bypassing hooks, immediately `git push -u origin HEAD`, open or update the PR to `main`, then run `tools/verify-last-commit-ci.ps1` and wait for green before another code block.

The brief may be abbreviated only for a <=5-line, single-file, no-behavior documentation/comment change. Release/tag/merge authority is never implied by this skill.
