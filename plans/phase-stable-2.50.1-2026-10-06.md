# Stable cut: v2.50.1 from the candidate v2.50.1-r1

## Why

The owner authorized the stable release after the candidate v2.50.1-r1 was verified (in Russian, "release it"). v2.50.0 is the current stable; v2.50.1-r1 moves desktop and Android to the core `sing-box-vpnctl` v1.14.2-vpnctl.2 (`plans/phase-core-1.14.2-vpnctl2-2026-10-05.md`).

## What

1. Stable commit (this PR): `AppVersion.Version` becomes `2.50.1`, `CURRENT_STATE.md` points at the stable, the ledger line VPNCTL-04 is closed (Android moved to the new core, verified on an emulator only), and the core brief gets its Outcome. The tree is otherwise the tested candidate `2dff3998`.
2. After merge: annotated tag `v2.50.1` on the merged main commit, draft release (non-prerelease, `--latest=false`), six Windows files (unsigned, SignPath is not configured) built on `windows-worker`, macOS, Linux and Android from the tag workflows, prepublication gate, publish with `--latest`, integrity and APT runs, the Homebrew tap event, post-ship on the final stable.
3. Release notes (Russian, like the previous stables) cover everything since v2.50.0.

## Not covered

An Android device, real macOS and Linux machines, signed Windows files.

## Verification

The mandatory previous-stable to candidate live-update gate (`v2.50.0` to `v2.50.1-r1`, then two cold cycles) on WINBRAT before this PR is merged; the live scenarios that were not run on the candidate (mode switches, Other versions, auto-select, fast clicks) on the installed candidate; exact-head CI; the gates of `ship-rolling-candidate` with the stable tag. `check-open-p0.ps1` passes without a waiver because the ledger has no open P0 or P1 line.

## Rollback

A published stable tag is immutable and its assets are never replaced; a correction is a new version. Emergency measure with the owner's approval: mark `v2.50.1` as a prerelease and make `v2.50.0` Latest again; users on `v2.50.1` can go back through Settings > Updates > Other versions (desktop). Android has no downgrade path.

## Outcome

Published 2026-10-06 as the stable `v2.50.1` (Latest; the previous stable was `v2.50.0`), annotated tag on main `d7c39c53` (PR #550), 18 assets (six Windows files built on `windows-worker`, hashed there and compared by size, SHA-256 and sidecar before upload; macOS, Linux and Android from the tag workflows; the CI arm64 APK is 174.9 MB). The tree equals the candidate `v2.50.1-r1` (`2dff3998`) except `AppVersion`, `CURRENT_STATE.md`, the ledger line VPNCTL-04 and the briefs.

Gates, in order:

| gate | result |
|---|---|
| live update gate on WINBRAT, `v2.50.0` deployed from its ZIP, then the real updater to `v2.50.1-r1` | PASS: helper done, copy exit code zero, installed version matches, app relaunched, receipt consumed; then 2 cold cycles PASS and cleanup PASS (a local run of `tools/brat-verify.ps1 -Action liveupdate` through `tools/brat-local-shim.ps1`, like `post-ship-local.ps1`) |
| live scenarios not run on the candidate, on the installed candidate | mode switches 6 of 6 never showed "Connect"; Other versions list shows the installed candidate, v2.50.0 and older; auto-select with a VLESS server selected builds a urltest group of 4 (53-200 ms), with a Hysteria2 server selected a group of 3 (58-172 ms), public IP = picked server, config restored; 600 fast clicks 0 errors, app alive; auto-select off in `config.yaml` afterwards |
| exact-head CI on the stable PR #550 | all checks green |
| full suite on WINBRAT at the merged `d7c39c53` | 3364 passed, 0 failed, 19 skipped |
| tag workflows (macOS, Linux, Android, test, Windows update) | all green |
| draft integrity (`verify-release-integrity.yml`, dispatched at the tag) | success |
| strict commit gate with explicit requirements | `OK`, 8 green |
| `check-open-p0.ps1` | `OK: no open P0/P1` (no waiver needed; VPNCTL-04 was closed in the stable PR) |
| publication integrity, APT, Windows update test on the release event | all success |
| Homebrew tap | event sent, cask updated to 2.50.1 with the hash of the public DMG |
| `post-ship-local.ps1 -Version 2.50.1` | `POSTSHIP-LOCAL: PASS` (2 cold cycles) |
| live scenarios on the installed stable | cycles: connect 3.3-3.7 s, stop 4.1-4.4 s, IP restored every time; Other versions list shows v2.50.1, v2.50.1-r1, v2.50.0 and older; tabs 0 failures; no log findings or crash events |

The slow timings of 2026-10-05 (6-7 s connect for both v2.50.0 and v2.50.1-r1) were the machine or the network: the next morning the installed stable connects in 3.3-3.7 s.

Not verified: Android on a real device (the emulator run is recorded in `plans/phase-core-1.14.2-vpnctl2-2026-10-05.md`), macOS and Linux on real machines, signed Windows files, a real Xray server, an official AmneziaWG peer.
