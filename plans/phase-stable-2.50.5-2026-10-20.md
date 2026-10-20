# Stable 2.50.5 from candidate 2.50.5-r3

## Authority and scope

The owner tested r3 and explicitly authorized stable publication: "Everything
seems fine, you can release." Cut stable 2.50.5 with no new product behavior.
Since 2.50.4: protocol icons in server lists and compact home mode, and the
Russian IP bypass correction. Website help is already deployed independently.

Base main 0faa1a19 differs from candidate 6b1304ec only in documentation and an
offline UiProbe fixture/test, not shipped application behavior. Keep the original
worktree/branch/index intact. No retention cleanup or mirror publication.
Rollback is the previous immutable stable release, not retagging.

## Preconditions and checks

Candidate: 18 assets, all platform/tag/test/update/integrity/APT runs green,
strict verifier 9 green/0 red, post-ship two cold cycles PASS, 0 lifecycle errors.
The one-line product fix passed full Windows tests (3457/19 skipped) and real
core IP routing. Previous-stable 2.50.4 -> r3 real updater with two cycles is
running before the stable tag. No open P0/P1, no waiver.

Worker preflight 2026-10-20: WINBRAT, 8 logical CPUs, 1% CPU, 13.4 GiB free
RAM, 118.6 GiB free disk, private SDK and Inno Setup present, no build/test/core
processes. SignPath secrets/variables absent; configured unsigned Windows path.

Stable gates: version-only product diff, exact-SHA build/full tests/visual tests,
PR/tag CI, 18 assets and drivers, strict prepublication checks, publish Latest,
APT/Homebrew/Android/Windows consumers and stable post-ship cycles. Pending.

## Outcome

In progress. No stable tag or publication yet.
