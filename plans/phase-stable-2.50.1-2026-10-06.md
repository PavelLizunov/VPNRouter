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

Pending.
