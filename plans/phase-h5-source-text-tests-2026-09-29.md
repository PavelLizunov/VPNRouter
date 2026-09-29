# H-5: remove source-text pin tests

## Why

A scripted audit of the suite (2,708 test methods, 69.5k lines) found 153 tests
outside release and tooling contracts that read source files as text and assert
on strings. They execute nothing, verify no behavior, and broke repeatedly when
code moved or comments were removed. The owner ordered aggressive test
reduction on 2026-09-29.

## What

Delete those 153 test methods in 53 files; nine test classes became empty and
were deleted. Kept: tests of release, workflow, packaging, tooling, verifier,
agent-context and update contracts (they guard files that ship or steer agents),
and the screenshot and visual-diff tests wired into post-ship verification.
`CliVersionSourceTests` is removed from the documented CLI filters.

## Not removed (candidates for later)

38 tests with no assertion, 194 tests of five lines or fewer (small real
behavior checks), 41 near-duplicate tests, and `Night*` and characterization
classes; each needs a per-file judgement.

## Verification

Exact-head CI builds the test project and runs the rest; structure check of
edited files (brace balance, attributes followed by methods).

## Outcome

Pending CI.
