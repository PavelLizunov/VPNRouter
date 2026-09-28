# H-2a: correct false statements in active instructions and docs

## Why

The H-0 audit of active instructions (snapshot `6491be4c`) found statements that
contradict current source: a release-skill step that always fails, a wrong
in-flight candidate in `CURRENT_STATE.md`, incomplete secrets and legal notices,
stale README claims, and pointers to history removed in H-1. Agents follow these
files, so each false line is a live risk. The owner approved H-2 on 2026-09-28.

## What

- Fix verified false or stale statements in: `ship-rolling-candidate` and
  `cut-stable` skills (unsigned Windows path), `CURRENT_STATE.md`,
  `.github/SECRETS.md`, `NOTICE.md`, `PRIVACY.md`, `README.md`/`README.ru.md`,
  `.github/pull_request_template.md`, `.github/workflows/AGENTS.md`,
  `phase-task-launcher`, `post-ship-mcp-verify`, `diagnose-config`,
  `audit-overflow-fix`, `VPNRouter.Core/AGENTS.md`, `.githooks/AGENTS.md`,
  `CONTRIBUTING.md`, `docs/test-workers.md`, `.gitattributes`,
  `samples/rules/README.md`, `profiles/default-android.json` comment, and the
  `tools/zapret/` wording in the contract and `tools/AGENTS.md`.
- Replace non-code pointers to removed history with `git show 6491be4c:<path>`
  or drop them.
- Ledger: record NEW-1 and the six imported map hypotheses as unverified,
  non-gating entries, plus the instruction findings with their disposition.

## Invariants

- Every edit is checked against source at the branch base; no claim is changed
  from memory. Unverifiable items are left and listed, not guessed.
- No product code or behavior change; `tools/zapret/` files untouched.
- Rules are corrected, not weakened: safety, authority and WINBRAT constraints
  stay intact. Consolidation of duplicated rules is H-2b.
- Test pins in `AgentContextContractTests` and other contract tests stay green.

## Verification

Scoped diff review, links resolve, cut gate set unchanged except the new
non-gating entries, exact-head PR CI green.

## Rollback

Revert the PR.

## Outcome

Pending.
