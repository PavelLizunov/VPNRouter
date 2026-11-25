---
name: cut-stable
description: Promote a fully verified rolling -rN candidate to stable vX.Y.Z. Uses exact-tag CI, 18-asset validation, fixed-WINBRAT update and connection gates, and never touches the development workstation's VPN installation.
whenToUse: The latest -rN passed all hard gates and the user explicitly said cut, ok, promote, or otherwise authorized the stable release. Stable cut is never autonomous.
---

# Cut a stable release

Promote `vX.Y.Z-rN` to a new immutable `vX.Y.Z` tag. Do not merely change
the prerelease flag and never force-update a published stable tag.
Read [tag and release retention](../../../docs/tag-retention-policy.md) before
creating tags or proposing cleanup; release approval is not deletion approval.

## Hard preconditions

All items must pass:

Before any build, test, package, or mutable worker action, follow
`docs/test-workers.md`: `harness-test` remains control plane only; use an exact
committed SHA; verify identity, active jobs, CPU, available RAM, free disk, and
required SDKs read-only; queue conflicting scenarios; and STOP rather than
provisioning SDKs, resizing resources, or cleaning shared caches.

1. Candidate AppVersion exactly equals its `-rN` tag.
2. Release build and full regression suite pass with the pinned SDK on an
   authorized preflighted build worker.
3. Exact candidate commit CI is green for tests, Windows update integration,
   macOS, Linux, Android, APT and release integrity.
4. The published candidate has exactly 18 canonical files: 6 Windows,
   4 macOS, 6 Linux and 2 Android ARM64 assets.
5. `tools/post-ship-verify.ps1 -Version X.Y.Z-rN` returns PASS.
6. The mandatory previous-stable to candidate live-update gate below returns
   PASS, including two connection cycles.
7. `tools/check-open-p0.ps1` exits 0. A waiver requires an explicit owner
   decision and a recorded reason.
8. The user explicitly authorizes the stable cut.

## Mandatory WINBRAT live-update gate

All install, launch, update, connect, log and cleanup work runs only on the
fixed WINBRAT VM through the repository tools. Never run VPNRouter locally or
touch the developer machine's `C:\Program Files\VPNRouter`.

1. Resolve the latest non-prerelease release.
2. Download its full Windows ZIP and sidecar to the repository root, recompute
   SHA256, and fail on any mismatch.
3. Verify the immutable target before mutation:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action identity
   ```

   The tool must report `WINBRAT @ 100.115.182.0`.
4. Deploy the previous stable:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 `
     -Action deploy -Version <baseline>
   ```

5. Exercise the real updater to the candidate:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 `
     -Action liveupdate -Version <candidate-rN>
   ```

   PASS requires helper completion, exact installed semantic version,
   successful relaunch and a consumed install receipt.
6. Run two complete post-update connection cycles:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-stability.ps1 `
     -Mode ColdCycles -Version <candidate-rN> -Cycles 2
   ```

7. Always clean WINBRAT, including failure paths:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/brat-stability.ps1 -Mode Cleanup
   ```

8. After recording required evidence, remove the exact baseline ZIP and sidecar
   downloaded in step 2 on both PASS and FAIL paths. Do not use a version glob or
   broad artifact cleanup.

Any failure means: do not cut stable; fix it in `-r(N+1)` and repeat every
hard gate.

## Prepare the stable draft

After the separate owner authorization and hard gates above, use a clean
checkout at the verified published candidate's exact commit in accepted
`main` history. Stable packages compile on GitHub from that commit with the
stable version. Prepare the draft and await the exact-tag builds and integrity
check.

```powershell
python .github/scripts/prepare_release.py vX.Y.Z --from-candidate vX.Y.Z-rN --notes <notes-file>
```

Record the stable tag, SHA and run IDs. Follow
[release strategy](../../../docs/release-strategy.md) for signing and recovery.
Mirror the immutable stable tag only when explicitly requested, with the
configured mirror and GitHub-equivalent SHA verified.

## Prepublication gate

Require the successful preparation command, all 18 canonical assets, checksums
and both Windows True Split bundles. Run the exact-tag prepublication strict
command in [ship-rolling-candidate](../ship-rolling-candidate/SKILL.md) with
this stable SHA and tag. Inspect the exact-tag Sign Windows run when configured.
APT and post-ship checks follow publication.

## Publish and notify consumers

After all draft gates pass and owner publication authority is confirmed:

```powershell
gh release edit $tag --draft=false --prerelease=false --latest
```

Confirm public/non-prerelease/Latest state and all canonical tagged download
URLs. Await postpublication integrity and APT; token-originated publication may
not trigger downstream workflows, so explicitly dispatch missing runs:

```powershell
gh workflow run verify-release-integrity.yml --ref $tag -f tag=$tag -f auto_draft_on_failure=false
gh workflow run publish-pages.yml --ref $tag -f tag=$tag
```

Await `notify-homebrew.yml` and verify the tap's `update-cask.yml` run and
resulting cask version and DMG hash. If the publication event was suppressed,
dispatch the notification at the published stable tag.

```powershell
gh workflow run notify-homebrew.yml --ref $tag
```

Published artifact corrections require a new version and tag.

## Mandatory stable post-ship verification

Run the same fail-closed gate against the final stable:

```powershell
powershell -ExecutionPolicy Bypass -File tools/post-ship-verify.ps1 `
  -Version X.Y.Z -Cycles 2
```

This binds visual tests, exact-SHA CI, exact assets, both Windows ZIP hashes and
driver bundles, fixed-WINBRAT deployment, connection cycles and lifecycle/log
checks to the final stable tag.

Optionally, and whenever update tooling changed, deploy the last `-rN` and run
`brat-verify -Action liveupdate -Version X.Y.Z` to prove the candidate-to-stable
update path too.

## Finalization

Only after final stable verification:

1. Publish complete release notes and verify `vX.Y.Z` remains Latest.
2. Verify Homebrew, APT, Android download page and canonical Windows URLs.
3. Keep releases under the linked retention policy. Any candidate-page retirement
   needs separate exact-target approval; do not delete stable or candidate tags.
4. Update `CURRENT_STATE.md` to stable `vX.Y.Z` with no in-flight candidate, and
   the `build.ps1 -Version` / `build-mac.sh` examples in both READMEs.
5. Record the exact test, CI, asset, WINBRAT and cleanup evidence in the handoff.

The user report must list PASS/FAIL per platform, exact asset count, live-update
result, connection cycles, log scan, True Split bundle result and any external
mirror limitation.
