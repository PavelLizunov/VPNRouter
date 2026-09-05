# Phase — Complete sing-box-vpnctl Migration and Packaging Hardening

**Approval**: User approved 2026-09-05 (MicroSpec: complete packaging migration to official `PavelLizunov/sing-box-vpnctl` 1.14.0-vpnctl.3)
**Author / Lead**: Gemini (sole code/test/doc author), Lead review Git
**Branch / Base**: `dsh/upgrade-singbox-vpnctl-1.14` (PR #233) / `origin/main` at `b7ce0e4f`
**Integration Note**: PR #240 (`ae352fdb`) contains approved NIGHT fixes not merged; compatibility integrated source verification required
**Current Head**: `be0cf6f7` (last 5 CI checks green; no full platform claim without real device execution)
**Risk**: MEDIUM (packaging pipeline modernization; eliminating legacy third-party fork paths)
**Rollback**: Safe task commit revert review (human/lead review of git revert, no automatic rollback)

## Why

1. Deprecate legacy third-party `Leadaxe/sing-box-lx` fork across all packaging pipelines in favor of official `PavelLizunov/sing-box-vpnctl` v1.14.0-vpnctl.3.
2. Address P1 defect VPNCTL-01: `.github/workflows/sign-windows.yml:104-106` compiles from Leadaxe source (`tools/build-singbox-lx.ps1`) and passes explicit `-SingBoxPath publish/sing-box-lx.exe`, signing a legacy fork instead of official release assets.
3. Address P1 defect VPNCTL-02: `build.ps1:320-326` silently prefers `publish\sing-box-lx.exe` if present, bypassing official release downloads and SHA256 integrity verification.
4. Enforce immutable release identity across Windows, Linux, macOS, and Android while retaining deliberate local `-SingBoxPath` overrides for development.

## What

1. Eliminate signed Windows legacy path in `.github/workflows/sign-windows.yml:104-106` (remove `build-singbox-lx.ps1` call and hardcoded `publish/sing-box-lx.exe`).
2. Update `build.ps1` to remove automatic preference for `publish/sing-box-lx.exe`; retain `-SingBoxPath` custom local override with verified release identity.
3. Standardize on official `PavelLizunov/sing-box-vpnctl` v1.14.0-vpnctl.3 with fixed platform SHA256 hashes across all 4 targets:
   - Windows amd64: `8094929df6c4b061dc9c360b1641474d41bdea16845d604a26d3721feefc6f74` (`sing-box-1.14.0-vpnctl.3-windows-amd64.zip`)
   - Linux amd64: `3d7fdbbf68f75b74f2bb4451eb2a1ed3421ee3ab6bccfea93f16c0d3eca91e8e` (`sing-box-1.14.0-vpnctl.3-linux-amd64.tar.gz`)
   - macOS universal: `c71bf2fab29a00d70f8706eb2f71643e35438769cbbacdd566d7c0e6058be3b1` (`sing-box-1.14.0-vpnctl.3-darwin-universal.zip`)
   - Android AAR: `471908107fb68de65f50cc8898e193b832b2ae12f0dfe9ee93d73f0b27f1a991` (`libbox.aar`)
4. Register P1 defects VPNCTL-01 and VPNCTL-02 in `plans/OPEN-DEFECTS.md`.
5. Run in safe non-publishing mode (`upload_to_release=false`) on existing CI (`build-mac`, `build-linux`, `build-android`). Future agent must inspect gates.
6. Guardrails: no merging protected main, no PR merge, no release publication, no git tags, no real device install, no SDK control plane or infrastructure edits.

## How

1. Register VPNCTL-01 and VPNCTL-02 under `## Open` in `plans/OPEN-DEFECTS.md` with exact source evidence.
2. Refactor `.github/workflows/sign-windows.yml` to package and sign official `sing-box-vpnctl` v1.14.0-vpnctl.3 archive.
3. Refactor `build.ps1` bundling logic to drop automatic `publish\sing-box-lx.exe` fallback while preserving explicit `-SingBoxPath`.
4. Run compatibility integrated source verification against `b7ce0e4f` and PR #240 (`ae352fdb`).
5. Execute non-publishing CI workflows with `upload_to_release=false` and await future agent gate inspection.

## Device Platform Matrix (Packaging & Verification, No Real Device Install)

| Platform | Target Asset | Packaging Mechanism | Real Device Install |
|---|---|---|---|
| Windows | `sing-box-1.14.0-vpnctl.3-windows-amd64.zip` | `build.ps1` bundling official SHA256 archive | None (no real device install) |
| Linux | `sing-box-1.14.0-vpnctl.3-linux-amd64.tar.gz` | `build-linux.yml` safe non-publishing run | None (no real device install) |
| macOS | `sing-box-1.14.0-vpnctl.3-darwin-universal.zip` | `build-mac.sh` / `build-mac.yml` (`upload_to_release=false`) | None (no real device install) |
| Android | `libbox.aar` (v1.14.0-vpnctl.3) | `build-android.yml` Gradle build (`upload_to_release=false`) | None (no real device install) |

## Verification Gates (PENDING - Future Agent Inspection Required)

- [ ] Gate 1 — Clean build: Windows/Linux/macOS/Android builds compile clean against official `sing-box-vpnctl` v1.14.0-vpnctl.3.
- [ ] Gate 2 — Test suite: Unit and packaging contract tests pass with zero failures.
- [ ] Gate 3 — Pipeline elimination: `.github/workflows/sign-windows.yml` no longer invokes Leadaxe `build-singbox-lx.ps1`.
- [ ] Gate 4 — Autoselect elimination: `build.ps1` no longer auto-selects `publish\sing-box-lx.exe` without explicit argument.
- [ ] Gate 5 — Hash integrity: All 4 platform release artifacts match official `v1.14.0-vpnctl.3` SHA256 pins.
- [ ] Gate 6 — Safe CI: Non-publishing CI runs succeed with `upload_to_release=false` on exact head `be0cf6f7` (last 5 checks green).

## Checklist: Rollback & Regression Failure Criteria

- [ ] Rollback trigger: Any packaging failure, checksum mismatch, or unhandled regression in core sing-box-vpnctl 1.14.
- [ ] Rollback protocol: Safe task commit revert review; lead inspects and executes clean `git revert`; no automatic rollback.
- [ ] Regression criteria: Breakage in existing Clash API, v2ray StatsService, DNS ECH suppression, or SCM service wrapping.
- [ ] Artifact match: Windows, Linux, macOS, and Android release artifacts strictly match official SHA256 hashes.
- [ ] Safety invariants: No push to protected `main`, no PR merge, no release tag, no real device install.
