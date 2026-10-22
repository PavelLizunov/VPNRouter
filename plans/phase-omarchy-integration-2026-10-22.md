# Omarchy draft integration - 2026-10-22

## Intent and scope

Integrate the full stacked headless seams/backend/Arch packaging draft from
`origin/dsh/omarchy-packaging-2026-10-07` onto current main `c3ebc46e` in the
owner-requested isolated worktree and branch. Preserve current non-Linux behavior
and the headless-contract, headless-publish and packaging-arch CI jobs. Open one
draft integration PR superseding PRs 558-560 without modifying those PRs.

## Method, risk and rollback

Merge the stacked tip, resolve against current owning sources, inspect the full
integration diff, run local Python contracts and exact-head GitHub Actions.
Primary risks are lifecycle/privilege boundaries and main API drift. Revert the
integration commits if rejected; no merge, tag, release or workstation TUN test.
No local .NET SDK installation or .NET build/test execution.

## Verification gates

1. Build: pending GitHub Actions at the integrated SHA.
2. Tests: pending Python packaging tests and headless/full .NET CI.
3. Documentation: this record plus the requested external source-referenced report.
4. Review: direct correctness/security/test inspection; independent bug-hunt is
   unavailable in the loaded skill catalog, and no agent delegation is requested.
5. UI/runtime: N/A for integration acceptance; real Omarchy connection remains a
   separately reported gap, not inferred from tests.
6. Integration: preserve stacked content and main behavior, exact remote SHA and
   all PR checks required before handoff.

## Outcome

In progress. Final source report will distinguish executed evidence from reading.

Initial merge `7659f5f` applied without conflicts. Local packaging Python suite:
121 tests pass. Exact notice files preserve upstream whitespace (unfiltered
`git diff --check` reports notice whitespace); code-only whitespace check passes.
PR #616 is draft. Initial headless-contract job only restored the console project;
it did not execute assertions. Corrected to `timeout 8m dotnet run --project ...`.
PowerShell shim exists but cannot select a version; using `gh pr checks --watch`
with exact-head identity instead of installing tools.
