# Phase — Complete sing-box-vpnctl Migration and Packaging Hardening

**Approval**: Approved 2026-09-05 (MicroSpec: package official `PavelLizunov/sing-box-vpnctl` 1.14.0-vpnctl.3 desktop; Android deferred).
**Branch / Base**: `dsh/upgrade-singbox-vpnctl-1.14` (PR #233) / `origin/main` at `b7ce0e4f`. Reviewed product `318a8bdb3b621052f701649abfe881d5838897ab`, current `bd79` docs-only.
**Combined Verification**: `1fe721a935f95986d60ea9bea3e76a87f4c11adf` on `dsh/verify-vpnctl-night-combined-2026-09-05` (= migration product + NIGHT `ae352fdb` code/tests, NO merge).
**Risk / Rollback**: MEDIUM. Safe task commit revert review (lead reviews clean `git revert`; no automatic rollback).

## Approved MicroSpec

- **Why**: Deprecate legacy `Leadaxe/sing-box-lx` fork across packaging pipelines. Enforce immutable release identity and SHA256 verification.
- **What**: Package official `sing-box-vpnctl` v1.14.0-vpnctl.3 on desktop (Windows/Linux/macOS). Eliminate signing legacy path (`.github/workflows/sign-windows.yml:104-106`) and autoselect fallback (`build.ps1:320-326`), bundle verified `libcronet.dll` (`c7434cfa...`) for Windows Naive construction without enabling new Mac Naive (existing disabled maintained), and migrate legacy FakeIP JSON in `CustomConfigInjector.cs`.
- **How**: Resolve defects VPNCTL-01..03, 05, 06 in PR #233; retain local `-SingBoxPath`; verify multi-platform CI with `upload_to_release=false`.

## Owner Android Deferral (Scope Clarification)

Per owner decision in `ask android-vpnctl-minsdk`, Android v1.14 migration is deprioritized/deferred: minSdk 23 preserved unchanged (no API 23 drop); 4 files restored byte-exact to base `b7ce0e4`. Defect VPNCTL-04 is DEFERRED (unchecked `- [ ]` in ledger; explicit not all P1 closed, not a current desktop blocker). Legacy Android APK build is green. WIP migration preserved on backup branch `dsh/deferred-android-vpnctl-api-2026-09-05` (`f4d5265c`). Historical failure evidence preserved: javac 23 errors in [Run 33988035788](https://github.com/PavelLizunov/VPNRouter/actions/runs/33988035788), minSdk 23 < libbox 24 manifest merge failure in [Run 33990807363](https://github.com/PavelLizunov/VPNRouter/actions/runs/33990807363).

## Final Device Platform Matrix (No Real Device / No Release Claims)

| Platform | Target Core / Asset | Packaging & Verification | CI Run & Status (ALL SUCCESS) | Real Device |
|---|---|---|---|---|
| Windows | `sing-box-1.14.0-vpnctl.3` + `libcronet.dll` | `build.ps1` bundles official core & pinned DLL | [33997229058](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997229058) install/update SUCCESS; tests [33997227017](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997227017) (83 pass 0 skip) | None |
| Linux | `sing-box-1.14.0-vpnctl.3-linux-amd64.tar.gz` | `build-linux.yml` safe non-publishing run | [33997243997](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997243997) SUCCESS (artifact 9978450176) | None |
| macOS | `sing-box-1.14.0-vpnctl.3-darwin-universal.zip` | `build-mac.sh` / `build-mac.yml` non-publishing | [33997246153](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997246153) SUCCESS (artifact 9978440045) | None |
| Android | Legacy tooling `libbox-singbox-1.13.10` / API 23 | `build-android.yml` dotnet publish (legacy green) | [33997248316](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997248316) SUCCESS (artifact 9978474400) | None |

## Multi-Platform CI Evidence & Check Verification

- **Tests Workflow**: [Run 33997227017](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997227017) SUCCESS: Ubuntu 3325 pass, 88 skip (total 3413), Windows 83 pass, 0 skip, Go Pass.
- **Windows Install / Update**: [Run 33997229058](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997229058) SUCCESS: core vpnctl.3 bundled, both DLL pins verified (`c7434cfa93c3041321dd19111c4de6c52b8a9531a65661ba45425d3c51ec69e2`), naive positive 0, negative 1 absent DLL; 4/4 native FakeIP passed. Windows ZIP not retained (only logs CI verification).
- **Artifact Sidecar Integrity**: 6 download sidecar checks all OK (Linux 3, Mac 2, APK 1). Embedded Linux tar core (`973e453dc835ec07b53e950c97eb956fedb436434619e178c5dace0568cde0f6`) and Mac ZIP core (`f5a931da2c0a9f841decc25e9efe61bf052e5dfd706350210d17dae46324f507`) match official release binaries. No direct inside DMG/AppImage compare claimed; verified via sidecars.
- **Historical Witnesses**:
  - Packaging tamper RED witness: [Run 33992362512](https://github.com/PavelLizunov/VPNRouter/actions/runs/33992362512) on `0f85cbfe` (3 Windows FAIL: tampered cache, corrupt zip, implicit LX; now green).
  - FakeIP RED witness: [Run 33996098211](https://github.com/PavelLizunov/VPNRouter/actions/runs/33996098211) on `c36e5034` (original 631-line fixture against unchanged `17004bc4`, genuine assertions; resolved in `318a8bdb`).

## Defect Summary (OPEN-DEFECTS.md)

- `[x]` **VPNCTL-01**: Resolved in PR #233 UNRELEASED. `.github/workflows/sign-windows.yml` uses official release archive (witness `33992362512` fixed, Windows green).
- `[x]` **VPNCTL-02**: Resolved in PR #233 UNRELEASED. `build.ps1` removes silent autoselect fallback to `sing-box-lx.exe` (witness `33992362512` fixed, Windows green).
- `[x]` **VPNCTL-03**: Resolved in PR #233 UNRELEASED. `build.ps1` validates SHA256 integrity on cache hits, preventing tampered zip bundling (witness `33992362512` fixed, Windows green).
- `- [ ]` **VPNCTL-04**: DEFERRED OWNER. Android migration deferred; legacy green; explicit not all P1 closed; not desktop-blocking.
- `[x]` **VPNCTL-05**: Resolved in PR #233 UNRELEASED. Windows packages pinned `libcronet.dll` for Naive construction; verified native 0/1 probes.
- `[x]` **VPNCTL-06**: Resolved in PR #233 UNRELEASED. CustomConfigInjector strips disabled FakeIP and migrates enabled FakeIP with 4/4 native pass.

## Independent Review, Security & README

- **Independent Review (Reviewer `5f067...`)**: All previous 3 findings cleared in source: 1) null keys removed before typed decoding (`4a5b967c`), 2) reserved FakeIP tags actionably rejected without mutation / rename (`1900c96c`), 3) manual migration limitation documented: old meaningful `strategy`, `address_resolver`, `address_strategy`, `client_subnet` rejected actionably (no unsupported semantic conversion). Mac Naive remains existing disabled (not enabled new). No API 23 drop.
- **Security & README**: Bounded review found no additional confirmed security blocker beyond corrected findings (no arbitrary downloads or elevation regressions). See [scoped differential security review](vpnctl-security-review-2026-09-05.md). README checked (actually updated 3 docs): internal core swap preserves user-facing documentation contracts.
- **Guardrails**: No real devices, no PR merge, no main push, no release tags, no signpath calls, no full solution tests claim.

## Verification Gates Checklist (Approved Scope)

- [x] **Gate 1 — Clean build**: Windows, Linux, macOS builds success (warnings allowed) with official vpnctl.3 core; Android legacy green ([33997248316](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997248316)).
- [x] **Gate 2 — Test suite**: Configured CI suites pass with 88 skip explicit scope on combined SHA `1fe721a9` ([33997227017](https://github.com/PavelLizunov/VPNRouter/actions/runs/33997227017): 3325 Ubuntu pass, 88 skip, 83 Windows pass).
- [x] **Gate 3 — Pipeline elimination**: `.github/workflows/sign-windows.yml` no longer invokes Leadaxe `build-singbox-lx.ps1`.
- [x] **Gate 4 — Autoselect elimination**: `build.ps1` no longer auto-selects `publish\sing-box-lx.exe`.
- [x] **Gate 5 — Hash integrity**: Product sidecars verify download integrity, embedded core hashes match official; Win DLL native check.
- [x] **Gate 6 — Safe CI**: Non-publishing CI runs green (`upload_to_release=false`); no PR merge or tag emission. Final gates check done within revised non-live approved scope.

## Appendix: Original Source Pins

- Windows amd64: `8094929df6c4b061dc9c360b1641474d41bdea16845d604a26d3721feefc6f74` (`sing-box-1.14.0-vpnctl.3-windows-amd64.zip`)
- Linux amd64: `3d7fdbbf68f75b74f2bb4451eb2a1ed3421ee3ab6bccfea93f16c0d3eca91e8e` (`sing-box-1.14.0-vpnctl.3-linux-amd64.tar.gz`)
- macOS universal: `c71bf2fab29a00d70f8706eb2f71643e35438769cbbacdd566d7c0e6058be3b1` (`sing-box-1.14.0-vpnctl.3-darwin-universal.zip`)
