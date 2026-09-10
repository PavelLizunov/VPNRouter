# VPNRouter release strategy

## Rolling candidates and stable cuts

Work on `X.Y.Z` ships as successive `vX.Y.Z-r1`, `vX.Y.Z-r2`, etc.
This policy replaced repeated patch releases during one testing cycle. Keep
one active candidate release page alongside the current stable Latest. Remove
a superseded candidate page only after its replacement passes full post-ship
verification and retention is authorized; preserve immutable tags as history.

The experimental updater channel discovers prereleases; the stable channel
ignores them. `UpdateChecker` orders `X.Y.Z-r1 < X.Y.Z-r2 < X.Y.Z`, so
AppVersion must match the complete tag version, including `-rN`.

- Iterate within the same logical release by incrementing `rN`.
- After a stable release, a new fix starts a new patch cycle at `-r1`.
- A stable cut creates a new no-suffix AppVersion commit through branch/PR/CI,
  then rebuilds all platforms from its new immutable stable tag. Merely changing
  the candidate's prerelease flag does not produce a stable binary.
- Every tag, release, merge and stable cut needs explicit owner authority.
  A hotfix does not waive tests, candidate verification or the live-update gate.

`docs/agent-contract.md` is canonical. Executable procedures live in
`.dsh/skills/ship-rolling-candidate/SKILL.md`,
`.dsh/skills/cut-stable/SKILL.md` and
`.dsh/skills/post-ship-mcp-verify/SKILL.md`.

## Draft-first publication order

1. Obtain release authorization. Record the accepted main commit SHA, require a
   clean checkout at that SHA and exact full AppVersion, then create/push only
   the new immutable version tag. Resolve the remote tag to its peeled commit
   and compare it with the accepted SHA before creating a release.
2. Create a draft using `gh release create TAG --verify-tag --draft
   --latest=false` with release notes. Add `--prerelease` for a candidate.
   Inspect an existing tag/draft rather than replacing it. No builder creates
   releases or implicitly publishes assets.
3. Inspect SignPath configuration without revealing values. Any SignPath secret
   or `SIGNPATH_EXPECTED_SUBJECT` variable, even partial configuration, requires
   the signed path: `gh workflow run sign-windows.yml --ref TAG -f version=VERSION`.
   It builds exact-tag Windows sources and verifies signatures before staging.
   Missing enrollment or failed signing means STOP, never unsigned fallback.
4. Only with all SignPath settings absent, build the custom sing-box-lx on the
   authorized exact-SHA worker, then run `build.ps1 -Version VERSION
   -SingBoxPath publish/sing-box-lx.exe -Upload`. Upload only stages unsigned
   Windows ZIPs/sidecars to the existing correct-channel draft; it requires
   `HEAD == accepted main == tag SHA`, never creates/publishes, and never clobbers.
5. Wait for tag-triggered `build-mac.yml`, `build-linux.yml`, `build-android.yml`,
   `test.yml` and `test-windows-update.yml`. If the tag push preceded draft
   creation, staging can fail safely because the draft was absent. Inspect runs
   and existing assets first, then explicitly dispatch missing platform work
   with `--ref TAG -f version=VERSION` and `-f upload_to_release=true` where
   offered. Test workflows take `--ref TAG` without a version input.
   Legacy `sign-android.yml` is disabled; use `build-android.yml`.
6. Before publication, require all platform staging and tag-bound test/update
   jobs green, exactly the 16 canonical assets below, all hashes matching and
   both Windows True Split bundles. Explicitly dispatch and await:

   ```sh
   gh workflow run verify-release-integrity.yml --ref TAG -f tag=TAG -f auto_draft_on_failure=false
   ```

   Strict CI requires `-Commit SHA -ReleaseTag TAG -Strict`: canonical workflow
   paths, tag/SHA, allowed events, latest run attempts and their jobs. Unrelated
   same-SHA checks cannot satisfy the gate. Use the explicit prepublication
   requirements in `ship-rolling-candidate`, not bare strict defaults (which
   include APT). APT and full post-ship verification are not prepublication
   prerequisites.
7. Reconfirm owner authority, then publish with explicit channel/Latest flags:

   ```sh
   # Candidate: keep the previous stable as Latest.
   gh release edit TAG --draft=false --prerelease --latest=false
   # Stable: only after the separately authorized stable-cut gates.
   gh release edit TAG --draft=false --prerelease=false --latest
   ```

