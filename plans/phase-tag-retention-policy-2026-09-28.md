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
2. Tests: PENDING documentation checks and exact-head Linux/Windows PR CI.
3. Documentation: PENDING one policy owner and consistent consumer links.
4. Independent review: PENDING explicitly routed single Opus; no self-review PASS.
5. UI/runtime: N/A, no product behavior or release/deployment.
6. Integration: PENDING base/head identity, green main after authorized merge and
   proof that tag refs remain unchanged. No mechanical source split.

## Outcome

Planning checkpoint only. No policy implementation, tag mutation or release
cleanup has occurred. Final acceptance receipts will be recorded on the PR after
merge so verification metadata does not require another product change.
