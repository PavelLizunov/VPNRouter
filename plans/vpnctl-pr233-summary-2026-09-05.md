# PR #233: sing-box-vpnctl Migration & Packaging Hardening

## Overview
- **Desktop Core**: Migrates Windows, Linux, and macOS to official `sing-box-vpnctl` **v1.14.0-vpnctl.3** (NOT old `.2`).
- **Android Status**: Retains legacy core 1.13.10 and API 23. Migration is **DEFERRED** per user decision; WIP work is preserved on branch `dsh/deferred-android-vpnctl-api-2026-09-05` (`f4d5265c`).
- **Windows Packaging & Signing**: Signing workflow defaults to official core; removed `build-singbox-lx.ps1` compile step and implicit `publish\sing-box-lx.exe` fallback; enforced strict SHA256 cache pins before extraction.
- **Supplemental Cronet DLL**: Extracted from pinned upstream archive and bundled into both fresh install (`VPNRouter-*.win.zip`) and update (`VPNRouter-update-*.win.zip`) packages; verified via native positive (`sing-box check -c` exit 0) and negative control probes (`cronet: library not found` exit 1).
- **FakeIP Migration**: Disabled legacy FakeIP blocks are removed; enabled configurations migrate to typed `fakeip` servers preserving address pools and IP families; ambiguous options and reserved system tags (`vpnrouter-vpn-dns`, `vpnrouter-dns-direct`) are actionably rejected instead of silently mutated or lost.

## CI & Verification Status
- **PR #233**: Targets current `main` (PR #240 NIGHT fixes `ae352fdb` are not merged). Latest PR #233 checks on `bd79fb1e` are green (product/tests head `318a8bdb`). Updated docs to follow soon; no invented future SHAs.
- **Combined Compatibility Check (Verification-Only)**: Commit `1fe721a935f95986d60ea9bea3e76a87f4c11adf` (integrating PR #233 `318a8bdb` + NIGHT `ae352fdb` code/tests) is for compatibility verification only and is **not automatically merged**:
  - `tests` (run 33997227017): ALL PASS (3,325 passed, 88 skipped on Ubuntu / 83 on Windows).
  - `test-windows-update` (run 33997229058): ALL PASS (4 native FakeIP + Cronet positive/negative controls).
  - `build-linux` (run 33997243997): PASS.
  - `build-mac` (run 33997246153): PASS.
  - `build-android` legacy (run 33997248316): PASS.

## Security Audit & Operational Boundaries
- **Phase Brief**: [plans/phase-complete-vpnctl-migration-2026-09-05.md](https://github.com/PavelLizunov/VPNRouter/blob/dsh/upgrade-singbox-vpnctl-1.14/plans/phase-complete-vpnctl-migration-2026-09-05.md)
- **Security Report**: [plans/vpnctl-security-review-2026-09-05.md](https://github.com/PavelLizunov/VPNRouter/blob/dsh/upgrade-singbox-vpnctl-1.14/plans/vpnctl-security-review-2026-09-05.md) (or [Scoped Differential Security Review](https://github.com/PavelLizunov/VPNRouter/blob/dsh/upgrade-singbox-vpnctl-1.14/plans/vpnctl-security-review-2026-09-05.md)).
- **Explicit Scope Boundaries**: Verification covers binary packaging, configuration validation, and test suites only. Makes no claims regarding real-device installation, release publication, SignPath live signing, or full-solution live VPN handshakes.