8. Verify public state and tagged download URLs. Await postpublication integrity
   and APT. Publication by workflow token may suppress downstream events; if
   absent, explicitly dispatch both at the published tag:

   ```sh
   gh workflow run verify-release-integrity.yml --ref TAG -f tag=TAG -f auto_draft_on_failure=false
   gh workflow run publish-apt.yml --ref TAG -f tag=TAG
   ```

   APT retains/reindexes the latest published canonical stable only, selected
   before asset validation. A missing DEB or sidecar fails closed; it never
   falls back to an older eligible stable. Candidate-tag runs prove provenance
   and stable-feed health, not candidate DEB inclusion.
9. Run the full fixed-WINBRAT post-ship gate after every candidate and stable
   publication. No Core-only or tiny-change exemption applies. Stable Homebrew
   notification is explicit after publication: draft staging in `build-mac.yml`
   suppresses the tap event. Follow `cut-stable` for the `vpnrouter-release`
   repository dispatch, then verify the tap run and cask version/hash. Never
   rerun a public-release uploader just to notify the tap.

## Recovery and integrity

Missing, extra, malformed or partial assets fail closed. Inspect names, hashes
and exact-tag provenance before explicitly staging only missing verified files;
never overwrite existing release assets or silently delete partial uploads.
If consistency cannot be proven, STOP. Published corrections require a new
version/tag; never move a tag, replace public binaries or bypass failed gates.

`verify-release-integrity.yml` checks the exact inventory, validates every
sidecar and recomputes all eight hashes before inspecting archive contents.
Windows embedded AppVersion is a hard gate. Other platforms retain limited
embedded-version inspection because trimming/compression can remove literals;
AppImage payloads are never executed to inspect their version. SHA sidecars
prove consistency, not publisher authenticity or reproducibility.

The integrity workflow runs on publication and explicit dispatch, not asset
upload or `release: edited`. Do not assume an upload causes a final recheck.
`auto_draft_on_failure=false` disables all release mutations for the explicit
prepublication check. Automatic failure handling can flag/draft a corrupt
release; this is containment, not authority to remove a failure banner and
republish without a verified correction.

## Stable readiness gates

A published candidate is ready for a separately authorized cut only after:

1. Clean Release solution build and full regression/headless suite on the
   authorized preflighted exact-SHA worker.
2. Canonical tag-bound macOS/Linux/Android, tests, Windows update, integrity and
   postpublication APT runs succeed.
3. Exactly 16 canonical assets with matching hashes and both True Split bundles.
4. Full fixed-WINBRAT post-ship verification passes, including two connection
   cycles and strict sanitized lifecycle/log checks.
5. Previous-stable -> candidate real live-update gate passes, including helper
   completion, exact installed version, receipt, relaunch and two connection
   cycles. Preserve the procedure in `cut-stable`; no local VPN fallback.
6. `tools/check-open-p0.ps1` exits 0, or an explicit owner waiver is recorded.

Any failure blocks stable. Fix it in a new candidate and repeat the gates;
rebuild and verify the final no-suffix stable tag after the authorized cut.

## Canonical inventory: 16 assets

Each binary has a same-name `.sha256` sidecar, for eight binaries and eight
sidecars. Replace `X.Y.Z` with the full version, including `-rN` when applicable.

| Platform | Binary | Producer |
|---|---|---|
| Windows | `VPNRouter-vX.Y.Z-win.zip` | unsigned `build.ps1` or exact-tag `sign-windows.yml` |
| Windows | `VPNRouter-update-vX.Y.Z-win.zip` | unsigned `build.ps1` or exact-tag `sign-windows.yml` |
| macOS | `VPNRouter-vX.Y.Z-mac.dmg` | `build-mac.yml` |
| macOS | `VPNRouter-vX.Y.Z-mac.zip` | `build-mac.yml` |
| Linux | `VPNRouter-vX.Y.Z-linux-amd64.deb` | `build-linux.yml` |
| Linux | `VPNRouter-vX.Y.Z-linux-x86_64.AppImage` | `build-linux.yml` |
| Linux | `VPNRouter-vX.Y.Z-linux.tar.gz` | `build-linux.yml` |
| Android | `VPNRouter-vX.Y.Z-android-arm64.apk` | `build-android.yml` |

Android production signing uses the long-lived `ANDROID_KEYSTORE_BASE64` and
`ANDROID_KEYSTORE_PASSWORD` secrets. Preserve that keystore: changing it blocks
installed APK upgrades. Enrollment/backup guidance remains in
`plans/vpnrouter-android-platform-parity-roadmap.md` Phase A. Windows enrollment
is documented in `plans/code-signing-signpath-runbook-2026-07-10.md`.
