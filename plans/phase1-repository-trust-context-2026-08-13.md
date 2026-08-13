# Phase 1 - Repository trust and agent context

**Owner**: Codex session 2026-08-13
**Branch**: `codex/quality-trust-hardening-249`
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` Phase 1 and documentation follow-ups
**Effort**: 3-5 hours
**Risk**: LOW
**Blast radius**: documentation, repository metadata guidance, agent bootstrap files and contract tests; no VPN runtime behavior
**Rollback**: revert the implementation commits or delete the branch

## Why

The public repository and the two root agent contexts contain stale facts and
duplicated policy. README still advertises 765 tests while the v2.49.0 gate ran
2762, GitHub describes a Windows-only application, and the 20 KB AGENTS/CLAUDE
mirrors already disagree. This creates avoidable user-trust friction and makes
each agent spend context on old release incidents before reaching the task.

## What

- Refresh README EN/RU test/file/LOC facts and add representative product UI
  screenshots from existing verified repository evidence.
- Refresh active roadmap/test documentation where it presents old baseline
  facts as current; keep historical plans explicitly historical.
- Add the exact GitHub description/topics update to the repository workflow and
  apply it through `gh` after the branch is pushed.
- Preserve the prepared Windows SignPath workflow and make the remaining
  owner-enrollment gate explicit and checkable; do not invent a second signer.
- Move shared operational rules to one canonical agent contract. Keep
  `AGENTS.md` and `CLAUDE.md` as short tool-specific bootstraps that both require
  the same contract.
- Put short, current focused test commands in the relevant zone documents.

```diff
- two approximately 300-line root policy mirrors with divergent facts
+ one canonical contract plus two short tool-specific bootstraps
```

## How

1. Inventory every rule that differs between AGENTS and CLAUDE and classify it
   as shared or tool-specific.
2. Move shared rules without weakening release, branch, WINBRAT or safety gates.
3. Add source-contract tests that fail if either bootstrap stops loading the
   common contract or starts duplicating the full golden-rule block.
4. Update current public facts and use existing verified screenshots rather
   than creating a new design artifact.
5. Verify the SignPath workflow remains fail-closed and document the exact
   external owner action still required.

### Tests written

- `AgentContextContractTests` - both root bootstraps load the one canonical
  contract and retain their tool-specific paths.
- Existing release tooling contract tests - SignPath and documentation assets
  remain discoverable.

### Verification approach

Documentation/source contract tests, full build/test suite, diff review, and
GitHub metadata read-back after the PR branch is pushed. No app launch is
required because there is no UI behavior change.

## Verification gate

- [ ] **Gate 1 - Build clean**: Release solution build, 0 errors.
- [ ] **Gate 2 - Tests green**: focused context tests and full suite.
- [ ] **Gate 3 - Docs**: README EN/RU, current-state/roadmap and this Outcome are current.
- [ ] **Gate 4 - Self-review**: ponytail/simplify review confirms no new policy layer or duplicated rule block.
- [ ] **Gate 5 - Remote brat UI verify**: N/A - no UI behavior change.
- [ ] **Gate 6 - Characterization diff**: N/A - not a product split.

## Outcome

Pending implementation and verification.

