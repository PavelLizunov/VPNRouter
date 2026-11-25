# VPNRouter release strategy

## Rolling candidates and stable cuts

Work on `X.Y.Z` ships as successive `vX.Y.Z-r1`, `vX.Y.Z-r2`, etc.
This policy replaced repeated patch releases during one testing cycle.
[Tag and release retention](../docs/tag-retention-policy.md) owns permanent tag
history, the verified-candidate safety net, retirement windows and service
exceptions. Publication does not authorize deleting older releases or tags.

The experimental updater channel discovers prereleases; the stable channel
ignores them. `UpdateChecker` orders `X.Y.Z-r1 < X.Y.Z-r2 < X.Y.Z`, so
released binaries must embed the complete tag version in `AppVersion.Version`, including `-rN`.

- Iterate within the same logical release by incrementing `rN`.
- After a stable release, a new fix starts a new patch cycle at `-r1`.
- A stable cut tags the accepted commit of the verified candidate. GitHub-hosted
  builds compile fresh packages with the no-suffix version from the stable tag.
  The candidate and stable retain distinct tags, binaries and updater channels.
- Every tag, release, merge and stable cut needs explicit owner authority.
  A hotfix does not waive tests, candidate verification or the live-update gate.

`docs/agent-contract.md` is canonical. Executable procedures live in
`.dsh/skills/ship-rolling-candidate/SKILL.md`,
`.dsh/skills/cut-stable/SKILL.md` and
`.dsh/skills/post-ship-mcp-verify/SKILL.md`.

## Prepare a Standard draft

Run the repository command from an exact, clean accepted checkout with Git,
Python 3.11 or newer and an authenticated GitHub CLI. The account needs release
and Actions write permissions and access to inspect repository signing settings.
Follow the authorization and test preconditions in the release skills. The first
release using this procedure is a candidate from a main commit containing the
preparation command and workflows.

```sh
python3 .github/scripts/prepare_release.py vX.Y.Z-rN --notes /path/to/release-notes.md
python3 .github/scripts/prepare_release.py vX.Y.Z --from-candidate vX.Y.Z-rN --notes /path/to/release-notes.md
```

A new candidate uses current GitHub `main`. A stable cut uses the verified,
published candidate's exact commit in accepted main history. The command
creates or validates the annotated tag, then creates or validates the
correct-channel draft. Only after that does it dispatch the macOS, Linux and
Android builds and both test workflows against the exact tag. The build
workflows stamp the full tag version in their temporary checkouts before
compilation. Ordinary development builds read the checked-in version.

Without SignPath configuration, the Windows update integration job builds the
installer and both True Split ZIPs. The command stages the six files from that
job's successful run and attempt after checking their hashes. When signing is
configured, the command dispatches Sign Windows instead. The current SignPath
workflow stages the signed ZIPs and their sidecars. Signed preparation stops
there until an approved installer signing path can prove the installer
provenance. Partial signing enrollment blocks staging.

After the platform and test runs pass and all 18 assets exist, the command
dispatches the read-only integrity check. Its success verifies the checksums,
Windows embedded version and both True Split bundles. Keep the exact
tag/SHA/run evidence and apply the strict checks in the release skills before
publication.

## Publish and verify

1. Use the explicit owner authorization and the applicable release skill.
   Publish the existing draft with explicit channel and Latest flags.

   ```sh
   gh release edit TAG --draft=false --prerelease --latest=false
   gh release edit TAG --draft=false --prerelease=false --latest
   ```

2. Verify the public state and tagged download URLs. Await the postpublication
   integrity and APT runs. For a stable, also await the Homebrew notification
   and confirm the tap updates to the same version and DMG hash. The workflows
   start together on publication. The already verified Windows integration
   run stays bound to the tag and its tested packages. If publication suppresses events, dispatch
   the missing workflow at the published tag.

   ```sh
   gh workflow run verify-release-integrity.yml --ref TAG -f tag=TAG -f auto_draft_on_failure=false
   gh workflow run publish-pages.yml --ref TAG -f tag=TAG
   gh workflow run notify-homebrew.yml --ref TAG  # stable only
   ```

   APT selects the latest published canonical stable before validating its
   assets. Missing assets fail the job. A candidate run checks provenance and
   the stable feed. Homebrew verifies that the tag is the public Latest stable
   before dispatching to the tap.
3. Run the full fixed-WINBRAT gate after every publication. Stable also
   requires the previous-stable to candidate live-update gate before the cut.

## Recovery and integrity

Use one preparation command at a time per tag. To resume after interruption,
rerun it with the same tag, accepted SHA and candidate input. It reuses
successful runs when the expected staged assets are present. Use
`--wait-minutes` to allow more time for queued or approval-bound workflows.
For hosted Windows
it reuses the exact retained artifact, compares existing bytes using GitHub
asset digests and uploads only matching-run files that are missing.

Missing, extra, malformed or partial assets fail closed. Inspect names, hashes
and exact-tag provenance before explicitly staging only missing verified files;
never overwrite existing release assets or silently delete partial uploads.
If consistency cannot be proven, STOP. Published corrections require a new
version/tag; never move a tag, replace public binaries or bypass failed gates.

`verify-release-integrity.yml` checks the exact inventory, validates every
sidecar and recomputes all nine hashes before inspecting archive contents.
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
3. Exactly 18 canonical assets with matching hashes and both True Split bundles.
4. Full fixed-WINBRAT post-ship verification passes, including two connection
   cycles and strict sanitized lifecycle/log checks.
5. Previous-stable -> candidate real live-update gate passes, including helper
   completion, exact installed version, receipt, relaunch and two connection
   cycles. Preserve the procedure in `cut-stable`; no local VPN fallback.
6. `tools/check-open-p0.ps1` exits 0, or an explicit owner waiver is recorded.

Any failure blocks stable. Fix it in a new candidate and repeat the gates;
rebuild and verify the final no-suffix stable tag after the authorized cut.

## Canonical inventory: 18 assets

Each binary has a same-name `.sha256` sidecar, for nine binaries and nine
sidecars. Replace `X.Y.Z` with the full version, including `-rN` when applicable.

| Platform | Binary | Producer |
|---|---|---|
| Windows | `VPNRouter-vX.Y.Z-win.zip` | hosted `test-windows-update.yml` or `sign-windows.yml` |
| Windows | `VPNRouter-update-vX.Y.Z-win.zip` | hosted `test-windows-update.yml` or `sign-windows.yml` |
| Windows | `VPNRouter-Setup-vX.Y.Z.exe` | hosted unsigned Inno Setup package from `test-windows-update.yml` |
| macOS | `VPNRouter-vX.Y.Z-mac.dmg` | `build-mac.yml` |
| macOS | `VPNRouter-vX.Y.Z-mac.zip` | `build-mac.yml` |
| Linux | `VPNRouter-vX.Y.Z-linux-amd64.deb` | `build-linux.yml` |
| Linux | `VPNRouter-vX.Y.Z-linux-x86_64.AppImage` | `build-linux.yml` |
| Linux | `VPNRouter-vX.Y.Z-linux.tar.gz` | `build-linux.yml` |
| Android | `VPNRouter-vX.Y.Z-android-arm64.apk` | `build-android.yml` |

Android production signing uses the long-lived `ANDROID_KEYSTORE_BASE64` and
`ANDROID_KEYSTORE_PASSWORD` secrets. Preserve that keystore: changing it blocks
installed APK upgrades. Enrollment/backup guidance remains in
`git show 6491be4c:plans/vpnrouter-android-platform-parity-roadmap.md` Phase A. Windows enrollment
is documented in `docs/code-signing-signpath-runbook.md`.
