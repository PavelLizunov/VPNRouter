# Faster Standard release preparation

## Why

The platform builds can finish in parallel, but Standard release preparation waits
on a version-only merge, a maintainer-side Windows build and manual recovery when
builders start before the draft exists. The 2.51.1-r1 release exposed this sequence
on the existing four-platform path.

## What

Provide one maintainer command to prepare a Standard release draft. Bind the draft,
platform packages, and verification runs to the same immutable tag and accepted
source commit. Prepare all platforms in parallel after the draft exists. Build the
unsigned Windows packages on a GitHub-hosted runner so WINBRAT is available for
live acceptance. Preserve the signed path and explicit publication authority.
Permit a stable package build from the verified candidate's accepted commit,
with its own stable version and the existing authorization and readiness gates.

## How

- Reuse the canonical platform, Windows update, regression and integrity workflows.
- Resolve the build version from the exact release tag for the new path, with the
  checked-in version remaining the default for ordinary development builds.
- Reuse the Windows installer and split-driver bundling path. Prove successful
  exact-run provenance before using CI output to complete a draft.
- Require an existing clean accepted source, inspect signing configuration and
  verify the draft channel before staging. Fail closed on conflicts and keep
  recovery tied to the same tag, commit, and canonical asset set.
- Keep explicit publish, stable readiness, immutable tags, 18 assets, checksum
  verification, public-channel checks, and WINBRAT/live-update acceptance.

## Risk and rollback

Release provenance, version ordering, signing policy and concurrent staging are
sensitive. Verify tag binding and exact checks before accepting a draft. Before a
release is authorized, rollback is a normal PR revert. Once a tag exists, preserve
it and use the documented append-only release recovery procedure.

## Verification gates

1. Release solution build on GitHub-hosted CI at the PR head.
2. Regression tests plus focused preparation, version and staging tests. Add a
   hosted Windows package smoke path with no release writes.
3. Canonical release strategy and ship/cut procedures align with the implementation.
4. Independent correctness, concurrency, credentials, and coverage review.
5. Live WINBRAT and public artifact acceptance require an authorized release.
6. Exercise failure boundaries without creating a production tag, release or deploy.

## Outcome

Planning baseline is `7c6e13fd`. The GitHub Actions runs for the accepted main
baseline completed successfully. Production release actions are outside this PR.

The first implementation adds the Standard draft command and tag version
stamping, reuses the hosted Windows integration packages, checks True Split
during integrity verification, and starts Homebrew notification on stable
publication. The existing SignPath workflow provides signed ZIPs and sidecars.
A signed installer still requires an approved SignPath enrollment and procedure.

All seven checks on the planning commit `93bba9fe` passed. Eighteen Python
tests, actionlint and Bash syntax checks on eight workflow files, the extracted
integrity fixture for both Windows ZIP layouts, and version stamping in a
disposable Git checkout passed locally. Implementation CI runs on PR #740.

The independent DeepSeek review of tree `03c9635` found a stale C# assertion,
a mutable inventory comparison, reuse of updater-only runs, a missing legacy
candidate preflight, unsuitable timeouts and stale release guidance.
The implementation addresses those findings. Signed ZIP staging remains
available, and the command stops before declaring completion until installer
signing provenance can be established. The release defect ledger records
the outstanding installer dependency.

The Linux test run on `e355106b` failed one release-skill text assertion.
The rolling-candidate instructions again explicitly require exactly 18
canonical assets. All 17 text assertions from that test were rechecked
against both skills before the follow-up commit.

Production timing and live WINBRAT evidence require an authorized release.
