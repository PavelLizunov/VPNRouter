# H-3a: remove history and noise from VPNRouter.Core comments

## Why

VPNRouter.Core carries about 18.7k comment lines; at least 1,564 are change
history (versions, dates, audit tags, plan pointers) rather than explanations.
They mislead agents and point at removed plans. The owner approved a hard
comment cleanup on 2026-09-28, project by project, starting with Core.

## What

Comment-only edits in `VPNRouter.Core/**/*.cs`: delete incident narratives,
version/date/audit/plan tags, changelog stories, commented-out code and comments
that restate the code; compress what remains to the reason, invariant, external
constraint or failure semantics the code needs.

## Invariants

- Code tokens and string literals unchanged: a script strips comments from base
  and result and requires identical code for every changed file.
- Keep license headers, compiler/analyzer directives, suppression
  justifications and public XML doc summaries (shortened, not removed).
- Source-guard tests that read Core files as text must stay green.
- No file added, renamed or deleted.

## Verification

Comment-only verifier, exact-head PR CI (`test` compiles Core and runs the
suite, including source-guard tests).

## Rollback

Revert the PR.

## Outcome

Pending.
