---
name: cut-stable
description: Promote a fully verified rolling -rN candidate to stable vX.Y.Z. Uses branch/PR/CI, exact 16-asset validation, fixed-WINBRAT update and connection gates, and never touches the development workstation's VPN installation.
whenToUse: The latest -rN passed all hard gates and the user explicitly said cut, ok, promote, or otherwise authorized the stable release. Stable cut is never autonomous.
---

# Cut a stable release

Promote `vX.Y.Z-rN` to a new immutable `vX.Y.Z` tag. Do not merely change
the prerelease flag and never force-update a published stable tag.

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
4. The published candidate has exactly 16 canonical files: 4 Windows,
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

## Stable commit

1. Create or continue a `dsh/` task branch from current `origin/main`.
2. Change `VPNRouter.Core/AppVersion.cs` from `X.Y.Z-rN` to `X.Y.Z`.
3. Update current version examples in `README.md`, `README.ru.md` and
   `CURRENT_STATE.md`.
4. Run build, full tests, verifier contracts, visual tests,
   `tools/check-open-p0.ps1`, PowerShell 5.1 parse checks and
   `git diff --check`.
5. Commit without bypassing hooks, immediately push the task branch, open a PR,
   and wait for all required PR checks.
6. Merge only after green CI. Build the stable tag from the exact merged
   `origin/main` commit with a clean checkout.

## Create the stable draft and stage assets

Use the accepted stable commit as `$sha`, `$version = 'X.Y.Z'` and
`$tag = "v$version"`. Require clean `HEAD == accepted main`, with the exact
no-suffix AppVersion. With explicit owner authorization, create and push the
new immutable stable tag, verify its remote peeled SHA, then create the draft:

```powershell
git tag $tag $sha
git push origin "refs/tags/$tag"
$remoteSha = gh api "repos/PavelLizunov/VPNRouter/commits/$tag" --jq '.sha'
if ($LASTEXITCODE -ne 0 -or $remoteSha -ne $sha) { throw 'Remote tag SHA mismatch.' }
gh release create $tag --verify-tag --draft --latest=false --title $tag --notes-file <notes-file>
```

Check every native command's exit code and stop on failure. Inspect existing
tags/drafts rather than recreating them. Never force-update a tag or broadly
fetch all tags when auxiliary refs are unhealthy.

Follow `ship-rolling-candidate`'s exact-tag staging and recovery procedure with
the stable version: inspect SignPath settings first. Any configured setting,
including partial enrollment, requires `sign-windows.yml --ref $tag
-f version=$version`; signing failure has no unsigned fallback. Only when all
settings are absent, build the custom sing-box-lx and stage unsigned ZIPs:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-singbox-lx.ps1
powershell -ExecutionPolicy Bypass -File build.ps1 -Version $version `
  -SingBoxPath "publish/sing-box-lx.exe" -Upload
```

`-Upload` only stages to an existing correct-channel draft at the exact accepted
main/tag SHA; it neither creates nor publishes a release and never clobbers.
Wait for tag-triggered macOS/Linux/Android builds or explicitly dispatch each
with `--ref $tag -f version=$version`. Missing draft fails staging safely;
inspect existing assets before retrying missing files only. Partial or
inconsistent assets mean STOP, not an overwrite. `build-android.yml` builds
and production-signs the ARM64 APK; legacy `sign-android.yml` is disabled.

Mirror the immutable stable tag only when explicitly requested, with the
configured mirror and GitHub-equivalent SHA verified. Do not repair remotes
or launch a local VPN to reach them.

## Prepublication gate

Require successful canonical macOS/Linux/Android staging, Windows signing when
configured, `test.yml` and Windows update runs at this stable tag and SHA, then:

- exactly 16 canonical assets and every SHA sidecar matching;
- full Windows ZIP True Split bundle under `app/driver/`;
- update Windows ZIP True Split bundle under `_bootstrap/driver/`;
- Android asset `VPNRouter-vX.Y.Z-android-arm64.apk`.

Explicitly verify the completed draft and wait for success:

```powershell
gh workflow run verify-release-integrity.yml --ref $tag -f tag=$tag -f auto_draft_on_failure=false
```

Run the explicit prepublication strict command in `ship-rolling-candidate`
with this stable `$sha` and `$tag`: it includes `-ReleaseTag`, three platform
build jobs, tests/update/integrity and excludes APT. Do not use bare strict
defaults here because they include APT. Require canonical tag-bound runs, not
arbitrary checks sharing the SHA; inspect Sign Windows separately when used.
APT and full post-ship checks belong after publication, not in this gate.

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
gh workflow run publish-apt.yml --ref $tag -f tag=$tag
```

Homebrew notification in `build-mac.yml` is suppressed during draft staging.
It is not automatically retriggered by publication. After verifying the public
stable and DMG hash, send the workflow's tap event using an authorized token
with contents-write access to `PavelLizunov/homebrew-vpnrouter`:

```powershell
gh api --method POST repos/PavelLizunov/homebrew-vpnrouter/dispatches `
  -f event_type=vpnrouter-release -f "client_payload[version]=$version" -f "client_payload[tag]=$tag"
```

Keep credentials outside commands/logs. Inspect the tap's `update-cask.yml` run
and resulting cask version/hash; missing access is an explicit blocker, not an
automatic-success claim. Do not rerun the macOS uploader against a published
release just to notify Homebrew. Published artifact corrections need a new
version, not replacement assets or a moved tag.

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
3. Remove superseded rolling release pages according to retention policy; do
   not delete the stable tag.
4. Update `CURRENT_STATE.md` to stable `vX.Y.Z` with no in-flight candidate.
5. Record the exact test, CI, asset, WINBRAT and cleanup evidence in the handoff.

The user report must list PASS/FAIL per platform, exact asset count, live-update
result, connection cycles, log scan, True Split bundle result and any external
mirror limitation.
