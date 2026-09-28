# Review and repository cleanup

## Authorization and outcome

The owner explicitly authorized one Opus review workflow, review/fixes and merge
of PR #323 into main. The owner also requested closing all PRs and removing excess
branches to prepare a clean baseline for new development.

## Scope and invariants

- Review PR #323 at `6935b1ec0129fb57df5328b56127ecbec3d27c62`, base
  `ab97905d2455f767357ed71ef09b56fb6b545f5d`; integrate only verified fixes.
- Require exact-head green checks before merge and verify resulting main checks.
- Inventory all open PRs and local/origin branches before cleanup.
- Preserve unmerged work, user untracked files, protected main, gh-pages, tags,
  remote configuration and Harness services. Do not force-push main or erase
  unpublished work to make a status report look clean.
- Recheck branch SHA and PR state immediately before each remote deletion.
- Active or unique changes require a concrete retention/merge/closure decision;
  the general request for cleanliness does not select which product work to lose.
- No deployments, releases, worker provisioning or broad cache deletion.

## Initial inventory

Open PRs: #323 instructions, #322 FreeConfigFetcher URL validation,
#321 TgProxy URL validation, #320 hosts-editor paths, #319 Zapret process arguments,
#318 xUnit major, #317 SkiaSharp major, #316 NuGet group, #315 Actions group,
#296 draft Omarchy integration. Main is `ab97905d`; current original checkout is
Omarchy `04bdff27` with five pre-existing untracked entries.

## Verification

Read-only Opus source/diff review, coordinator validation of findings, existing
contract tests via exact-head CI, ancestry/patch-preservation evidence for cleanup,
final PR/branch inventory and status of the intended development checkout.

## Owner-selected cleanup mode

The owner selected: inspect and preserve useful changes, merge ready work, close
duplicates; propose a separate disposition for unfinished Omarchy and risky major
upgrades. Continue the explicitly authorized one-Opus review process for the
bounded remaining PR set. Do not merge red CI or turn cleanup into an unrequested
major dependency migration.

## Unknowns

Branch count alone is not deletion evidence. Retain exact tip identities and a
verified Git bundle before removing obsolete refs; preserve live gh-pages, whose
GitHub Pages source was confirmed. Any retirement of active draft work needs a
concrete owner decision. Record product findings before applying repairs.

## Outcome

PR #323 independently reviewed PASS and squash-merged to main as
`c2a1fa2e007070a4954823dcc35260f6c07137f2`; all four post-merge checks passed.
Evidence: `plans/pr323-independent-review-2026-09-28.md`.

Cleanup branch: `dsh/repository-cleanup-2026-09-28`, based on that merge.
Remaining PR snapshots fetched to `refs/pr-review/315` through `refs/pr-review/322`.
PR #316 fails Windows CLI compilation after Spectre command signature changes;
#317 and #318 have failed checks. No remaining PR or branch has been closed/deleted yet.
