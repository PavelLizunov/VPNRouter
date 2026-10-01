# R-11: ship the rolling candidate v2.50.0-r11

## Why

Main carries about sixty commits after `v2.50.0-r10`: the Android tile, state and look work, the Windows
installer, the `cleanup` command, the icon set, the redaction fix and the long-function splits. The owner
asked on 2026-10-01 for r11 with "all the new work, including design and the installer", and gave a
standing mandate to decide the rest without asking. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r11` (this PR). It follows #424, so the release has 18 assets.
2. After it is merged: tag `v2.50.0-r11` on the accepted commit and create the draft prerelease with notes.
3. Windows assets are unsigned (owner decision, `plans/phase-signing-off-2026-10-01.md`): `build.ps1 -Version
   2.50.0-r11 -Upload` on `windows-worker` builds the zips, the installer and sidecars and uploads six files to the draft
   without clobber.
4. macOS, Linux and Android come from the tag-triggered workflows; the Windows update test likewise.
5. Prepublication gate: 18 assets, sidecars, integrity workflow, strict CI check; open P0/P1 lines (owner-gated)
   need an explicit `check-open-p0.ps1 -Waive` for this cut.
6. Publication as a prerelease (`--latest=false`); then integrity and APT runs.
7. Post-ship WINBRAT gate: NOT run from here (it needs the owner's offline Windows PC); recorded as not run.

## Not covered

Stable cut. A candidate is not verified until the post-ship gate passes. The Android tunnel on a real phone
(arm64 libbox) is tested later on the tester's phone.

## Verification

Exact-head CI on this PR; the release steps above are recorded in the Outcome as they complete.

## Outcome

Published 2026-10-01 as prerelease `v2.50.0-r11` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on
main `fb1194f9`, 18 assets.

- Preconditions: `verify-last-commit-ci.ps1` OK on `fb1194f9`; `check-open-p0.ps1` reported 3 open owner-gated P1
  lines (VPNCTL-04 deferred to API 24, Ox Alpha provider-side credential revocation, Tcpip 4266 measurement-gated)
  and was run with `-Waive` (same lines as r10; the owner mandated r11 and autonomous decisions); no SignPath
  secret or variable exists, so the unsigned path applied.
- Windows (6 files) built on `windows-worker` with `relbuild2.ps1` (`build.ps1 -BundleSplitDriver -Installer`;
  preflight: CPU 15 percent, 13.3 GB free RAM, 17.7 GB free disk, nothing heavy running). Copied back, each file
  matched its sidecar; both zips carry the split-tunnel driver and `2.50.0-r11` in `VPNRouter.Core.dll`;
  uploaded to the draft without clobber (digests equal the local files). macOS, Linux and Android came from the
  tag workflows.
- Tag CI green: macOS, Linux, Android, Windows update test, `dotnet test`. Draft integrity run (dispatched,
  `auto_draft_on_failure=false`) passed with 18 assets; its only warnings are the Android APK assembly store and
  the AppImage (as for r10). Strict `verify-last-commit-ci.ps1` for the exact commit: 8 green, 0 red.
- After publication the `release: published` integrity, APT and Windows update test runs succeeded without a manual dispatch.
- Installer: built unsigned; the integrity workflow checks the `MZ` header and the version text.
- Post-ship gate (`tools/post-ship-verify.ps1`): NOT run. It needs the owner's offline Windows PC (WinRM credential
  file, local .NET SDK). The candidate is published but not verified; run
  `tools/post-ship-verify.ps1 -Version 2.50.0-r11 -Cycles 2` from that PC. The Android tunnel on a real phone
  is also still to be tested on the tester's phone.
