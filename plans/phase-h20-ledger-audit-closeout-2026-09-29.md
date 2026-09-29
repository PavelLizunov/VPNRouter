# H-20: ledger close-out after the audit fixes

## Why

The 2026-09-29 audit annotated 21 open P0/P1 lines. Ten were verified as fixed in source, premise
gone, or refuted by a passing test, but their checkboxes were untouched, so the stable-cut gate
still counted them.

## What

- Tick ten lines: NIGHT-01, -02, -03, -04, -06, -08 and four release-pipeline P1s. Each carries a
  one-line reason. NIGHT-02 is closed by an existing passing regression test that uses the exact
  scenario.
- Annotate the entries whose fixes are open draft PRs (#350, #352, #353, #354, #355) and the
  credentials entry (owner rotated the old keys; history rewrite awaits an explicit go). These
  stay open until the PRs merge.

## Not covered

NIGHT-05, NIGHT-07 and two release P1s marked "largely/partly fixed" stay open; the WINBRAT
liveupdate entry is still not re-verified.

## Verification

Only `plans/OPEN-DEFECTS.md` and this brief change; the gate script parses the same format (open
P0/P1 lines: 21 before, 11 after). Exact-head CI.

## Outcome

Pending CI.
