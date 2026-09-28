# Tag retention policy

## Why

The owner requested a durable, convenient tagging and retention policy after the
repository cleanup. Tag count is not a deletion criterion: the read-only inventory
found 276 tags and 65 GitHub Releases, with 211 tags lacking a Release object.
Those observations do not establish whether a tag is unused or was never published.

## What and invariants

- Add one policy owner under docs/ and link the canonical contract, release
  strategy, both release skills and the English/Russian READMEs to it.
- Keep stable and published candidate tags permanently. Preserve stable release
  pages/assets and the free-pool/tooling consumers.
- Keep current and previous verified candidate release pages; older pages become
  review candidates only after 30 days and a verified replacement. Any actual
  deletion needs a separate exact-target owner approval and recovery evidence.
- Use annotated tags for future app releases; keep current vX.Y.Z-rN naming and
  peeled-commit provenance. Do not rewrite historical lightweight tags.
- No existing tag, Release, binary, GitHub ruleset, updater, workflow or signing
  configuration changes. No VPN/UI execution. Preserve Omarchy and performance work.

## How and authority

Base: b733e3f35f0375b84ef1a878a854b48cfab6adf9.
Branch: dsh/tag-retention-policy-2026-09-28, isolated worktree.

The owner explicitly approved one-Opus workflow review, green CI, merge to main
and removal of this task's temporary branch. That is not release/tag creation or
retention-deletion authority. Commit/push this brief and wait for its PR checks
before implementation. Then validate the policy links, retained safety pins,
skill frontmatter and annotated-tag peeling in a disposable local fixture.

## Risk, rollback and unknowns

Risk is contradictory release instructions or accidental cleanup authority.
The existing single-candidate-page wording must be reconciled with the new safe
retention window. free-pool-latest intentionally replaces assets and must not be
made immutable as a side effect. GitHub enforcement remains unchanged, not claimed
active. Roll back documentation through a normal reviewed commit, never by moving
published tags. Historical deletion candidates are outside this task.

## Six gates

1. Build: N/A locally, Markdown-only; no control-plane SDK/build provisioning.
2. Tests: PASS scoped checks; final exact-head Linux/Windows PR CI is pending.
3. Documentation: PASS one policy owner, 38 local links across nine Markdown
   files, two unchanged three-key skill frontmatters and release-command pins.
4. Independent review: PENDING explicitly routed single Opus; no self-review PASS.
5. UI/runtime: N/A, no product behavior or release/deployment.
6. Integration: PENDING green main after authorized merge and unchanged tag refs.
   No mechanical source split.

## Outcome checkpoint

- Added docs/tag-retention-policy.md; linked the contract, documentation index,
  release strategy, both release skills and English/Russian READMEs.
- The 30-day candidate-page grace period starts at verified supersession, not
  publication alone. The two verified candidates and in-flight/incident/rollback
  exclusions are a minimum, not a cap or automatic deletion rule.
- Both release skills now show annotated tag creation; remote provenance still
  resolves the peeled commit. The sole-candidate-page wording was removed.
- Brief head a5e54fbcb331cd1eaf82c0b449aed0ceaa82e659 passed all four checks in
  run 36427224870 plus grep 36427224885 before implementation.
- Whitespace, links, ASCII policy text and skill checks passed. A disposable Git
  fixture confirmed annotated tag type, peeled SHA and rejection of duplicate
  names. It created no tag in VPNRouter and was removed automatically.
- The first new-file whitespace command returned git diff --no-index status 1
  for an added file, with no diagnostics; the corrected check distinguished that
  expected status from whitespace errors. No failure was ignored.
- Baseline remote refs: 276 tags. SHA256 of sorted (ref, object-SHA) pairs encoded
  as compact JSON: e194b7a8e622211247b1978a58725f11ee0b4234c5625bd1027b5e68b51f3b3f.
- No existing tags/releases, GitHub settings or executable files changed. Final
  review, exact-head CI and post-merge receipts belong on PR #325, so acceptance
  metadata does not itself require another product change.
