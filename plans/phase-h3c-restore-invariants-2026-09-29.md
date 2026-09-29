# H-3c: restore one-line invariant comments

## Why

H-3a removed every code comment. An independent review listed 250 removed blocks
that carried real invariants (ordering, races, atomic writes, leak and secret
protection, driver quirks); about 28 of them have no test mentioning the anchor
symbol. The owner approved restoring the essential ones on 2026-09-29, before
dead-code work starts.

## What

52 one-line comments above their anchor lines in 34 files, stating only the
reason or invariant, without versions, dates, audit tags or history. Selected by
hand from the review registry (kept outside the repository at
`~/VPNRouter-knowledge/removed-comments-registry-2026-09-29.md`).

## Verification

Comment-only verifier: 34 files, 0 code differences. Exact-head PR CI green.

## Outcome

Merged as #334 on 2026-09-29; exact-head CI green. Follow-up: cover the untested invariants with tests during the
code stage and drop comments that a test then enforces.
