---
name: bug-hunt
description: Adversarial multi-agent review of a diff or subsystem with verified findings recorded in plans/OPEN-DEFECTS.md.
when: Before stable, after a non-trivial feature/refactor, or when the user asks for a bug-hunt or independent review.
---

# Adversarial bug-hunt

Read `docs/REVIEW_AGENT_PROMPT.md` and the relevant zone invariants. Give the
full diff/subsystem and invariant brief independently to reviewers with distinct
correctness, concurrency, security/fail-closed and test/release lenses.

Re-open every claimed `file:line` and drop unverified claims. Triage survivors
as P0/P1/P2 with problem, evidence, smallest fix, cost and blast radius. Fix
in-scope P0/P1 before proceeding. Record every real deferred survivor in
`plans/OPEN-DEFECTS.md` with status and eventual implementation/PR reference;
never leave a finding only in chat. Re-run the affected lenses after fixes.

The stable gate reads this ledger through `tools/check-open-p0.ps1`. A waiver
requires an explicit owner decision and recorded reason.
