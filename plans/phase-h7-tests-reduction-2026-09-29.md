# H-7: remove low-value tests (classifier list)

## Why

The owner ordered aggressive test reduction on 2026-09-29. A cheaper external
agent classified 2,555 test methods (KEEP, MERGE, DELETE with a reason and
confidence); the list is kept outside the repository. I verified all 269
high-confidence DELETE entries exist in current main.

## What

Delete 222 test methods in 84 files; 17 test classes became empty and were
deleted (one file kept because it holds a shared type). Reasons in the list:
assertion-free calls, source or documentation read as text, tests of a test's
own mock or helper, Night* tests pinning internals through reflection, trivial
property checks.

## Excluded on purpose

47 DELETE entries in release, workflow, packaging, tooling, verifier,
agent-context and screenshot or visual-diff tests (post-ship verification
uses the screenshot tests). MERGE entries (257) and KEEP-with-medium
confidence (360) are untouched pending a decision.

## Verification

Structure check of edited files (brace balance, attribute placement);
documented filters updated for deleted classes (`P07CliStopSourceGuardTests`,
`HelperCmdParserGuardTests`); exact-head CI. Historical ledger entries that
mention `P07CliStopSourceGuardTests` stay as dated evidence.

## Outcome

Merged as #343 on 2026-09-29; exact-head CI green.
