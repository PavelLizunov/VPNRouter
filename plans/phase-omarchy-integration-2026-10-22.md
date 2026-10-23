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

1. Build: CI headless publish, Core/test-project compilation and Android compile pass at code head 96b80782; final exact-head rollup is retained in PR #616.
2. Tests: 121 local Python tests pass; real console contracts and Linux regression job pass at 96b80782. Windows/final-checkpoint evidence is retained in the PR rollup and external report.
3. Documentation: this record plus the requested external source-referenced report.
4. Review: direct correctness/security/test inspection; independent bug-hunt is
   unavailable in the loaded skill catalog, and no agent delegation is requested.
5. UI/runtime: N/A for integration acceptance; real Omarchy connection remains a
   separately reported gap, not inferred from tests.
6. Integration: preserve stacked content and main behavior, exact remote SHA and
   all PR checks required before handoff.

## Outcome

Integrated all three draft branches by merge; all three original tips are ancestors
of the integration branch. Main base c3ebc46e and the original dirty checkout/index
were preserved. Draft PR: https://github.com/PavelLizunov/VPNRouter/pull/616.

Follow-up fixes execute the previously inert console CI suite, reconcile removed
DNS override tests, align Linux profile catalogs, and isolate firewall lifecycle
fixtures from platform defaults. At code head 96b80782, headless-contract (37
aggregate/top-level checks), headless-publish, packaging-arch, Linux test, Go,
Android compile and grep pass; Windows characterization is still running at this
recording point. The final documentation checkpoint will be monitored again via
`gh pr checks 616 --watch`; the PR exact-head rollup and owner-requested external
report retain the final verdict and SHA.

The source report is delivered to the owner's requested scratchpad path ending
`scratchpad/codex/r1-backend.md`, with protocol, Linux launch/privilege model,
process routing, package/runtime pins and numbered estimates. Deferred gaps are
recorded in OPEN-DEFECTS.md. No install, live TUN or Quattro load was performed.
No main merge, tags, releases or edits to PRs 296/558/559/560. Rollback remains
reverting/rejecting the isolated task branch, with no live deployment to undo.

Initial merge `7659f5f` applied without conflicts. Local packaging Python suite:
121 tests pass. Exact notice files preserve upstream whitespace (unfiltered
`git diff --check` reports notice whitespace); code-only whitespace check passes.
PR #616 is draft. Initial headless-contract job only restored the console project;
it did not execute assertions. Corrected to `timeout 8m dotnet run --project ...`.
PowerShell shim exists but cannot select a version; using `gh pr checks --watch`
with exact-head identity instead of installing tools.

Real headless execution at `ffbfcae2` exposed compile failures for removed DNS
overrides and the missing isolated profile-source seam. Tests now assert the
current unsupported override response, no mutation/revision change, profile DNS
generation and custom-config semantics. Core gains an optional data directory
for source isolation, and Linux generic catalogs follow platform catalogs as in
the headless offline helper; Windows/macOS priorities remain unchanged.

At `97245a82`, real headless runner: 37 top-level checks pass; headless publish,
packaging, grep and Go pass. Linux full suite exposed four catalog-dependent
lifecycle tests (3185 passed, 4 failed, 77 skipped): they relied on the Windows
Discord profile's firewall flag. Tests now provide isolated explicit local profile
intent, keeping the intended firewall teardown/cancellation assertions intact.
This is task-owned regression repair, not a pre-existing-main failure waiver.
