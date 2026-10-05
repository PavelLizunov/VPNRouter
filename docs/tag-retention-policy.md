# Tag and release retention

This document owns tag naming and retention. The [agent contract](agent-contract.md)
owns safety and authority; the [release strategy](release-strategy.md)
and release skills own publication and verification. Adopting this policy does not
create, delete or move tags, delete releases, or configure GitHub enforcement.

## Retention defaults

A Git tag identifies source history. A GitHub Release holds notes and downloadable
assets. Keeping a tag does not preserve deleted release binaries or their URLs.

| Item | Default |
| --- | --- |
| Stable app tag `vX.Y.Z` | Keep permanently; never move, reuse or rename a published tag. |
| Published candidate tag `vX.Y.Z-rN` | Keep permanently, even after its Release page is retired. |
| Stable Release and assets | Keep permanently. No routine age/count cleanup. |
| Candidate Release and assets | Keep the latest verified candidate and its previous verified candidate, plus the protections below. |
| Versioned tooling tags/releases | Keep immutable for reproducible builds; exclude from app-candidate cleanup. |
| `free-pool-latest` | Keep the existing service endpoint; its designated pool assets remain mutable. |
| PR/debug/test builds | Use commit SHA and CI run ID, not a new persistent tag or Release. Use the workflow's declared artifact retention (30 days by default, explicit shorter windows remain valid). |
| Historical tags without a Release | Keep until individually classified; a missing Release is not deletion evidence. |

## New app tags

- Keep `vX.Y.Z` for stable and `vX.Y.Z-rN` for rolling candidates. The updater and
  repair/build tooling recognize `-rN`; adopting `-rc.1` needs a separate migration.
- Tag only an explicitly authorized release at its accepted, verified commit,
  not each fix, PR or build. Publish one exact tag ref, never a blanket `--tags` push.
- Create annotated tags (`git tag -a`); use signed tags (`git tag -s`) when an
  approved signing setup is available. Never disable required signing or provision
  keys as a side effect. Release skills contain the executable commands.
- Verify the tag's peeled commit against the accepted SHA; an annotated tag object's
  SHA is not the commit SHA. Existing lightweight tags remain valid historical refs:
  do not rewrite them merely to add annotations/signatures or normalize old names.
- A failed release does not free its version number for reuse. Keep its evidence,
  fix the cause and use a new candidate/version rather than moving a public tag.

## Candidate Release retirement

Retention is a minimum, not a quota. Several candidate pages may coexist.

1. Keep the latest verified candidate and the preceding verified candidate across
   release cycles. Also keep all in-flight, incident-investigation, supported
   rollback and pinned-verification releases, regardless of age.
2. An older candidate page is eligible for review only 30 days after a replacement
   passed the full post-ship gate. If that date/evidence is missing, keep the page.
   A stable cut alone does not waive the window or the two-candidate safety net.
3. Check updater/installer links, package manifests, support commitments and known
   consumers of the exact release/download URLs. Keep the page if use is unresolved.
4. Archive the notes, release metadata, asset names, binaries and checksums with
   their tag and commit identities; verify the archive and its recovery location.
   A Git bundle alone does not contain Release assets.
5. Obtain separate owner approval for the exact pages and asset loss, then recheck
   identities and replacement health immediately before deletion. Delete only
   approved Release pages/assets, never their tags; do not use `--cleanup-tag`.
6. Record the decision, archive and outcome under `plans/` or on the owning PR.
   No wildcard, newest-N script, scheduled deletion or implicit ship-time cleanup.

Stable or tooling takedowns require a separate exceptional owner decision; the
candidate procedure does not authorize them. Accidental/abandoned tags likewise
need an individual provenance/consumer audit, verified ref/object backup and exact
owner approval. Age, tag count or absence of a Release alone is insufficient.

## Service exceptions

- `free-pool-latest` is not an app version. The existing
  [pool workflow](../.github/workflows/build-free-pool.yml) replaces `pool.json`
  and `pool.json.gz` via `--clobber`. Do not delete or rename the endpoint, move its
  tag, or apply app-binary immutability to these explicitly mutable assets.
- `tooling-libbox-singbox-1.13.10` is pinned with an asset SHA256 by the Android workflows of
  tags built before 2.50.1. Keep the release and `libbox.aar`. From 2.50.1 the Android
  workflows take `libbox-legacy.aar` from the `sing-box-vpnctl` release with a pinned SHA256 and
  an attestation check (see [SECRETS.md](../.github/SECRETS.md)); rotate through a new version
  and verified consumer pin.
- New service exceptions require an explicit owner decision, not a convenient
  `latest`/`nightly` moving tag. Existing exceptions do not weaken app release gates.

## Enforcement boundary

This is a procedural contract, not a cleanup job or proof of server enforcement.
Adding tag rulesets or GitHub immutable releases is a separate reviewed change:
protect app version refs without breaking service-asset updates, draft-first
staging or failure containment. Never infer those settings from this document.
